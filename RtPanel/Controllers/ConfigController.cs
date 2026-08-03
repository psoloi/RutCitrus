using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtCli.Grpc;
using RtPanel.Services;

namespace RtPanel.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ConfigController : ControllerBase
    {
        private readonly RtCliClientService _client;
        private readonly ConfigSchemaService _schema;

        public ConfigController(RtCliClientService client, ConfigSchemaService schema)
        {
            _client = client;
            _schema = schema;
        }

        /// <summary>
        /// 列出可编辑的配置文件清单。
        /// </summary>
        [HttpGet("files")]
        public async Task<IActionResult> ListFiles()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, files = new List<object>(), message = "未连接到服务器" });

            var response = await _client.ListConfigFilesAsync();
            if (response == null)
                return Ok(new { success = false, files = new List<object>(), message = "请求失败" });

            var files = response.Files.Select(f => new
            {
                fileName = f.FileName,
                displayName = f.DisplayName,
                description = f.Description
            }).ToList();

            return Ok(new { success = true, files });
        }

        /// <summary>
        /// 获取配置文件原始文本(供文本编辑器使用)。
        /// 不传 file 时默认 config.yml，保持向后兼容。
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetConfig([FromQuery] string? file = null)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, content = "", message = "未连接到服务器" });

            ConfigResponse? response;
            if (string.IsNullOrEmpty(file) || file == "config.yml")
                response = await _client.GetConfigAsync();
            else
                response = await _client.GetConfigFileAsync(file);

            if (response == null)
                return Ok(new { success = false, content = "", message = "请求失败" });

            return Ok(new { response.Success, response.Content, response.Message });
        }

        /// <summary>
        /// 保存配置文件原始文本并热重载。
        /// 不传 file 时默认 config.yml。
        /// </summary>
        [HttpPost("save")]
        public async Task<IActionResult> SaveConfig([FromBody] SaveConfigRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            SaveConfigResponse? response;
            if (string.IsNullOrEmpty(request.FileName) || request.FileName == "config.yml")
                response = await _client.SaveConfigAsync(request.Content);
            else
                response = await _client.SaveConfigFileAsync(request.FileName, request.Content);

            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { response.Success, response.Message });
        }

        /// <summary>
        /// 获取结构化配置节点(供界面编辑器使用)。
        /// 不传 file 时默认 config.yml。
        /// </summary>
        [HttpGet("schema")]
        public async Task<IActionResult> GetSchema([FromQuery] string? file = null)
        {
            var fileName = string.IsNullOrEmpty(file) ? "config.yml" : file;
            if (!_client.IsConnected)
                return Ok(new { success = false, nodes = new List<object>(), message = "未连接到服务器" });

            var response = fileName == "config.yml"
                ? await _client.GetConfigAsync()
                : await _client.GetConfigFileAsync(fileName);

            if (response == null || !response.Success)
                return Ok(new { success = false, nodes = new List<object>(), message = response?.Message ?? "请求失败" });

            // 从 RtCli Repository 获取配置项注释
            var docsResponse = await _client.GetConfigDocsAsync(fileName);
            var externalComments = docsResponse != null
                ? new Dictionary<string, string>(docsResponse.Docs)
                : new Dictionary<string, string>();

            var nodes = _schema.Parse(response.Content, fileName, externalComments);
            return Ok(new { success = true, nodes, content = response.Content, fileName });
        }

        /// <summary>
        /// 基于界面编辑器的变更列表回写配置文件并热重载。
        /// </summary>
        [HttpPost("saveSchema")]
        public async Task<IActionResult> SaveSchema([FromBody] ConfigSaveRequest request)
        {
            var fileName = string.IsNullOrEmpty(request.FileName) ? "config.yml" : request.FileName;
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            ConfigResponse? cur = fileName == "config.yml"
                ? await _client.GetConfigAsync()
                : await _client.GetConfigFileAsync(fileName);

            if (cur == null || !cur.Success)
                return Ok(new { success = false, message = cur?.Message ?? "无法读取当前配置" });

            var newContent = _schema.ApplyChanges(cur.Content, request.Changes ?? new List<ConfigChange>(), fileName);

            SaveConfigResponse? response = fileName == "config.yml"
                ? await _client.SaveConfigAsync(newContent)
                : await _client.SaveConfigFileAsync(fileName, newContent);

            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { response.Success, response.Message, content = newContent });
        }
    }

    public class SaveConfigRequest
    {
        public string FileName { get; set; } = "config.yml";
        public string Content { get; set; } = "";
    }
}
