using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using RutCitrusWeb.Hubs;

namespace RutCitrusWeb.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class KickController : ControllerBase
    {
        private readonly IHubContext<NotificationHub> _hubContext;

        public KickController(IHubContext<NotificationHub> hubContext)
        {
            _hubContext = hubContext;
        }

        [HttpPost]
        public async Task<IActionResult> Post([FromBody] KickRequest request)
        {
            try
            {
                // 向被踢出用户发送通知
                await _hubContext.Clients.Client(request.ComputerName).SendAsync("Kicked", request.Reason);

                // 清除用户的会话
                // 这里需要实现您的会话管理逻辑

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = ex.Message });
            }
        }
    }

    public class KickRequest
    {
        public string ComputerName { get; set; }
        public string IP { get; set; }
        public string Reason { get; set; }
    }
} 