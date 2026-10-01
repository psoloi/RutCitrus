using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtCli.Grpc;
using RtPanel.Services;

namespace RtPanel.Controllers
{
    /// <summary>
    /// 「功能」页面 API：目前提供 AI 技能管理(创建/编辑/删除/导入，写入 RtCli 的 ai_settings.yml)。
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class FeaturesController : ControllerBase
    {
        private readonly RtCliClientService _client;

        public FeaturesController(RtCliClientService client)
        {
            _client = client;
        }

        /// <summary>获取 AI 技能列表。</summary>
        [HttpGet("skills")]
        public async Task<IActionResult> GetSkills()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器", skills = new List<object>() });

            var response = await _client.GetAiSkillsAsync();
            if (response == null)
                return Ok(new { success = false, message = "请求失败", skills = new List<object>() });

            var skills = response.Skills.Select(s => new
            {
                name = s.Name,
                description = s.Description,
                behavior = s.Behavior
            }).ToList();

            return Ok(new { success = true, message = response.Message, skills });
        }

        /// <summary>新增或编辑一个 AI 技能。</summary>
        [HttpPost("skills/save")]
        public async Task<IActionResult> SaveSkill([FromBody] SaveSkillRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var response = await _client.SaveAiSkillAsync(
                request.OriginalName, request.Name, request.Description ?? "", request.Behavior ?? "");
            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { response.Success, response.Message });
        }

        /// <summary>删除指定 AI 技能。</summary>
        [HttpPost("skills/delete")]
        public async Task<IActionResult> DeleteSkill([FromBody] DeleteSkillRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var response = await _client.DeleteAiSkillAsync(request.Name);
            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { response.Success, response.Message });
        }

        /// <summary>导入技能(JSON/YAML 文本)。</summary>
        [HttpPost("skills/import")]
        public async Task<IActionResult> ImportSkills([FromBody] ImportSkillsRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器", skills = new List<object>() });

            var response = await _client.ImportAiSkillsAsync(request.Content ?? "", request.Overwrite);
            if (response == null)
                return Ok(new { success = false, message = "请求失败", skills = new List<object>() });

            var skills = response.Skills.Select(s => new
            {
                name = s.Name,
                description = s.Description,
                behavior = s.Behavior
            }).ToList();

            return Ok(new { response.Success, response.Message, skills });
        }

        /// <summary>获取 AI 记忆列表与记忆设置。</summary>
        [HttpGet("memory")]
        public async Task<IActionResult> GetMemories()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器", memories = new List<object>(), settings = (object?)null });

            var response = await _client.GetAiMemoriesAsync();
            if (response == null)
                return Ok(new { success = false, message = "请求失败", memories = new List<object>(), settings = (object?)null });

            var memories = response.Memories.Select(m => new
            {
                time = m.Time,
                topic = m.Topic,
                content = m.Content
            }).ToList();

            object? settings = null;
            if (response.Settings != null)
            {
                var s = response.Settings;
                settings = new
                {
                    enabled = s.Enabled,
                    useAi = s.UseAi,
                    maxTokens = s.MaxTokens,
                    maxContentLength = s.MaxContentLength,
                    maxEntries = s.MaxEntries,
                    captureServerInfo = s.CaptureServerInfo,
                    capturePlayerCount = s.CapturePlayerCount,
                    captureCommonErrors = s.CaptureCommonErrors,
                    prompt = s.Prompt
                };
            }

            return Ok(new { response.Success, response.Message, memories, settings });
        }

        /// <summary>保存 AI 记忆设置。</summary>
        [HttpPost("memory/settings")]
        public async Task<IActionResult> SaveMemorySettings([FromBody] MemorySettingsRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var settings = new AiMemorySettings
            {
                Enabled = request.Enabled,
                UseAi = request.UseAi,
                MaxTokens = request.MaxTokens,
                MaxContentLength = request.MaxContentLength,
                MaxEntries = request.MaxEntries,
                CaptureServerInfo = request.CaptureServerInfo,
                CapturePlayerCount = request.CapturePlayerCount,
                CaptureCommonErrors = request.CaptureCommonErrors,
                Prompt = request.Prompt ?? ""
            };

            var response = await _client.SaveAiMemorySettingsAsync(settings);
            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { response.Success, response.Message });
        }

        /// <summary>清空所有 AI 记忆。</summary>
        [HttpPost("memory/clear")]
        public async Task<IActionResult> ClearMemories()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var response = await _client.ClearAiMemoriesAsync();
            if (response == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { response.Success, response.Message });
        }

        /// <summary>AI 自动化任务状态(是否运行/上次运行/执行次数/触发条件)。</summary>
        [HttpGet("ai/status")]
        public async Task<IActionResult> GetAiAutomationStatus()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetAiAutomationStatusAsync();
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new
            {
                resp.Success,
                resp.Message,
                runnerRunning = resp.RunnerRunning,
                memoryCount = resp.MemoryCount,
                memoryBytes = resp.MemoryBytes,
                auditCount = resp.AuditCount,
                tasks = resp.Tasks.Select(t => new
                {
                    key = t.Key, t.Name, enabled = t.Enabled, trigger = t.Trigger, interval = t.Interval,
                    minTrigger = t.MinTriggerIntervalMinutes, actions = t.Actions,
                    lastRun = t.LastRun, runCount = t.RunCount, running = t.Running
                })
            });
        }

        /// <summary>AI 工具调用审计(每次工具调用的时间/任务/工具/参数/结果/成败)。</summary>
        [HttpGet("ai/tools")]
        public async Task<IActionResult> GetAiToolAudit([FromQuery] int limit = 100)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetAiToolAuditAsync(limit);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new
            {
                resp.Success,
                resp.Message,
                filePath = resp.FilePath,
                entries = resp.Entries.Select(e => new
                {
                    time = e.Time, task = e.Task, tool = e.Tool, args = e.Args, result = e.Result, ok = e.Ok
                })
            });
        }

        /// <summary>历史 AI 结论(ai_save.yml)。</summary>
        [HttpGet("ai/responses")]
        public async Task<IActionResult> GetAiResponses([FromQuery] int limit = 50)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetAiResponsesAsync(limit);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new
            {
                resp.Success,
                resp.Message,
                total = resp.Total,
                responses = resp.Responses.Select(r => new
                {
                    taskKey = r.TaskKey, taskName = r.TaskName,
                    inputSummary = r.InputSummary, aiResponse = r.AiResponse, timestamp = r.Timestamp
                })
            });
        }
        /// <summary>删除一条历史 AI 结论(自动化结果)。</summary>
        [HttpPost("ai/responses/delete")]
        public async Task<IActionResult> DeleteAiResponse([FromBody] DeleteAiResponsePayload request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.DeleteAiResponseAsync(request?.Timestamp ?? "", request?.TaskKey ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { success = resp.Success, message = resp.Message });
        }

        /// <summary>AI 自动化规则(逐条注入系统提示的"必须遵守的规则")。</summary>
        [HttpGet("ai/rules")]
        public async Task<IActionResult> GetAiRules()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetAiRulesAsync();
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new
            {
                resp.Success,
                resp.Message,
                rules = resp.Rules,
                systemPrompt = resp.SystemPrompt,
                runnerRunning = resp.RunnerRunning
            });
        }

        /// <summary>保存 AI 自动化规则。</summary>
        [HttpPost("ai/rules")]
        public async Task<IActionResult> SaveAiRules([FromBody] SaveAiRulesRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.SaveAiRulesAsync(request?.Rules ?? new List<string>());
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { resp.Success, resp.Message });
        }

        // ===== AI 工作流(ai_settings.yml 中 ServerAutoAi 的任务列表与运行参数) =====

        /// <summary>AI 工作流配置(任务列表 + 运行参数)。</summary>
        [HttpGet("ai/workflow")]
        public async Task<IActionResult> GetAiWorkflow()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetAiWorkflowAsync();
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            var runtime = resp.Runtime;
            var tasks = resp.Tasks.Select(t => new
            {
                key = t.Key,
                enabled = t.Enabled,
                name = t.Name,
                trigger = t.Trigger,
                input = t.Input,
                limit = t.Limit,
                prompt = t.Prompt,
                interval = t.Interval,
                actions = t.Actions,
                minTriggerIntervalMinutes = t.MinTriggerIntervalMinutes,
                errorSimilarityThreshold = t.ErrorSimilarityThreshold,
                errorCacheMinutes = t.ErrorCacheMinutes,
                serverKeys = t.ServerKeys
            }).ToList();

            return Ok(new
            {
                success = resp.Success,
                message = resp.Message,
                filePath = resp.FilePath,
                runnerRunning = resp.RunnerRunning,
                availableTools = resp.AvailableTools,
                defaultTriggers = resp.DefaultTriggers,
                availableEvents = resp.AvailableEvents,
                runtime = runtime == null ? null : new
                {
                    prompt = runtime.Prompt,
                    maxTokens = runtime.MaxTokens,
                    apiEndpoint = runtime.ApiEndpoint,
                    apiKey = "",
                    apiKeySet = runtime.ApiKeySet,
                    apiKeyMasked = runtime.ApiKeyMasked,
                    model = runtime.Model,
                    temperature = runtime.Temperature,
                    requestTimeoutSeconds = runtime.RequestTimeoutSeconds,
                    retryOnError = runtime.RetryOnError,
                    retryCount = runtime.RetryCount,
                    saveResponse = runtime.SaveResponse,
                    contextCacheSize = runtime.ContextCacheSize,
                    maxToolRounds = runtime.MaxToolRounds
                },
                tasks
            });
        }

        /// <summary>保存(新增/编辑)一个 AI 自动化任务。</summary>
        [HttpPost("ai/workflow/task")]
        public async Task<IActionResult> SaveAiTask([FromBody] AiTaskRequestDto request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (request == null)
                return Ok(new { success = false, message = "请求参数无效" });

            var task = new AiTaskConfig
            {
                Key = request.Key ?? "",
                Enabled = request.Enabled,
                Name = request.Name ?? "",
                Trigger = request.Trigger ?? "",
                Limit = request.Limit,
                Prompt = request.Prompt ?? "",
                Interval = request.Interval ?? "",
                MinTriggerIntervalMinutes = request.MinTriggerIntervalMinutes,
                ErrorSimilarityThreshold = request.ErrorSimilarityThreshold,
                ErrorCacheMinutes = request.ErrorCacheMinutes
            };
            task.Input.AddRange(request.Input ?? new List<string>());
            task.Actions.AddRange(request.Actions ?? new List<string>());
            task.ServerKeys.AddRange(request.ServerKeys ?? new List<string>());

            var resp = await _client.SaveAiTaskAsync(task, request.OriginalKey ?? "");
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { resp.Success, resp.Message });
        }

        /// <summary>删除一个 AI 自动化任务。</summary>
        [HttpPost("ai/workflow/task/delete")]
        public async Task<IActionResult> DeleteAiTask([FromBody] AiTaskKeyDto request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (request == null || string.IsNullOrWhiteSpace(request.Key))
                return Ok(new { success = false, message = "任务标识不能为空" });

            var resp = await _client.DeleteAiTaskAsync(request.Key);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { resp.Success, resp.Message });
        }

        /// <summary>保存 AI 工作流运行参数。</summary>
        [HttpPost("ai/workflow/runtime")]
        public async Task<IActionResult> SaveAiRuntime([FromBody] AiRuntimeDto request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (request == null)
                return Ok(new { success = false, message = "请求参数无效" });

            var runtime = new AiRuntimeConfig
            {
                Prompt = request.Prompt ?? "",
                MaxTokens = request.MaxTokens,
                ApiEndpoint = request.ApiEndpoint ?? "",
                ApiKey = request.ApiKey ?? "",
                Model = request.Model ?? "",
                Temperature = request.Temperature,
                RequestTimeoutSeconds = request.RequestTimeoutSeconds,
                RetryOnError = request.RetryOnError,
                RetryCount = request.RetryCount,
                SaveResponse = request.SaveResponse,
                ContextCacheSize = request.ContextCacheSize,
                MaxToolRounds = request.MaxToolRounds
            };

            var resp = await _client.SaveAiRuntimeAsync(runtime);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { resp.Success, resp.Message });
        }

        /// <summary>调度器任务列表(蓝图编辑器数据源)。</summary>
        [HttpGet("scheduler/tasks")]
        public async Task<IActionResult> GetSchedulerTasks()
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });

            var resp = await _client.GetSchedulerTasksAsync();
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new
            {
                resp.Success,
                resp.Message,
                isRunning = resp.IsRunning,
                tasks = resp.Tasks.Select(t => new
                {
                    key = t.Key, enabled = t.Enabled, trigger = t.Trigger, condition = t.Condition,
                    execute = t.Execute, passParameters = t.PassParameters, serverKeys = t.ServerKeys,
                    timeoutMs = t.TimeoutMs, delayMinutes = t.DelayMinutes, delayUntil = t.DelayUntil,
                    executeImmediately = t.ExecuteImmediately, maxExecutions = t.MaxExecutions,
                    expireAt = t.ExpireAt, queue = t.Queue, runCount = t.RunCount
                }),
                scripts = resp.Scripts,
                events = resp.Events,
                servers = resp.Servers
            });
        }

        /// <summary>保存调度器任务(蓝图编辑器保存按钮)。</summary>
        [HttpPost("scheduler/task")]
        public async Task<IActionResult> SaveSchedulerTask([FromBody] SaveSchedulerTaskRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (request == null || string.IsNullOrWhiteSpace(request.Key))
                return Ok(new { success = false, message = "任务标识不能为空" });

            var info = new SchedulerTaskInfo
            {
                Key = request.Key.Trim(),
                Enabled = request.Enabled,
                Trigger = request.Trigger ?? "",
                Condition = request.Condition ?? "",
                Execute = request.Execute ?? "",
                PassParameters = request.PassParameters,
                TimeoutMs = request.TimeoutMs,
                DelayMinutes = request.DelayMinutes,
                DelayUntil = request.DelayUntil ?? "",
                ExecuteImmediately = request.ExecuteImmediately,
                MaxExecutions = request.MaxExecutions,
                ExpireAt = request.ExpireAt ?? "",
                Queue = string.IsNullOrWhiteSpace(request.Queue) ? "Fast" : request.Queue
            };
            info.ServerKeys.AddRange(request.ServerKeys ?? new List<string>());

            var resp = await _client.SaveSchedulerTaskAsync(info);
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { resp.Success, resp.Message });
        }

        /// <summary>删除调度器任务。</summary>
        [HttpPost("scheduler/delete")]
        public async Task<IActionResult> DeleteSchedulerTask([FromBody] SaveSchedulerTaskRequest request)
        {
            if (!_client.IsConnected)
                return Ok(new { success = false, message = "未连接到服务器" });
            if (request == null || string.IsNullOrWhiteSpace(request.Key))
                return Ok(new { success = false, message = "任务标识不能为空" });

            var resp = await _client.DeleteSchedulerTaskAsync(request.Key.Trim());
            if (resp == null)
                return Ok(new { success = false, message = "请求失败" });

            return Ok(new { resp.Success, resp.Message });
        }
    }

    /// <summary>AI 规则保存请求</summary>
    public class SaveAiRulesRequest
    {
        public List<string> Rules { get; set; } = new();
    }

    /// <summary>删除一条自动化结果</summary>
    public class DeleteAiResponsePayload
    {
        public string? Timestamp { get; set; }
        public string? TaskKey { get; set; }
    }

    /// <summary>调度器任务保存请求(蓝图编辑器的节点属性)</summary>
    public class SaveSchedulerTaskRequest
    {
        public string Key { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Trigger { get; set; } = "";
        public string Condition { get; set; } = "";
        public string Execute { get; set; } = "";
        public bool PassParameters { get; set; }
        public List<string> ServerKeys { get; set; } = new();
        public int TimeoutMs { get; set; }
        public int DelayMinutes { get; set; }
        public string DelayUntil { get; set; } = "";
        public bool ExecuteImmediately { get; set; }
        public int MaxExecutions { get; set; }
        public string ExpireAt { get; set; } = "";
        public string Queue { get; set; } = "Fast";
    }

    public class SaveSkillRequest
    {
        public string OriginalName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Behavior { get; set; } = "";
    }

    public class DeleteSkillRequest
    {
        public string Name { get; set; } = "";
    }

    public class ImportSkillsRequest
    {
        public string Content { get; set; } = "";
        public bool Overwrite { get; set; }
    }

    public class MemorySettingsRequest
    {
        public bool Enabled { get; set; } = true;
        public bool UseAi { get; set; } = true;
        public int MaxTokens { get; set; } = 512;
        public int MaxContentLength { get; set; } = 500;
        public int MaxEntries { get; set; } = 1000;
        public bool CaptureServerInfo { get; set; } = true;
        public bool CapturePlayerCount { get; set; } = true;
        public bool CaptureCommonErrors { get; set; } = true;
        public string Prompt { get; set; } = "";
    }

    /// <summary>AI 工作流任务保存请求(originalKey 非空且与 key 不同=改名)。</summary>
    public class AiTaskRequestDto
    {
        public string Key { get; set; } = "";
        public string OriginalKey { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Name { get; set; } = "";
        public string Trigger { get; set; } = "";
        public List<string> Input { get; set; } = new List<string>();
        public int Limit { get; set; }
        public string Prompt { get; set; } = "";
        public string Interval { get; set; } = "";
        public List<string> Actions { get; set; } = new List<string>();
        public int MinTriggerIntervalMinutes { get; set; }
        public int ErrorSimilarityThreshold { get; set; }
        public int ErrorCacheMinutes { get; set; }
        /// <summary>生效实例标识列表(空=全部实例)</summary>
        public List<string> ServerKeys { get; set; } = new List<string>();
    }

    /// <summary>AI 工作流任务删除请求。</summary>
    public class AiTaskKeyDto
    {
        public string Key { get; set; } = "";
    }

    /// <summary>AI 工作流运行参数保存请求(apiKey 留空=不修改)。</summary>
    public class AiRuntimeDto
    {
        public string Prompt { get; set; } = "";
        public int MaxTokens { get; set; }
        public string ApiEndpoint { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "";
        public double Temperature { get; set; }
        public int RequestTimeoutSeconds { get; set; }
        public bool RetryOnError { get; set; }
        public int RetryCount { get; set; }
        public bool SaveResponse { get; set; }
        public int ContextCacheSize { get; set; }
        public int MaxToolRounds { get; set; }
    }
}