using Microsoft.AspNetCore.StaticFiles;
using RtPanel.Services;

namespace RtPanel
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddRazorPages(options =>
            {
                // 对所有页面启用认证检查（Login 页面通过 [AllowAnonymous] 豁免）
                options.Conventions.AuthorizeFolder("/");
                options.Conventions.AllowAnonymousToPage("/Login");
            });
            builder.Services.AddControllers();
            builder.Services.AddSingleton<RtCliClientService>();

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

            app.UseHttpsRedirection();

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

            app.Run();
        }
    }
}
