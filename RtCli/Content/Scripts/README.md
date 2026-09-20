# RtCli Python 脚本集 — Minecraft 控制台错误识别

注意这个只是测试项目，完全是为了演示 RtCli 脚本引擎的使用而使用AI编写的目前不可靠。
配合 RtCli（`Modules/Extension/Scripts.cs` 脚本引擎）使用的 Python 脚本集。
项目将 Minecraft 控制台信息传递给脚本，脚本识别错误类型与原因后，
把结果打印到标准输出（由 RtCli 捕获并记录为 `[Python:脚本名]` 日志）。

仅依赖 Python 标准库（Python 3.7+），无需安装第三方包。

## 文件结构

```
Content/Scripts/
├── mc_error_analyzer.py    入口1: 分析一段控制台信息，识别错误类型/原因并给出建议
├── mc_log_scanner.py       入口2: 扫描日志文件/目录，输出按类别聚合的错误统计报告
├── README.md               本文档
└── mclog/                  共享分析库（Python 包，可被其他脚本 import 复用）
    ├── __init__.py         对外 API: analyze / analyze_line / scan
    ├── parser.py           日志行解析（时间戳/级别/线程/Logger）+ Java 异常链提取
    ├── rules.py            内置错误识别规则库（22 类，可扩展）
    ├── engine.py           分析引擎与扫描聚合
    ├── formatter.py        中文文本 / JSON 输出格式化
    ├── cli.py              通用命令行接口（对接 RtCli 调用契约）
    └── selftest.py         内置自检样例
```

## 项目调用契约（接口）

RtCli 脚本引擎按以下方式调用 Python 脚本（`-X utf8` + `PYTHONUTF8=1`，UTF-8 输出）：

```
python -X utf8 <脚本路径> "<input>" --event <事件名>
```

| 参数 | 说明 |
| ---- | ---- |
| `input`（位置参数） | 控制台信息文本；玩家事件触发时为事件参数 JSON；若为已存在的文件路径则自动读取该文件 |
| `--event` | RtCli 事件名称（透传，仅用于结果展示） |
| `--json` | 输出结构化 JSON（默认输出中文文本） |
| `--file` | 从指定文件读取输入 |
| `--stdin` | 从标准输入读取 |
| `--tail N` | 仅分析最后 N 行 |
| `--selftest` | 运行内置自检（16 个样例） |
| `--top N` | 仅 mc_log_scanner：最多显示 N 类问题 |

**退出码**：`0` = 分析完成（无论是否检测到错误）；`2` = 用法错误；`3` = 内部错误。

### mc_error_analyzer 输出示例

输入（控制台错误信息）：

```
[14:23:05 ERROR]: java.lang.NullPointerException: Cannot invoke world.getName() because world is null
	at com.example.myplugin.MyPlugin.onEnable(MyPlugin.java:42)
	at org.bukkit.plugin.java.JavaPluginLoader.enablePlugin(JavaPluginLoader.java:263)
```

标准输出（RtCli 会记录为日志）：

```
[MC控制台错误分析] 检测到问题 | 严重度: ERROR | 分类: 插件
  错误类型: java.lang.NullPointerException
  错误信息: Cannot invoke world.getName() because world is null
  原因: Bukkit/Spigot/Paper 插件在启用、事件处理或任务执行中出错
  来源: 第三方组件 (com.example.myplugin.MyPlugin.onEnable)
  时间: 14:23:05
  堆栈要点:
    - com.example.myplugin.MyPlugin.onEnable(MyPlugin.java:42)
    - org.bukkit.plugin.java.JavaPluginLoader.enablePlugin(JavaPluginLoader.java:263)
  建议: 更新或移除报错插件；确认其与核心版本、前置库兼容
```

`--json` 时输出同结构的完整字段（`is_error / severity / rule_id / category_name /
error_type / error_message / cause / suggestion / source / stack_summary /
exception_chain / other_issues / level_stats` 等）。

### 识别的错误类别（22 类）

崩溃报告、内存不足、栈溢出、服务器无响应（看门狗）、端口占用、EULA 未同意、
Java 版本不匹配、Java 启动失败、模组依赖缺失、模组加载失败、插件错误、数据包错误、
世界/区块错误、数据库错误、磁盘/IO 错误、配置文件错误、玩家认证失败、网络连接异常、
服务器卡顿（掉刻）、未归类 Java 异常，以及 ERROR / WARN 级别兜底。

崩溃标志与 RtCli `Analyzer.ProcessCrashDetectionLine` 保持一致
（`---- Minecraft Crash Report ----`、`This crash report has been saved to`、
`Shutting down the server`、`Server thread/FATAL` 等）。

## 在 RtCli 中注册使用

在 `Data/scripts_settings.yml` 中注册（按需选择触发方式）：

```yaml
scripts:
  # 方式A: 由 AI(Intelligence) 通过 run_script 工具按需调用，input 动态传入控制台信息
  mc_error_analyzer:
    file: mc_error_analyzer.py
    trigger_event: ""            # 留空会在启动时执行一次(无输入仅打印用法提示)
    enabled: true
    input: ""

  # 方式B: 绑定事件触发，input 配置为日志文件路径(脚本会自动读取并分析)
  crash_analyzer:
    file: mc_error_analyzer.py
    trigger_event: ServerCrashEvent
    enabled: true
    input: "E:/mcserver/logs/latest.log"

  # 方式C: 定时扫描日志(Scheduler run_script 动作)
  log_scanner:
    file: mc_log_scanner.py
    trigger_event: ""
    enabled: true
    input: "E:/mcserver/logs/latest.log"
```

AI 调用示例（`run_script` 工具参数，input 即控制台信息）：

```json
{"name": "mc_error_analyzer", "input": "12:34:56 [Server thread/ERROR]: Failed to bind to port"}
```

## 手动调用

```powershell
# 分析一段控制台信息
python -X utf8 Content\Scripts\mc_error_analyzer.py "12:34:56 [Server thread/ERROR]: Failed to bind to port"

# 分析最近 200 行日志
python -X utf8 Content\Scripts\mc_error_analyzer.py E:\mcserver\logs\latest.log --tail 200

# 扫描日志目录(自动取最新 .log)并输出 JSON
python -X utf8 Content\Scripts\mc_log_scanner.py E:\mcserver --top 10 --json

# 自检
python -X utf8 Content\Scripts\mc_error_analyzer.py --selftest
```

## Python API（供其他脚本复用）

```python
from mclog import analyze, scan, format_text

result = analyze("[12:00:00 ERROR]: Failed to bind to port")
print(result["is_error"], result["category_name"], result["cause"])

print(format_text(scan(open("logs/latest.log", encoding="utf-8").read())))
```

## 扩展规则

编辑 `mclog/rules.py`，在 `RULES` 中按 `_rule(规则ID, 名称, 分类ID, 分类名, 严重度,
优先级, [正则列表], 原因, 建议)` 追加即可。`priority` 数值越小越先匹配；
命中多规则时首个作为主判定，其余计入"其他问题"。
