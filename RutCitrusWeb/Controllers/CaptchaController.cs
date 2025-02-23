using Microsoft.AspNetCore.Mvc;
using System.Drawing;
using System.IO;
using System.Drawing.Imaging;

namespace RutCitrusWeb.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CaptchaController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get()
        {
            var captcha = GenerateCaptcha();
            HttpContext.Session.SetString("Captcha", captcha);

            using var bitmap = new Bitmap(120, 40);
            using var graphics = Graphics.FromImage(bitmap);
            
            // 设置背景
            graphics.Clear(Color.White);

            // 添加干扰线
            using var pen = new Pen(Color.LightGray);
            for (int i = 0; i < 5; i++)
            {
                var x1 = Random.Shared.Next(0, 120);
                var y1 = Random.Shared.Next(0, 40);
                var x2 = Random.Shared.Next(0, 120);
                var y2 = Random.Shared.Next(0, 40);
                graphics.DrawLine(pen, x1, y1, x2, y2);
            }

            // 绘制验证码
            using var font = new Font("Arial", 20, FontStyle.Bold);
            using var brush = new SolidBrush(Color.FromArgb(Random.Shared.Next(0, 100), 
                                                           Random.Shared.Next(0, 100), 
                                                           Random.Shared.Next(0, 100)));
            graphics.DrawString(captcha, font, brush, 10, 5);

            // 转换为图片流
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return File(ms.ToArray(), "image/png");
        }

        private string GenerateCaptcha()
        {
            const string chars = "2345678ABCDEFGHJKLMNPQRSTUVWXYZ";
            return new string(Enumerable.Repeat(chars, 4)
                .Select(s => s[Random.Shared.Next(s.Length)]).ToArray());
        }
    }
} 