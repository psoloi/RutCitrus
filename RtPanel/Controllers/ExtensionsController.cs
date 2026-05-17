using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtPanel.Services;

namespace RtPanel.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ExtensionsController : ControllerBase
    {
        private readonly RtCliClientService _client;

        public ExtensionsController(RtCliClientService client)
        {
            _client = client;
        }

        [HttpGet]
        public async Task<IActionResult> GetExtensions()
        {
            if (!_client.IsConnected)
            {
                return Ok(new List<object>());
            }

            var response = await _client.GetExtensionsAsync();
            if (response == null)
            {
                return Ok(new List<object>());
            }

            var result = response.Extensions.Select(e => new
            {
                e.Key,
                e.Name,
                e.Version,
                e.Description,
                e.LoadTime
            });

            return Ok(result);
        }

        [HttpPost("unload")]
        public async Task<IActionResult> UnloadExtension([FromBody] UnloadRequest request)
        {
            if (!_client.IsConnected)
            {
                return Ok(new { success = false, message = "未连接到服务器" });
            }

            var response = await _client.UnloadExtensionAsync(request.ExtensionKey);
            if (response == null)
            {
                return Ok(new { success = false, message = "请求失败" });
            }

            return Ok(new { response.Success, response.Message });
        }

        [HttpPost("load")]
        public async Task<IActionResult> LoadExtension([FromBody] LoadRequest request)
        {
            if (!_client.IsConnected)
            {
                return Ok(new { success = false, message = "未连接到服务器" });
            }

            var response = await _client.LoadExtensionAsync(request.ExtensionPath);
            if (response == null)
            {
                return Ok(new { success = false, message = "请求失败" });
            }

            return Ok(new { response.Success, response.Message });
        }
    }

    public class UnloadRequest
    {
        public string ExtensionKey { get; set; } = "";
    }

    public class LoadRequest
    {
        public string ExtensionPath { get; set; } = "";
    }
}
