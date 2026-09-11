using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RtPanel.Services;

namespace RtPanel.Pages
{
    public class PrivacyModel : PageModel
    {
        private readonly RtCliClientService _client;

        public PrivacyModel(RtCliClientService client)
        {
            _client = client;
        }

        /// <summary>RtPanel 自身版本号</summary>
        public string PanelVersion =>
            typeof(PrivacyModel).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        /// <summary>已连接 RtCli 的版本号(未连接时为空)</summary>
        public string? RtCliVersion => _client.ConnectedRtCliVersion;

        public void OnGet()
        {
        }
    }
}
