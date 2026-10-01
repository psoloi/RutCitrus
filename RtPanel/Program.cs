using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.StaticFiles;
using RtPanel.Services;

namespace RtPanel
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // 固定 ContentRoot 为 exe 所在目录，避免双击启动时工作目录不确定导致找不到 wwwroot
            var contentRoot = AppContext.BaseDirectory;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
                WebRootPath = Path.Combine(contentRoot, "wwwroot"),
            });

            // 默认监听所有网卡(IPv4 + IPv6 双栈)，由 appsettings.json 的 Urls 配置(http://*:5096)
            // * 表示任意 IP，Kestrel 会以双栈方式监听，IPv4 与 IPv6 客户端都可访问；
            // 如需只允许本机访问，改为 http://localhost:5096，启动时也可传 --urls=...

            builder.Services.AddRazorPages(options =>
            {
                // 对所有页面启用认证检查（Login 页面通过 [AllowAnonymous] 豁免）
                options.Conventions.AuthorizeFolder("/");
                options.Conventions.AllowAnonymousToPage("/Login");
            });
            builder.Services.AddControllers(options =>
            {
                // 对所有控制器的不安全方法(POST/PUT/DELETE/PATCH)启用防跨站请求伪造校验
                options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
            });

            // 防跨站请求伪造：令牌通过 X-CSRF-TOKEN 请求头传递(前端 fetch 统一注入)
            builder.Services.AddAntiforgery(options =>
            {
                options.HeaderName = "X-CSRF-TOKEN";
                options.Cookie.Name = "RtPanel.CSRF";
                options.Cookie.HttpOnly = true;
            });
            builder.Services.AddSingleton<RtCliClientService>();
            builder.Services.AddSingleton<ConfigSchemaService>();

            // 面板服务端持续采集系统历史(后台采集 + 单例供控制器读取)
            builder.Services.AddSingleton<StatsHistoryService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<StatsHistoryService>());

            // 启用会话（用于存储登录状态）
            builder.Services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromHours(8);
                options.Cookie.HttpOnly = true;
                options.Cookie.Name = "RtPanel.Session";
            });

            // 简单的 cookie 认证方案
            builder.Services.AddAuthentication("RtPanelCookie")
                .AddCookie("RtPanelCookie", options =>
                {
                    options.LoginPath = "/Login";
                    options.AccessDeniedPath = "/Login";
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                });

            builder.Services.AddAuthorization();

            var app = builder.Build();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            // 404 等非异常状态码也渲染 /Error 页面(带 ?code={0} 重执行, 页面内区分 404/500)
            app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

            // 关闭 HTTPS 强制跳转：双击 exe 启动的场景通常没有证书，强制 HTTPS 会导致无法访问
            // 如需 HTTPS，请配置反向代理或在 appsettings 中启用
            // app.UseHttpsRedirection();

            var provider = new FileExtensionContentTypeProvider();
            provider.Mappings[".ico"] = "image/x-icon";
            app.UseStaticFiles(new StaticFileOptions
            {
                ContentTypeProvider = provider
            });

            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseSession();
            app.MapRazorPages();
            app.MapControllers();

            // 启动后自动打开浏览器(仅当非 Development 且未通过命令行禁用时)
            // app.Urls 返回的是通配监听地址(如 http://*:5096)，浏览器无法直接打开，统一换成 localhost
            var listenUrl = app.Urls.FirstOrDefault() ?? "http://*:5096";
            var urls = listenUrl
                .Replace("://*:", "://localhost:")
                .Replace("://+:", "://localhost:")
                .Replace("://0.0.0.0:", "://localhost:")
                .Replace("://[::]:", "://localhost:");
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine();
                Console.WriteLine("================================================");
                Console.WriteLine("注意开启面板前请确保已打开RtCli并配置完毕");
                Console.WriteLine("================================================");
                Console.WriteLine($"  RtPanel 已启动 - 打开浏览器并访问：{urls}");
                if (IsAnyIpBinding(listenUrl))
                {
                    Console.WriteLine("  监听地址：0.0.0.0 / [::]（IPv4 与 IPv6 双栈均可访问）");
                    if (int.TryParse(urls.Split(':').LastOrDefault(), out var port))
                    {
                        foreach (var address in GetAccessAddresses(port))
                            Console.WriteLine($"  局域网/公网访问：{address}");
                    }
                }
                Console.WriteLine("  按 Ctrl+C 关闭面板");
                Console.WriteLine("================================================");
                Console.WriteLine();
                Console.ResetColor();

                if (!app.Environment.IsDevelopment() &&
                    !args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = urls,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // 部分环境(如 Linux 无桌面)无法打开浏览器，忽略错误
                    }
                }
            });

            app.Run();
        }

        /// <summary>判断监听地址是否为"所有网卡"(此时 IPv4 与 IPv6 双栈同时可用)。</summary>
        private static bool IsAnyIpBinding(string url)
        {
            return url.Contains("://*:", StringComparison.Ordinal)
                || url.Contains("://+:", StringComparison.Ordinal)
                || url.Contains("://0.0.0.0:", StringComparison.Ordinal)
                || url.Contains("://[::]:", StringComparison.Ordinal);
        }

        /// <summary>枚举本机可用的 IPv4/IPv6 访问地址，方便其它设备直接访问面板。</summary>
        private static IEnumerable<string> GetAccessAddresses(int port)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var addr in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (addr.AddressFamily != AddressFamily.InterNetwork &&
                        addr.AddressFamily != AddressFamily.InterNetworkV6)
                        continue;
                    // 跳过回环与 IPv6 链路本地地址(带 %scope 的地址不适合直接展示)
                    if (IPAddress.IsLoopback(addr)) continue;
                    if (addr.AddressFamily == AddressFamily.InterNetworkV6 && addr.IsIPv6LinkLocal) continue;

                    string host = addr.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{addr}]" : addr.ToString();
                    string url = $"http://{host}:{port}";
                    if (seen.Add(url)) result.Add(url);
                }
            }
            catch
            {
                // 无法枚举网卡时忽略，不影响面板启动
            }
            return result;
        }
    }
}
