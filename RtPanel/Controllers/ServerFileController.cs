using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtPanel.Services;

namespace RtPanel.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ServerFileController : ControllerBase
    {
        private readonly RtCliClientService _client;
        private readonly ConfigSchemaService _schema;

        public ServerFileController(RtCliClientService client, ConfigSchemaService schema)
        {
            _client = client;
            _schema = schema;
        }

        /// <summary>列出当前 MC 服务端工作目录下可编辑的配置文件。</summary>
        [HttpGet("list")]
        public async Task<IActionResult> List()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var response = await _client.ListServerFilesAsync();
            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            var files = response.Files.Select(f => new
            {
                fileName = f.FileName,
                displayName = f.DisplayName,
                description = f.Description,
                category = f.Category,
                editable = f.Editable,
                sizeBytes = f.SizeBytes
            }).ToList();

            return Ok(new
            {
                response.Success,
                response.Message,
                serverKey = response.ServerKey,
                serverName = response.ServerName,
                workPath = response.WorkPath,
                files
            });
        }

        /// <summary>获取 server.properties 在指定版本下的字段 schema(用于界面版本选择器)。</summary>
        [HttpGet("propertiesSchema")]
        public async Task<IActionResult> GetPropertiesSchema([FromQuery] string? version = null)
        {
            var ver = string.IsNullOrEmpty(version) ? "26.2" : version;
            var fields = _schema.GetVisiblePropertiesFields(ver);

            // 从 RtCli Repository 获取 server.properties 字段描述
            Dictionary<string, string> docs = new();
            if (_client.IsConnected)
            {
                var docsResponse = await _client.GetConfigDocsAsync("server.properties");
                if (docsResponse != null)
                    docs = new Dictionary<string, string>(docsResponse.Docs);
            }

            return Ok(new
            {
                success = true,
                version = ver,
                versions = ConfigSchemaService.SupportedVersions,
                categories = PropertiesCategoryNames.Map,
                fields = fields.Select(f => new
                {
                    key = f.Key,
                    displayName = f.DisplayName,
                    description = docs.TryGetValue(f.Key, out var d) && !string.IsNullOrEmpty(d) ? d : f.Description,
                    type = f.Type,
                    @default = f.Default,
                    category = f.Category,
                    sinceVer = f.SinceVer,
                    removedVer = f.RemovedVer
                })
            });
        }

        /// <summary>读取 MC 服务端配置文件原始文本。?schema=true 时返回结构化节点。</summary>
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string serverKey, [FromQuery] string file, [FromQuery] string? version = null)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var response = await _client.GetServerFileAsync(serverKey ?? "", file ?? "");
            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            if (!response.Success)
                return Ok(new { response.Success, response.Message });

            // server.properties 走 properties 解析器
            if (string.Equals(file, "server.properties", StringComparison.OrdinalIgnoreCase))
            {
                // 从 RtCli Repository 获取字段描述
                var docsResponse = await _client.GetConfigDocsAsync("server.properties");
                var externalDescs = docsResponse != null
                    ? new Dictionary<string, string>(docsResponse.Docs)
                    : new Dictionary<string, string>();

                var nodes = _schema.ParseProperties(response.Content, string.IsNullOrEmpty(version) ? "26.2" : version, externalDescs);
                return Ok(new
                {
                    response.Success,
                    response.Message,
                    response.Content,
                    fileName = response.FileName,
                    category = response.Category,
                    parser = "properties",
                    nodes,
                    detectedVersion = version ?? ""
                });
            }

            // JSON 类配置(ops/whitelist/banned) 直接返回原始文本(前端用文本编辑)
            // YAML 类配置走通用 YAML 解析器
            if (response.Category == "yaml")
            {
                // 从 RtCli Repository 获取配置项注释
                var docsResponse = await _client.GetConfigDocsAsync(file ?? "");
                var externalComments = docsResponse != null
                    ? new Dictionary<string, string>(docsResponse.Docs)
                    : new Dictionary<string, string>();

                var nodes = _schema.Parse(response.Content, file ?? "", externalComments);
                return Ok(new
                {
                    response.Success,
                    response.Message,
                    response.Content,
                    fileName = response.FileName,
                    category = response.Category,
                    parser = "yaml",
                    nodes
                });
            }

            // 其它(text/json/ban/whitelist) 用纯文本编辑器
            return Ok(new
            {
                response.Success,
                response.Message,
                response.Content,
                fileName = response.FileName,
                category = response.Category,
                parser = "text"
            });
        }

        /// <summary>保存 MC 服务端配置文件。?applySchema=true 时按界面变更回写。</summary>
        [HttpPost("save")]
        public async Task<IActionResult> Save([FromBody] SaveServerFileApiRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            string content = request.Content ?? "";

            // 如果是 schema 模式 + properties 文件，先把变更回写到当前文本
            if (request.ApplySchema && !string.IsNullOrEmpty(request.FileName))
            {
                // 先读取当前文本
                var cur = await _client.GetServerFileAsync(request.ServerKey ?? "", request.FileName);
                if (cur == null || !cur.Success)
                    return Ok(new { success = false, message = cur?.Message ?? "无法读取当前配置" });

                if (string.Equals(request.FileName, "server.properties", StringComparison.OrdinalIgnoreCase))
                    content = _schema.ApplyProperties(cur.Content, request.Changes ?? new List<ConfigChange>(), request.Version ?? "");
                else
                    content = _schema.ApplyChanges(cur.Content, request.Changes ?? new List<ConfigChange>(), request.FileName);
            }

            var response = await _client.SaveServerFileAsync(request.ServerKey ?? "", request.FileName ?? "", content);
            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { response.Success, response.Message, content });
        }
    }

    public class SaveServerFileApiRequest
    {
        public string ServerKey { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Content { get; set; } = "";
        public bool ApplySchema { get; set; }
        public string Version { get; set; } = "";
        public List<ConfigChange> Changes { get; set; } = new();
    }
}
