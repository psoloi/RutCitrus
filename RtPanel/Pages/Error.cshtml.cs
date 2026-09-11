using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Diagnostics;

namespace RtPanel.Pages
{
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [IgnoreAntiforgeryToken]
    public class ErrorModel : PageModel
    {
        public string? RequestId { get; set; }

        public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

        /// <summary>HTTP 状态码(404/500等, 异常路径默认 500)</summary>
        public int HttpStatusCode { get; private set; }

        /// <summary>是否为页面未找到(404)</summary>
        public bool IsNotFound => HttpStatusCode == 404;

        /// <summary>触发错误时请求的原始路径(仅 404 重执行时可用)</summary>
        public string? OriginalPath { get; private set; }

        private readonly ILogger<ErrorModel> _logger;

        public ErrorModel(ILogger<ErrorModel> logger)
        {
            _logger = logger;
        }

        public void OnGet()
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

            // UseStatusCodePagesWithReExecute 会带 ?code={0} 重执行本页
            if (int.TryParse(Request.Query["code"].FirstOrDefault(), out var code))
                HttpStatusCode = code;

            // 异常处理路径默认按 500 展示
            if (HttpStatusCode == 0)
                HttpStatusCode = 500;

            // 原始请求路径(由状态码页中间件通过 Items 传递)
            OriginalPath = HttpContext.Items["originalPath"] as string
                ?? (HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodeReExecuteFeature>()?.OriginalPath);
        }
    }
}
