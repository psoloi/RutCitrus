using RtCli.Modules;
using RtCli.Modules.Extension;
using RtCli.Modules.Function;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;
using Rt.Common;
using Rt.Core;

namespace Rt
{
    public class ExampleExtension : ExtensionBase
    {
        // Core里面放主功能，Com则是模块

        public override string Name => "Rt";
        public override string Version => "1.7.0";
        public override string Description => "RtCli扩展插件，扩展了MC服务器安全方面的功能";

        private bool _isLoaded = false;
        // NetworkMonitor 是单例(NetworkMonitor.Instance)，无需字段
        private static PacketsLimitMonitor? _packetsLimitMonitor;
        private static AntibotMonitor? _antibotMonitor;

        public override void Load()
        {
            if (_isLoaded)
            {
                Output.Log("Rt扩展已经加载过了", 2, Name);
                return;
            }

            RegisterAssemblyResolver();

            Output.Log("Rt扩展正在加载...", 1, Name);

            // 生成/加载 rt_config.yml 配置文件
            RtConfig.EnsureAndLoad();

            SubscribeEvent<CommandExecuteEvent>(OnCommandExecute);

            // rte - 显示版本信息
            CommandRegistry.RegisterCommand("rte", args =>
            {
                Output.Log($"[green]Rt版本：{Version} 输入rte help获取帮助[/]", 1, "Rt");
            }, "Rt扩展命令");

            // rte help - 帮助
            CommandRegistry.RegisterCommand("rte help", args =>
            {
                Output.Log("[green]Rt扩展命令列表[/]", 1, "Rt");
                Output.Log("[grey]── 网络监测器(全局抓包核心) ──[/]", 1, "Rt");
                Output.Log("  [cyan]rte monitor status[/]      查看网络监测器状态(包数/流量/接口)", 1, "Rt");
                Output.Log("  [cyan]rte monitor start[/]       启动网络监测器(开始抓包)", 1, "Rt");
                Output.Log("  [cyan]rte monitor stop[/]        停止网络监测器", 1, "Rt");
                Output.Log("  [cyan]rte monitor debug[/]       显示5秒内捕获的数据包详细信息", 1, "Rt");
                Output.Log("  [cyan]rte monitor diag[/]        诊断: 接口列表/配置检查/权限/Npcap", 1, "Rt");
                Output.Log("[grey]── 发包频率限制子功能 ──[/]", 1, "Rt");
                Output.Log("  [cyan]rte packetslimit status[/] 查看发包频率限制状态", 1, "Rt");
                Output.Log("  [cyan]rte packetslimit start[/]  启动发包频率限制(需先启动monitor)", 1, "Rt");
                Output.Log("  [cyan]rte packetslimit stop[/]   停止发包频率限制", 1, "Rt");
                Output.Log("[grey]── 反机器人流量监测子功能 ──[/]", 1, "Rt");
                Output.Log("  [cyan]rte antibot status[/]      查看反机器人监测状态", 1, "Rt");
                Output.Log("  [cyan]rte antibot start[/]       启动反机器人监测(需先启动monitor)", 1, "Rt");
                Output.Log("  [cyan]rte antibot stop[/]        停止反机器人监测", 1, "Rt");
                Output.Log("  [cyan]rte antibot notify[/]      切换流量通知(每15s显示流量统计)", 1, "Rt");
                Output.Log("  [cyan]rte antibot verbose[/]     切换详细数据包日志", 1, "Rt");
                Output.Log("[grey]── 其他 ──[/]", 1, "Rt");
                Output.Log("  [cyan]rte get [[host:port]][/]   查询MC服务器状态(Server List Ping协议)", 1, "Rt");
                Output.Log("  [cyan]rte reload[/]              热重载 rt_config.yml 配置", 1, "Rt");
            }, "Rt扩展命令帮助");

            // rte get - MC服务器状态查询
            CommandRegistry.RegisterCommand("rte get", args =>
            {
                McStatusQuery.Execute(args);
            }, "Rt扩展获取Minecraft服务器状态信息");

            // ============================================================
            // 网络监测器命令(全局抓包核心)
            // ============================================================

            // rte monitor status - 查看网络监测器状态
            CommandRegistry.RegisterCommand("rte monitor status", args =>
            {
                NetworkMonitor.Instance.ShowStatus();
            }, "查看网络监测器状态");

            // rte monitor start - 启动网络监测器
            CommandRegistry.RegisterCommand("rte monitor start", args =>
            {
                var nm = NetworkMonitor.Instance;
                if (nm.IsRunning)
                {
                    Output.Log("网络监测器已在运行", 2, "Rt");
                    return;
                }
                var cfg = RtConfig.Current.NetworkMonitor;
                nm.Start(cfg.Interface, cfg.ServerIp, cfg.ServerPort);
            }, "启动网络监测器");

            // rte monitor stop - 停止网络监测器
            CommandRegistry.RegisterCommand("rte monitor stop", args =>
            {
                var nm = NetworkMonitor.Instance;
                if (!nm.IsRunning)
                {
                    Output.Log("网络监测器未运行", 2, "Rt");
                    return;
                }
                // 停止 monitor 时，子功能失去数据源，建议一并停止
                _packetsLimitMonitor?.Stop();
                _antibotMonitor?.Stop();
                nm.Stop();
            }, "停止网络监测器");

            // rte monitor debug - 显示5秒内捕获的数据包详细信息
            CommandRegistry.RegisterCommand("rte monitor debug", args =>
            {
                NetworkMonitor.Instance.ShowDebugSnapshot();
            }, "显示网络监测器Debug快照(5秒内捕获的数据包详情)");

            // rte monitor diag - 诊断信息
            CommandRegistry.RegisterCommand("rte monitor diag", args =>
            {
                NetworkMonitor.Instance.ShowDiagnostics();
            }, "显示网络监测器诊断信息(接口列表/配置检查/权限/Npcap)");

            // ============================================================
            // 发包频率限制子功能命令
            // ============================================================

            // rte packetslimit status - 查看发包限制状态
            CommandRegistry.RegisterCommand("rte packetslimit status", args =>
            {
                _packetsLimitMonitor?.ShowStatus();
                if (_packetsLimitMonitor == null)
                    Output.Log("发包限制子功能尚未初始化", 2, "Rt");
            }, "查看发包频率限制状态");

            // rte packetslimit start - 启动发包限制
            CommandRegistry.RegisterCommand("rte packetslimit start", args =>
            {
                if (_packetsLimitMonitor == null)
                    _packetsLimitMonitor = new PacketsLimitMonitor();

                if (_packetsLimitMonitor.IsRunning)
                {
                    Output.Log("发包限制子功能已在运行", 2, "Rt");
                    return;
                }
                _packetsLimitMonitor.Start();
            }, "启动发包频率限制子功能");

            // rte packetslimit stop - 停止发包限制
            CommandRegistry.RegisterCommand("rte packetslimit stop", args =>
            {
                if (_packetsLimitMonitor == null || !_packetsLimitMonitor.IsRunning)
                {
                    Output.Log("发包限制子功能未运行", 2, "Rt");
                    return;
                }
                _packetsLimitMonitor.Stop();
            }, "停止发包频率限制子功能");

            // ============================================================
            // 反机器人流量监测子功能命令
            // ============================================================

            // rte antibot status - 查看反机器人监测状态
            CommandRegistry.RegisterCommand("rte antibot status", args =>
            {
                _antibotMonitor?.ShowStatus();
                if (_antibotMonitor == null)
                    Output.Log("反机器人监测器尚未初始化", 2, "Rt");
            }, "查看反机器人流量监测状态");

            // rte antibot start - 启动反机器人监测器
            CommandRegistry.RegisterCommand("rte antibot start", args =>
            {
                if (_antibotMonitor == null)
                    _antibotMonitor = new AntibotMonitor();

                if (_antibotMonitor.IsRunning)
                {
                    Output.Log("反机器人监测器已在运行", 2, "Rt");
                    return;
                }
                _antibotMonitor.Start();
            }, "启动反机器人流量监测子功能");

            // rte antibot stop - 停止反机器人监测器
            CommandRegistry.RegisterCommand("rte antibot stop", args =>
            {
                if (_antibotMonitor == null || !_antibotMonitor.IsRunning)
                {
                    Output.Log("反机器人监测器未运行", 2, "Rt");
                    return;
                }
                _antibotMonitor.Stop();
            }, "停止反机器人流量监测子功能");

            // rte antibot notify - 切换流量通知开关
            CommandRegistry.RegisterCommand("rte antibot notify", args =>
            {
                if (_antibotMonitor == null || !_antibotMonitor.IsRunning)
                {
                    Output.Log("反机器人监测器未运行", 2, "Rt");
                    return;
                }
                _antibotMonitor.ToggleNotify();
            }, "切换反机器人流量通知(每15s显示当前流量与攻击告警)");

            // rte antibot verbose - 切换详细数据包日志
            CommandRegistry.RegisterCommand("rte antibot verbose", args =>
            {
                if (_antibotMonitor == null || !_antibotMonitor.IsRunning)
                {
                    Output.Log("反机器人监测器未运行", 2, "Rt");
                    return;
                }
                _antibotMonitor.ToggleVerbose();
            }, "切换详细数据包日志(显示每个包的IP/方向/大小)");

            // rte reload - 热重载配置
            CommandRegistry.RegisterCommand("rte reload", args =>
            {
                RtConfig.Reload();
                Output.Log("Rt扩展配置已重新加载", 1, "Rt");
            }, "热重载Rt扩展配置文件");

            // ============================================================
            // 自动启动逻辑
            // ============================================================

            // 1) 若 init_network_monitor=true，自动启动抓包核心
            if (RtConfig.Current.InitNetworkMonitor)
            {
                var nmCfg = RtConfig.Current.NetworkMonitor;
                Output.Log("[grey]配置 init_network_monitor=true，自动启动网络监测器...[/]", 1, "Rt");
                NetworkMonitor.Instance.Start(nmCfg.Interface, nmCfg.ServerIp, nmCfg.ServerPort);
            }

            // 2) 若 packets_limit.enabled=true，自动启动发包限制子功能(需 NetworkMonitor 已运行)
            if (RtConfig.Current.PacketsLimit.Enabled)
            {
                _packetsLimitMonitor = new PacketsLimitMonitor();
                _packetsLimitMonitor.Start();
            }

            // 3) 若 antibot.enabled=true，自动启动反机器人监测子功能(需 NetworkMonitor 已运行)
            if (RtConfig.Current.Antibot.Enabled)
            {
                _antibotMonitor = new AntibotMonitor();
                _antibotMonitor.Start();
            }

            _isLoaded = true;
            Output.Log("Rt扩展加载完成", 1, Name);
        }

        private void OnCommandExecute(CommandExecuteEvent e)
        {
            //Output.Log($"[green]命令执行[/]: {e.Command}", 1, Name);
        }

        public override void Run()
        {
            if (!_isLoaded)
            {
                Output.Log("Rt扩展未加载，无法运行", 3, Name);
                return;
            }

            Output.Log("Rt扩展正在运行...", 1, Name);
        }

        public override void Unload()
        {
            if (!_isLoaded)
            {
                Output.Log("Rt扩展未加载，无法卸载", 2, Name);
                return;
            }

            Output.Log("Rt扩展正在卸载...", 1, Name);

            // 先停止子功能(它们订阅 NetworkMonitor 事件)
            _packetsLimitMonitor?.Stop();
            _packetsLimitMonitor = null;

            _antibotMonitor?.Stop();
            _antibotMonitor = null;

            // 再停止 NetworkMonitor
            NetworkMonitor.Instance.Stop();

            CommandRegistry.UnregisterCommand("rte");
            CommandRegistry.UnregisterCommand("rte help");
            CommandRegistry.UnregisterCommand("rte get");
            CommandRegistry.UnregisterCommand("rte monitor status");
            CommandRegistry.UnregisterCommand("rte monitor start");
            CommandRegistry.UnregisterCommand("rte monitor stop");
            CommandRegistry.UnregisterCommand("rte monitor debug");
            CommandRegistry.UnregisterCommand("rte monitor diag");
            CommandRegistry.UnregisterCommand("rte packetslimit status");
            CommandRegistry.UnregisterCommand("rte packetslimit start");
            CommandRegistry.UnregisterCommand("rte packetslimit stop");
            CommandRegistry.UnregisterCommand("rte antibot status");
            CommandRegistry.UnregisterCommand("rte antibot start");
            CommandRegistry.UnregisterCommand("rte antibot stop");
            CommandRegistry.UnregisterCommand("rte antibot notify");
            CommandRegistry.UnregisterCommand("rte antibot verbose");
            CommandRegistry.UnregisterCommand("rte reload");

            base.Unload();

            _isLoaded = false;
            Output.Log("Rt扩展卸载完成", 1, Name);
        }




        private static bool _resolverRegistered = false;
        private static readonly Dictionary<string, byte[]> _loadedAssemblies = new();

        private static void RegisterAssemblyResolver()
        {
            if (_resolverRegistered)
                return;

            var currentContext = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
            if (currentContext == null)
            {
                Output.Log("扩展缺失AssemblyLoadContext", 3, "Rt");
                return;
            }

            currentContext.Resolving += (context, assemblyName) =>
            {
                string resourceName = assemblyName.Name + ".dll";

                if (_loadedAssemblies.TryGetValue(assemblyName.Name!, out byte[]? assemblyBytes))
                {
                    using var stream = new MemoryStream(assemblyBytes);
                    return context.LoadFromStream(stream);
                }

                Assembly asm = Assembly.GetExecutingAssembly();
                string[] resources = asm.GetManifestResourceNames();

                var matchingResource = resources.FirstOrDefault(r =>
                    r.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase) ||
                    r == resourceName ||
                    r.EndsWith("." + resourceName, StringComparison.OrdinalIgnoreCase));

                if (matchingResource != null)
                {
                    using Stream? resourceStream = asm.GetManifestResourceStream(matchingResource);
                    if (resourceStream != null)
                    {
                        byte[] bytes = new byte[resourceStream.Length];
                        resourceStream.Read(bytes, 0, bytes.Length);

                        _loadedAssemblies[assemblyName.Name!] = bytes;

                        using var memStream = new MemoryStream(bytes);
                        return context.LoadFromStream(memStream);
                    }
                }

                // 框架程序集回退：从运行时目录加载(版本兼容)
                // 当宿主RtCli较旧未内置此逻辑时，扩展自行解决System.*版本不匹配问题
                string? fwName = assemblyName.Name;
                if (!string.IsNullOrEmpty(fwName) &&
                    (fwName.StartsWith("System.") || fwName.StartsWith("Microsoft.") ||
                     fwName.StartsWith("netstandard")))
                {
                    try
                    {
                        // 先检查是否已在任何上下文中加载(任意版本)
                        var loaded = AppDomain.CurrentDomain.GetAssemblies()
                            .FirstOrDefault(a => a.GetName().Name == fwName);
                        if (loaded != null)
                            return loaded;

                        // 从.NET运行时目录加载对应DLL
                        string? runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
                        if (!string.IsNullOrEmpty(runtimeDir))
                        {
                            string dllPath = System.IO.Path.Combine(runtimeDir, fwName + ".dll");
                            if (System.IO.File.Exists(dllPath))
                                return Assembly.LoadFrom(dllPath);
                        }
                    }
                    catch { }
                }

                return null;
            };

            _resolverRegistered = true;
        }
    }
}
