using MySqlConnector;
using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace RtCli.Modules.Function
{
    #region LuckPerms 配置模型

    /// <summary>
    /// support.yml 根配置
    /// </summary>
    public class SupportSettings
    {
        /// <summary>YAML 键为 luckperms(UnderscoredNamingConvention 会把 LuckPerms 转成 luck_perms，故显式指定别名)</summary>
        [YamlMember(Alias = "luckperms")]
        public LuckPermsConfig LuckPerms { get; set; } = new LuckPermsConfig();
    }

    /// <summary>
    /// LuckPerms 配置 (大项: luckperms)
    /// </summary>
    public class LuckPermsConfig
    {
        /// <summary>是否启用 LuckPerms 变更监测</summary>
        public bool Enabled { get; set; } = false;
        /// <summary>MySQL 数据库连接配置</summary>
        public LuckPermsMysqlConfig Mysql { get; set; } = new LuckPermsMysqlConfig();
    }

    /// <summary>
    /// LuckPerms MySQL 配置 (小项: mysql)
    /// </summary>
    public class LuckPermsMysqlConfig
    {
        public string Address { get; set; } = "127.0.0.1:3306";
        public string Database { get; set; } = "minecraft";
        public string Username { get; set; } = "root";
        public string Password { get; set; } = "";
        /// <summary>YAML 键为 table_prefix(命名约定不支持连字符)</summary>
        public string TablePrefix { get; set; } = "luckperms_";
    }

    #endregion

    /// <summary>
    /// Support 扩展模块 - 负责 LuckPerms 权限变更监测。
    /// 启用后轮询 LuckPerms 的 actions 表，检测到新记录即发布 LuckPermsChangeEvent 事件，
    /// 供脚本(scripts)与调度器(scheduler)订阅使用。
    /// </summary>
    internal class Support
    {
        private const string ThisName = "Support";
        private static SupportSettings _settings = new SupportSettings();
        private static CancellationTokenSource? _cts;
        private static bool _isRunning = false;
        private static long _lastActionId = 0;
        private static readonly object _lock = new();

        public static bool IsRunning => _isRunning;
        public static SupportSettings Settings => _settings;

        private static void Log(string msg, int msgType)
        {
            Output.Log(Markup.Escape(msg), msgType, ThisName);
        }

        public static void Initialize()
        {
            LoadSettings();
        }

        /// <summary>从 ContentManager 统一路径加载配置（不存在则自动创建默认配置）</summary>
        public static void LoadSettings()
        {
            _settings = ContentManager.LoadSupportSettings();
        }

        /// <summary>启动监测（enabled=false 时不启动）</summary>
        public static void Start()
        {
            lock (_lock)
            {
                if (_isRunning) return;

                if (!_settings.LuckPerms.Enabled)
                {
                    // 未启用时静默返回, 不输出消息
                    return;
                }

                var mysql = _settings.LuckPerms.Mysql;
                if (string.IsNullOrEmpty(mysql.Address) || string.IsNullOrEmpty(mysql.Database))
                {
                    Log("LuckPerms MySQL 配置不完整，无法启动监测", 2);
                    return;
                }

                _cts = new CancellationTokenSource();
                _isRunning = true;

                _ = Task.Run(() => PollLoop(_cts.Token));
                Log("LuckPerms 变更监测已启动", 1);
            }
        }

        /// <summary>停止监测</summary>
        public static void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning) return;
                _cts?.Cancel();
                _isRunning = false;
                Log("LuckPerms 变更监测已停止", 1);
            }
        }

        /// <summary>重新加载配置并重启监测</summary>
        public static void Reload()
        {
            Stop();
            Thread.Sleep(500);
            LoadSettings();
            Start();
        }

        /// <summary>轮询循环</summary>
        private static async Task PollLoop(CancellationToken token)
        {
            var luckPerms = _settings.LuckPerms;
            var mysql = luckPerms.Mysql;
            int interval = 5; // 轮询间隔(秒)

            // 首次启动: 获取当前最大ID，只监测新记录
            try
            {
                _lastActionId = await GetMaxActionIdAsync(mysql);
                Log($"LuckPerms 当前最大 action ID: {_lastActionId}", 1);
            }
            catch (Exception ex)
            {
                Log($"LuckPerms 初始化失败(无法连接MySQL): {ex.Message}", 3);
                _isRunning = false;
                return;
            }

            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    await Task.Delay(interval * 1000, token);
                    if (token.IsCancellationRequested) break;

                    var newActions = await QueryNewActionsAsync(mysql, _lastActionId);

                    foreach (var action in newActions)
                    {
                        var evt = new LuckPermsChangeEvent(
                            action.ActorUuid,
                            action.ActorName,
                            action.Type,
                            action.ActedUuid,
                            action.ActedName,
                            action.Action
                        );

                        EventBus.Publish(evt);
                        Log($"LuckPerms变更: {action.ActorName} -> {action.Action} ({action.ActedName})", 1);
                    }

                    if (newActions.Count > 0)
                        _lastActionId = newActions[^1].Id;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log($"LuckPerms 轮询异常: {ex.Message}", 2);
                    await Task.Delay(10 * 1000, token); // 异常后等待10秒
                }
            }
        }

        /// <summary>获取 actions 表当前最大 ID</summary>
        private static async Task<long> GetMaxActionIdAsync(LuckPermsMysqlConfig mysql)
        {
            string connStr = BuildConnectionString(mysql);
            string tableName = $"{mysql.TablePrefix}actions";

            using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();

            string sql = $"SELECT COALESCE(MAX(`id`), 0) FROM `{tableName}`";
            using var cmd = new MySqlCommand(sql, conn);
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }

        /// <summary>查询新记录</summary>
        private static async Task<List<LuckPermsAction>> QueryNewActionsAsync(LuckPermsMysqlConfig mysql, long lastId)
        {
            string connStr = BuildConnectionString(mysql);
            string tableName = $"{mysql.TablePrefix}actions";
            var result = new List<LuckPermsAction>();

            using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();

            string sql = $"SELECT `id`, `actor_uuid`, `actor_name`, `type`, `acted_uuid`, `acted_name`, `action` " +
                         $"FROM `{tableName}` WHERE `id` > @lastId ORDER BY `id` ASC";
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@lastId", lastId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new LuckPermsAction
                {
                    Id = reader.GetInt64("id"),
                    ActorUuid = reader.GetString("actor_uuid"),
                    ActorName = reader.GetString("actor_name"),
                    Type = reader.GetString("type"),
                    ActedUuid = reader.GetString("acted_uuid"),
                    ActedName = reader.GetString("acted_name"),
                    Action = reader.GetString("action")
                });
            }

            return result;
        }

        /// <summary>构建 MySQL 连接字符串</summary>
        private static string BuildConnectionString(LuckPermsMysqlConfig mysql)
        {
            // 兼容 "host:port" 与 "host" 两种格式，避免 MySqlConnector 不识别冒号端口
            string host = mysql.Address ?? "";
            string port = "3306";
            int colon = host.LastIndexOf(':');
            if (colon >= 0 && host.Length > colon + 1)
            {
                var portPart = host.Substring(colon + 1).Trim();
                if (int.TryParse(portPart, out _))
                {
                    port = portPart;
                    host = host.Substring(0, colon).Trim();
                }
            }

            // SslMode=none          : 兼容未启用 SSL 的自建 MySQL
            // AllowPublicKeyRetrieval=true : 兼容 MySQL 8.0 caching_sha2_password 在非 SSL 下认证
            return $"Server={host};Port={port};Database={mysql.Database};" +
                   $"User={mysql.Username};Password={mysql.Password};" +
                   $"Charset=utf8mb4;SslMode=none;AllowPublicKeyRetrieval=true;" +
                   $"Pooling=true;MinimumPoolSize=1;MaximumPoolSize=3;" +
                   $"ConnectionTimeout=10;DefaultCommandTimeout=30;";
        }
    }

    /// <summary>LuckPerms action 记录</summary>
    internal class LuckPermsAction
    {
        public long Id { get; set; }
        public string ActorUuid { get; set; } = "";
        public string ActorName { get; set; } = "";
        public string Type { get; set; } = "";
        public string ActedUuid { get; set; } = "";
        public string ActedName { get; set; } = "";
        public string Action { get; set; } = "";
    }
}