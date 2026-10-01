using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    /// <summary>
    /// 扩展/插件 DLL 加载安全校验器。
    /// 职责:
    /// 1. 强制加载路径必须位于程序根目录内(路径逃逸防护);
    /// 2. 加载前校验文件类型/大小/是否为合法托管程序集;
    /// 3. 数字签名认证: 已签名的文件必须证书链校验通过, 未签名默认放行(可开启强制要求);
    /// 4. 拦截伪装宿主/框架程序集名称的 DLL;
    /// 5. 校验同名 .deps.json, 若声明了可执行文件(.exe)则拦截。
    /// 所有校验失败仅返回原因, 不抛异常, 由调用方决定是否记日志/取消加载。
    /// </summary>
    internal static class Validator
    {
        /// <summary>程序根目录(运行时基目录), 所有扩展程序集与依赖的加载路径必须位于其中。</summary>
        public static readonly string ProgramRoot = NormalizeDirectory(AppContext.BaseDirectory);

        /// <summary>单个扩展文件大小上限(64MB), 防止异常大文件拖垮加载流程。</summary>
        public const long MaxAssemblySizeBytes = 64L * 1024 * 1024;

        /// <summary>
        /// 是否强制要求扩展已数字签名(默认 false)。
        /// false: 未签名放行(仅记录警告), 已签名但校验失败仍拦截;
        /// true : 未签名直接拒绝加载。
        /// </summary>
        public static bool RequireSignature { get; set; } = false;

        /// <summary>
        /// 受信任发布者指纹白名单(SHA256, 十六进制, 不区分大小写)。
        /// 非空时, 已签名扩展的签名证书指纹必须命中白名单; 未签名扩展仍由 RequireSignature 决定。
        /// </summary>
        public static HashSet<string> TrustedPublisherThumbprints { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>被禁止的程序集名称, 防止用同名 DLL 伪装/替换宿主或框架程序集。</summary>
        private static readonly HashSet<string> BlockedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "RtCli", "RtPanel", "mscorlib", "netstandard"
        };

        /// <summary>单个扩展允许占用的托管堆内存上限(256MB)。</summary>
        public const long MaxExtensionMemoryBytes = 256L * 1024 * 1024;

        /// <summary>超限需要持续的时长(2秒): 短暂峰值(如解析大文件)不触发卸载。</summary>
        public static readonly TimeSpan ExtensionMemorySustainDuration = TimeSpan.FromSeconds(2);

        /// <summary>内存守护采样间隔(250ms)</summary>
        public static readonly TimeSpan ExtensionMemorySampleInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// 扩展内存超限状态机(每个扩展一个实例)。
        ///
        /// 语义: 占用超过上限只开始计时, 不立即卸载; 只有**连续**超限达到 SustainDuration
        /// 才判定"内存失控"(返回 true)。期间任意一次回落到上限内都会清零计时,
        /// 因此大文件解析之类的瞬时峰值不会误杀扩展。
        /// </summary>
        public sealed class ExtensionMemoryWatch
        {
            private DateTime? _exceededSince;

            public ExtensionMemoryWatch(long limitBytes = MaxExtensionMemoryBytes,
                                        TimeSpan? sustainDuration = null)
            {
                LimitBytes = limitBytes;
                SustainDuration = sustainDuration ?? ExtensionMemorySustainDuration;
            }

            /// <summary>内存上限(字节)</summary>
            public long LimitBytes { get; }

            /// <summary>需要持续的时长</summary>
            public TimeSpan SustainDuration { get; }

            /// <summary>观测到的峰值占用(字节), 供日志/面板展示</summary>
            public long PeakBytes { get; private set; }

            /// <summary>超限起始时刻(null 表示当前未超限)</summary>
            public DateTime? ExceededSince => _exceededSince;

            /// <summary>
            /// 喂入一次采样。
            /// </summary>
            /// <param name="usageBytes">当前占用(字节)</param>
            /// <param name="now">采样时刻</param>
            /// <param name="exceededFor">已连续超限时长(未超限为 Zero)</param>
            /// <returns>true 表示应强制卸载</returns>
            public bool Sample(long usageBytes, DateTime now, out TimeSpan exceededFor)
            {
                exceededFor = TimeSpan.Zero;
                if (usageBytes > PeakBytes) PeakBytes = usageBytes;

                if (usageBytes <= LimitBytes)
                {
                    _exceededSince = null;
                    return false;
                }

                _exceededSince ??= now;
                exceededFor = now - _exceededSince.Value;
                return exceededFor >= SustainDuration;
            }

            /// <summary>清零(卸载完成或重新开始观测时调用)</summary>
            public void Reset()
            {
                _exceededSince = null;
                PeakBytes = 0;
            }
        }

        // ==================== 路径安全 ====================

        /// <summary>判断路径是否位于程序根目录内(已解析真实绝对路径)。</summary>
        public static bool IsWithinProgramRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                string root = ProgramRoot + Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), ProgramRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string NormalizeDirectory(string dir)
        {
            try { return Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return dir; }
        }

        // ==================== 扩展加载前校验 ====================

        /// <summary>
        /// 校验扩展 DLL 是否允许加载。
        /// </summary>
        /// <param name="assemblyPath">扩展程序集路径</param>
        /// <param name="reason">不允许加载时的原因</param>
        /// <returns>true=允许加载</returns>
        public static bool ValidateExtension(string assemblyPath, out string reason)
        {
            reason = "";
            try
            {
                if (string.IsNullOrWhiteSpace(assemblyPath)) { reason = "扩展路径为空"; return false; }
                if (!assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { reason = "扩展文件必须是 .dll 格式"; return false; }
                if (!File.Exists(assemblyPath)) { reason = "扩展文件不存在"; return false; }

                string full = Path.GetFullPath(assemblyPath);

                // 1. 路径必须位于程序根目录内
                if (!IsWithinProgramRoot(full))
                {
                    reason = $"加载路径越出程序根目录: {full}";
                    return false;
                }

                // 2. 文件大小校验
                long size = new FileInfo(full).Length;
                if (size <= 0) { reason = "扩展文件为空"; return false; }
                if (size > MaxAssemblySizeBytes)
                {
                    reason = $"扩展文件过大({size / 1024 / 1024}MB), 超过上限 {MaxAssemblySizeBytes / 1024 / 1024}MB";
                    return false;
                }

                // 3. 数字签名认证
                if (!ValidateSignature(full, out string sigReason)) { reason = sigReason; return false; }

                // 4. 托管程序集名称校验(拦截伪装宿主/框架的同名 DLL)
                var (managed, asmName) = InspectAssemblyName(full);
                if (!managed) { reason = "非有效的托管程序集(可能为原生 DLL)"; return false; }
                if (!string.IsNullOrEmpty(asmName) && IsBlockedAssemblyName(asmName))
                {
                    reason = $"程序集名称被禁止: {asmName}(疑似伪装宿主/框架程序集)";
                    return false;
                }

                // 5. 同名 .deps.json 可执行文件拦截
                if (DepsJsonHasExecutable(full, out string exeName))
                {
                    reason = $".deps.json 声明了可执行文件({exeName}), 已拦截";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = $"安全校验异常: {ex.Message}";
                return false;
            }
        }

        // ==================== 数字签名 ====================

        /// <summary>
        /// Authenticode 数字签名校验。
        /// 已签名 → 校验证书链, 失败则拒绝; 未签名 → 由 RequireSignature 决定;
        /// 非 Windows 平台不支持 Authenticode, 跳过签名校验(其余校验仍生效)。
        /// </summary>
        public static bool ValidateSignature(string file, out string reason)
        {
            reason = "";
            if (!OperatingSystem.IsWindows())
                return true;

            try
            {
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);

                if (!chain.Build(cert))
                {
                    reason = "数字签名无效(证书链校验失败)";
                    return false;
                }

                // 必须是代码签名证书(EKU: 1.3.6.1.5.5.7.3.3), 排除普通 TLS/客户端证书签名的 DLL
                bool hasCodeSigningEku = cert.Extensions
                    .OfType<X509EnhancedKeyUsageExtension>()
                    .Any(eku => eku.EnhancedKeyUsages.Cast<Oid>()
                        .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3"));
                if (!hasCodeSigningEku)
                {
                    reason = "签名证书不是代码签名证书(缺少 Code Signing EKU)";
                    return false;
                }

                // 发布者白名单: 配置后只信任明确列出的证书指纹
                if (TrustedPublisherThumbprints.Count > 0)
                {
                    string thumbprint = cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
                    if (!TrustedPublisherThumbprints.Contains(thumbprint))
                    {
                        reason = $"签名证书未被信任(指纹 {thumbprint} 不在发布者白名单)";
                        return false;
                    }
                }

                return true;
            }
            catch (CryptographicException)
            {
                // 文件未签名
                if (RequireSignature)
                {
                    reason = "扩展未数字签名, 已按策略拒绝加载";
                    return false;
                }
                Output.Debug("扩展未数字签名, 按当前策略(RequireSignature=false)放行", "Validator");
                return true;
            }
            catch (Exception ex)
            {
                // 签名校验本身异常(如系统不支持): 默认不因此拦截, 交由其他校验兜底
                if (RequireSignature)
                {
                    reason = $"签名校验失败: {ex.Message}";
                    return false;
                }
                return true;
            }
        }

        // ==================== 程序集名称 ====================

        private static (bool Managed, string? Name) InspectAssemblyName(string path)
        {
            try { return (true, AssemblyName.GetAssemblyName(path).Name); }
            catch (BadImageFormatException) { return (false, null); }
            catch (FileLoadException) { return (false, null); }
            catch { return (false, null); }
        }

        private static bool IsBlockedAssemblyName(string name)
            => BlockedAssemblyNames.Contains(name)
               || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);

        // ==================== .deps.json 校验 ====================

        /// <summary>检查同名 deps.json 中是否声明了可执行文件(.exe)。</summary>
        public static bool DepsJsonHasExecutable(string dllPath, out string exeName)
        {
            exeName = "";
            try
            {
                string depsPath = Path.ChangeExtension(dllPath, ".deps.json");
                if (!File.Exists(depsPath)) return false;

                using var doc = JsonDocument.Parse(File.ReadAllText(depsPath));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                if (!doc.RootElement.TryGetProperty("targets", out var targets)) return false;
                if (targets.ValueKind != JsonValueKind.Object) return false;

                string[] assetGroups = { "runtime", "native", "runtimeTargets", "resources" };
                foreach (var target in targets.EnumerateObject())
                {
                    if (target.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var lib in target.Value.EnumerateObject())
                    {
                        if (lib.Value.ValueKind != JsonValueKind.Object) continue;
                        foreach (var group in assetGroups)
                        {
                            if (!lib.Value.TryGetProperty(group, out var assets) || assets.ValueKind != JsonValueKind.Object) continue;
                            foreach (var asset in assets.EnumerateObject())
                            {
                                if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    exeName = asset.Name;
                                    return true;
                                }
                            }
                        }
                    }
                }
                return false;
            }
            catch { return false; }
        }
    }

    internal class Optimizer()
    {
        public static void Container()
        {
        }
        public static void Reactor()
        {
        }
    }
}