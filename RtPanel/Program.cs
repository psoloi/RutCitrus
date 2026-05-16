using Microsoft.AspNetCore.StaticFiles;
using RtPanel.Services;

namespace RtPanel
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddRazorPages();
            builder.Services.AddControllers();
            builder.Services.AddSingleton<RtCliClientService>();

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
            app.UseAuthorization();
            app.MapRazorPages();
            app.MapControllers();

            app.Run();
        }
    }
}
