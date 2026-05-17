using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtPanel.Services;

namespace RtPanel.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ConfigController : ControllerBase
    {
        private readonly RtCliClientService _client;

        public ConfigController(RtCliClientService client)
        {
            _client = client;
        }

        /// <summary>
        /// 获取配置文件内容
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetConfig()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, content = "", message = "未连接到服务器" });

            var response = await _client.GetConfigAsync();
            if (response == null)
                return Ok(new { success = false, content = "", message = "请求失败" });

            return Ok(new { response.Success, response.Content, response.Message });
        }

        /// <summary>
        /// 保存配置文件内容并热重载
        /// </summary>
        [HttpPost("save")]
        public async Task<IActionResult> SaveConfig([FromBody] SaveConfigRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var response = await _client.SaveConfigAsync(request.Content);
            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { response.Success, response.Message });
        }
    }

    public class SaveConfigRequest
    {
        public string Content { get; set; } = "";
    }
}
