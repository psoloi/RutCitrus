using Microsoft.AspNetCore.Mvc.RazorPages;
using RtPanel.Services;

namespace RtPanel.Pages
{
    /// <summary>事件详细页面: 操作日志时间图(时间概览 + 按部件分组的操作记录)</summary>
    public class EventsModel : PageModel
    {
        private readonly RtCliClientService _client;

        public EventsModel(RtCliClientService client)
        {
            _client = client;
        }

        /// <summary>面板当前是否已连接到 RtCli(仅用于首屏提示, 实际数据由 /api/events/list 提供)</summary>
        public bool Connected { get; private set; }

        public void OnGet()
        {
            Connected = _client.IsConnected;
        }
    }
}
