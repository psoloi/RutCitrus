using Microsoft.AspNetCore.Mvc;
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

            // 默认监听端口由 appsettings.json 的 Kestrel:Endpoints 配置(http://localhost:5096)
            // 如需修改端口，编辑 appsettings.json 或启动时传 --urls=http://localhost:其它端口

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
            var urls = app.Urls.FirstOrDefault() ?? "http://localhost:5096";
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine();
                Console.WriteLine("================================================");
                Console.WriteLine("注意开启面板前请确保已打开RtCli并配置完毕");
                Console.WriteLine("================================================");
                Console.WriteLine($"  RtPanel 已启动 - 打开浏览器并访问：{urls}");
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
    }
}
