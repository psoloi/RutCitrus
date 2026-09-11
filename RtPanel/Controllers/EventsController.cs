using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtPanel.Services;

namespace RtPanel.Controllers
{
    /// <summary>事件板: 实例生命周期事件(开启/关闭/崩溃/自动重启/stop无响应)</summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class EventsController : ControllerBase
    {
        private readonly RtCliClientService _client;

        public EventsController(RtCliClientService client)
        {
            _client = client;
        }

        /// <summary>获取事件板事件列表与未读数</summary>
        [HttpGet("list")]
        public async Task<IActionResult> List([FromQuery] int limit = 100)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetEventBoardAsync(limit);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var events = resp.Events.Select(e => new
            {
                id = e.Id,
                eventType = e.EventType,
                instanceId = e.InstanceId,
                instanceName = e.InstanceName,
                title = e.Title,
                detail = e.Detail,
                recordedAt = e.RecordedAt
            }).ToList();

            var unreadCount = events.Count(e => e.id > resp.LastReadId);
            return Ok(new { success = true, events, lastReadId = resp.LastReadId, unreadCount });
        }

        /// <summary>标记事件板全部已读</summary>
        [HttpPost("read")]
        public async Task<IActionResult> MarkRead()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.MarkEventBoardReadAsync();
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            return Ok(new { success = resp.Success, message = resp.Message });
        }

        /// <summary>清空事件板</summary>
        [HttpPost("clear")]
        public async Task<IActionResult> Clear()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.ClearEventBoardAsync();
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });
            return Ok(new { success = resp.Success, message = resp.Message });
        }
    }
}
