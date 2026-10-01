using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    /// <summary>Intelligence 分部实现(自 Intelligence.cs 按职责拆分, 类成员同源)。</summary>
    internal partial class Intelligence
    {
        #region AI客户端与工具函数适配
        /// <summary>
        /// AI工具函数适配器 - 将AiTools内置工具包装为Microsoft.Extensions.AI的AIFunction
        /// 使AI可以通过原生函数调用(function calling)机制调用工具
        /// </summary>
        internal sealed class RtAiToolFunction : AIFunction
        {
            private readonly string _name;
            private readonly string _description;
            private readonly JsonElement _schema;

            public RtAiToolFunction(string name, string description, JObject schema)
            {
                _name = name;
                _description = description;
                _schema = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(schema.ToString(Formatting.None));
            }

            public override string Name => _name;

            public override string Description => _description;

            public override JsonElement JsonSchema => _schema;

            protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            {
                // 将函数调用参数序列化为JSON字符串，复用既有工具执行逻辑
                string argsJson;
                try
                {
                    if (arguments == null || arguments.Count == 0)
                    {
                        argsJson = "{}";
                    }
                    else
                    {
                        var jo = new JObject();
                        foreach (var kv in arguments)
                        {
                            if (kv.Value == null || kv.Value == DBNull.Value)
                                jo[kv.Key] = JValue.CreateNull();
                            else if (kv.Value is JsonElement je)
                                jo[kv.Key] = JToken.Parse(je.GetRawText());
                            else
                                jo[kv.Key] = JToken.FromObject(kv.Value);
                        }
                        argsJson = jo.ToString(Formatting.None);
                    }
                }
                catch (Exception ex)
                {
                    return new ValueTask<object?>($"[参数序列化失败] {ex.Message}");
                }

                // 必须 await: 原写法把 Task 对象本身当成了返回值, 模型收到的是
                // "System.Threading.Tasks.Task`1[System.String]" 而不是工具的真实结果, 工具调用形同虚设。
                return await AiTools.ExecuteToolAsync(_name, argsJson);
            }
        }

        /// <summary>
        /// OpenAI兼容API的IChatClient实现 - 支持原生函数调用(tools/tool_calls)
        /// 适配本地模型(Ollama)及各类OpenAI兼容服务端(DeepSeek/OpenRouter等)
        /// </summary>
        internal sealed class OpenAiCompatibleChatClient : IChatClient
        {
            private readonly string _endpoint;
            private readonly string _apiKey;
            private readonly string _model;
            private readonly int _maxTokens;
            private readonly double _temperature;
            private readonly int _timeoutSeconds;

            public OpenAiCompatibleChatClient(string endpoint, string apiKey, string model, int maxTokens, double temperature, int timeoutSeconds)
            {
                _endpoint = endpoint;
                _apiKey = apiKey;
                _model = model;
                _maxTokens = maxTokens;
                _temperature = temperature;
                _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 180;
            }

            public ChatClientMetadata? Metadata => null;

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                return GetResponseCoreAsync(messages, options, cancellationToken);
            }

            private async Task<ChatResponse> GetResponseCoreAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
            {
                // 构建OpenAI请求体
                var requestMessages = new JArray();
                foreach (var msg in messages)
                {
                    var text = msg.Text;

                    // 收集工具调用与工具结果内容
                    var toolCalls = msg.Contents?.OfType<FunctionCallContent>().ToList() ?? new List<FunctionCallContent>();
                    var toolResults = msg.Contents?.OfType<FunctionResultContent>().ToList() ?? new List<FunctionResultContent>();

                    if (toolResults.Count > 0)
                    {
                        // 工具结果消息(role=tool)
                        foreach (var tr in toolResults)
                        {
                            requestMessages.Add(new JObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = tr.CallId ?? "",
                                ["content"] = tr.Result?.ToString() ?? ""
                            });
                        }
                        continue;
                    }

                    if (msg.Role == ChatRole.Assistant && toolCalls.Count > 0)
                    {
                        // 助手工具调用消息
                        var assistantCalls = new JArray();
                        foreach (var fc in toolCalls)
                        {
                            string argsJson;
                            try
                            {
                                argsJson = fc.Arguments != null
                                    ? System.Text.Json.JsonSerializer.Serialize(fc.Arguments)
                                    : "{}";
                            }
                            catch
                            {
                                argsJson = "{}";
                            }
                            assistantCalls.Add(new JObject
                            {
                                ["id"] = fc.CallId ?? "",
                                ["type"] = "function",
                                ["function"] = new JObject
                                {
                                    ["name"] = fc.Name ?? "",
                                    ["arguments"] = argsJson
                                }
                            });
                        }

                        requestMessages.Add(new JObject
                        {
                            ["role"] = "assistant",
                            ["content"] = string.IsNullOrEmpty(text) ? null : (JToken?)text,
                            ["tool_calls"] = assistantCalls
                        });
                        continue;
                    }

                    requestMessages.Add(new JObject
                    {
                        ["role"] = MapRole(msg.Role),
                        ["content"] = text ?? ""
                    });
                }

                var requestBody = new JObject
                {
                    ["model"] = options?.ModelId ?? _model,
                    ["messages"] = requestMessages,
                    ["max_tokens"] = options?.MaxOutputTokens ?? _maxTokens,
                    ["temperature"] = options?.Temperature ?? (float)_temperature,
                    ["stream"] = false
                };

                // 工具定义(原生函数调用)
                var tools = options?.Tools?.OfType<AIFunction>().ToList();
                if (tools != null && tools.Count > 0)
                {
                    var toolsArray = new JArray();
                    foreach (var tool in tools)
                    {
                        toolsArray.Add(new JObject
                        {
                            ["type"] = "function",
                            ["function"] = new JObject
                            {
                                ["name"] = tool.Name,
                                ["description"] = tool.Description ?? "",
                                ["parameters"] = JObject.Parse(tool.JsonSchema.GetRawText())
                            }
                        });
                    }
                    requestBody["tools"] = toolsArray;
                    requestBody["tool_choice"] = "auto";
                }

                var json = requestBody.ToString(Formatting.None);

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeoutSeconds) };
                if (!string.IsNullOrWhiteSpace(_apiKey))
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                HttpResponseMessage response = await httpClient.PostAsync(_endpoint, httpContent, cancellationToken);

                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"AI请求失败 (HTTP {(int)response.StatusCode}): {responseBody}");
                }

                // 解析响应
                var jsonResponse = JObject.Parse(responseBody);
                var choice = jsonResponse["choices"]?[0];
                var message = choice?["message"];
                if (message == null)
                    throw new InvalidOperationException($"AI响应格式异常: {responseBody}");

                // 推理模型(如 deepseek-reasoner)会返回 reasoning_content/reasoning，
                // 广播出去供面板在 AI 自动化卡片上动态展示思考过程
                string? reasoning = message["reasoning_content"]?.ToString();
                if (string.IsNullOrWhiteSpace(reasoning))
                    reasoning = message["reasoning"]?.ToString();
                if (!string.IsNullOrWhiteSpace(reasoning))
                    AiThinkingBus.Publish(reasoning!);

                var contents = new List<AIContent>();

                string? textContent = message["content"]?.ToString();
                if (!string.IsNullOrEmpty(textContent))
                    contents.Add(new TextContent(textContent));

                var toolCallsToken = message["tool_calls"];
                if (toolCallsToken is JArray callArray && callArray.Count > 0)
                {
                    foreach (var call in callArray)
                    {
                        string? callId = call["id"]?.ToString();
                        string? fnName = call["function"]?["name"]?.ToString();
                        string? fnArgs = call["function"]?["arguments"]?.ToString();
                        if (string.IsNullOrEmpty(fnName)) continue;

                        Dictionary<string, object?>? argsDict = null;
                        if (!string.IsNullOrWhiteSpace(fnArgs))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(fnArgs);
                                argsDict = new Dictionary<string, object?>();
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                    argsDict[prop.Name] = prop.Value.Clone();
                            }
                            catch
                            {
                                // 参数解析失败时按空参数处理，交由工具层报错
                            }
                        }

                        contents.Add(new FunctionCallContent(callId ?? Guid.NewGuid().ToString("N"), fnName, argsDict ?? new Dictionary<string, object?>()));
                    }
                }

                if (contents.Count == 0)
                    contents.Add(new TextContent(""));

                var responseMessage = new ChatMessage(ChatRole.Assistant, contents);

                string? finishReason = choice!["finish_reason"]?.ToString();
                var chatResponse = new ChatResponse(responseMessage)
                {
                    ModelId = _model,
                    FinishReason = string.IsNullOrEmpty(finishReason) ? null : new ChatFinishReason(finishReason)
                };

                return chatResponse;
            }

            private static string MapRole(ChatRole role)
            {
                string? v = role.Value;
                if (v == "system") return "system";
                if (v == "assistant") return "assistant";
                if (v == "tool") return "tool";
                return "user";
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("OpenAiCompatibleChatClient不支持流式响应。");
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>AI 思考过程广播：模型返回 reasoning_content 时触发，面板据此动态展示</summary>
        internal static class AiThinkingBus
        {
            internal static event Action<string>? Received;

            internal static void Publish(string reasoning)
            {
                try { Received?.Invoke(reasoning); }
                catch { /* 订阅方异常不影响 AI 主流程 */ }
            }
        }

        #endregion
    }
}
