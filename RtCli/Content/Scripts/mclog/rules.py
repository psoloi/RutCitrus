# -*- coding: utf-8 -*-
"""mclog 内置错误识别规则库。

崩溃标志与 RtCli Analyzer.ProcessCrashDetectionLine 的判定保持一致
（---- Minecraft Crash Report ---- / This crash report has been saved to /
Shutting down the server / Server thread/FATAL 等）。

规则来源：RtCli 项目自有服务端群的真实日志（Paper/Leaf/Velocity/Mohist/Forge 等），
含插件加载失败、重复插件、NMS 版本不兼容、世界加载失败、认证服务不可用等真实场景。

priority 数值越小越先匹配，先命中者作为主判定，其余命中归入“其他问题”。
java_exception / generic_error / generic_warn 三条为兜底规则（无 patterns，
由引擎在无规则命中时按“异常堆栈 / ERROR 级别 / WARN 级别”兜底触发）。
"""

import re
from dataclasses import dataclass


@dataclass(frozen=True)
class Rule:
    rid: str
    name: str
    category_id: str
    category_name: str
    severity: str
    priority: int
    patterns: tuple
    cause: str
    suggestion: str

    def match(self, text):
        for pat in self.patterns:
            m = pat.search(text)
            if m:
                return m
        return None


def _rule(rid, name, cat, cat_name, severity, priority, patterns, cause, suggestion):
    return Rule(rid=rid, name=name, category_id=cat, category_name=cat_name,
                severity=severity, priority=priority,
                patterns=tuple(re.compile(p) for p in patterns),
                cause=cause, suggestion=suggestion)


RULES = [
    _rule('crash_report', '服务端崩溃', 'crash', '崩溃', 'FATAL', 10,
          [r'(?i)---- Minecraft Crash Report ----',
           r'(?i)This crash report has been saved to',
           r'(?i)Crash report saved',
           r'(?i)Shutting down the server',
           r'Server thread/FATAL'],
          '服务端发生致命错误并生成/记录了崩溃信息',
          '打开 crash-reports 目录中最新报告，看头部 Description 与最底层 Caused by；排查最近新增/更新的模组或插件'),

    _rule('out_of_memory', '内存不足', 'memory', '内存', 'FATAL', 20,
          [r'java\.lang\.OutOfMemoryError',
           r'(?i)insufficient memory for the Java Runtime Environment',
           r'(?i)Could not reserve enough space'],
          'Java 内存耗尽，无法继续分配对象',
          '增大堆内存（如 -Xmx4G）；减少模组/插件数量；持续增长需排查内存泄漏'),

    _rule('stack_overflow', '栈溢出', 'memory', '内存', 'ERROR', 21,
          [r'java\.lang\.StackOverflowError'],
          '方法调用层级过深，通常由无限递归或循环调用引起',
          '检查相关模组/插件的递归逻辑，更新或移除问题组件'),

    _rule('server_unresponsive', '服务器无响应', 'performance', '性能', 'ERROR', 25,
          [r'(?i)A single server tick took',
           r'ServerHangWatchdog',
           r'(?i)The server has stopped responding',
           r'(?i)has not responded for',
           r'(?i)is not responding'],
          '服务端主线程卡死或单刻耗时过长，看门狗已介入',
          '根据卡死时的线程堆栈定位卡点；常见原因：大型红石/实体过多、模组插件死锁、磁盘 IO 繁忙'),

    _rule('port_conflict', '端口占用/绑定失败', 'network', '网络', 'ERROR', 30,
          [r'(?i)Failed to bind to port',
           r'java\.net\.BindException',
           r'(?i)Address already in use',
           r'(?i)perhaps a server is already running on that port'],
          '服务端端口被占用或无法绑定，通常已有实例在运行',
          '执行 netstat -ano | findstr :<端口> 查找占用进程；或修改 server.properties 的 server-port'),

    _rule('eula_not_agreed', 'EULA 未同意', 'startup', '启动', 'ERROR', 35,
          [r'(?i)You need to agree to the EULA',
           r'(?i)eula=false'],
          'Minecraft 最终用户许可协议（EULA）未同意，服务端拒绝启动',
          '将 eula.txt 中的 eula=false 改为 eula=true 后重启'),

    _rule('java_version_mismatch', 'Java 版本不匹配', 'startup', '启动', 'ERROR', 40,
          [r'UnsupportedClassVersionError',
           r'(?i)has been compiled by a more recent version of the Java Runtime',
           r'Unsupported major\.minor version',
           r'(?i)class file has wrong version'],
          '核心/模组所需 Java 版本高于当前运行环境',
          '升级 Java（如 Paper 1.20.5+ 需 Java 21），或改用与核心匹配的版本'),

    _rule('java_startup_failure', 'Java 启动失败', 'startup', '启动', 'ERROR', 45,
          [r'(?i)Error: Could not find or load main class',
           r'(?i)Unrecognized (?:VM )?option',
           r'(?i)Invalid (?:initial|maximum) heap size',
           r'(?i)Error occurred during initialization of VM',
           r'(?i)Could not create the Java Virtual Machine',
           r'(?i)A fatal exception has occurred'],
          'Java 虚拟机或启动参数存在问题，进程未能启动',
          '检查启动命令与参数（内存、核心 jar 路径、JVM 选项）是否正确'),

    _rule('class_not_found', '缺失类/库', 'mod', '模组', 'ERROR', 47,
          [r'java\.lang\.NoClassDefFoundError',
           r'java\.lang\.ClassNotFoundException'],
          '运行所需的类或第三方库缺失，常因依赖冲突或不完整安装',
          '确认相关模组/插件及其前置库完整安装、版本匹配，无重复冲突'),

    _rule('mod_dependency_missing', '模组依赖缺失', 'mod', '模组', 'ERROR', 50,
          [r'(?i)Missing or unsupported mandatory dependenc',
           r'(?i)Mod file .+ is missing mods',
           r'ModResolutionException',
           r'(?i)Mod resolution failed'],
          '模组缺少必需的前置依赖，或依赖版本不匹配',
          '按提示安装缺失的前置模组，并确保版本相互兼容'),

    _rule('mod_load_failure', '模组加载失败', 'mod', '模组', 'ERROR', 52,
          [r'ModLoadingException',
           r'(?i)Exception in mod loading',
           r'(?i)Failed to create mod instance',
           r'FormattedException: Some of your mods'],
          '模组在加载/初始化阶段抛出异常',
          '定位报错模组后更新或移除；结合完整异常堆栈排查'),

    _rule('plugin_dependency_missing', '插件依赖缺失', 'plugin', '插件', 'ERROR', 55,
          [r'UnknownDependencyException',
           r'(?i)due to a missing plugin',
           r'(?i)Cannot load expansion .+ due to a missing plugin',
           r'(?i)Could not find .+ plugin'],
          '插件缺少其声明的前置/软依赖插件',
          '安装报错信息所提示的缺失插件，并核对版本兼容性'),

    _rule('ambiguous_plugin', '插件重复', 'plugin', '插件', 'ERROR', 57,
          [r'(?i)Ambiguous plugin name'],
          'plugins 目录下存在同名插件的多个 jar，核心无法确定加载哪一个',
          '删除重复的旧版本插件 jar，仅保留所需版本'),

    _rule('plugin_invalid', '插件加载失败', 'plugin', '插件', 'ERROR', 58,
          [r"(?i)Could not load '.+\.jar' in folder 'plugins'",
           r'(?i)InvalidDescriptionException',
           r'java\.util\.zip\.ZipException',
           r'(?i)Invalid plugin description'],
          '插件 jar 损坏、plugin.yml 不合法或与当前核心/Java 版本不兼容',
          '检查该插件是否完整、版本是否适配；损坏则重新下载，过期则升级'),

    _rule('plugin_error', '插件错误', 'plugin', '插件', 'ERROR', 60,
          [r'(?i)Error occurred while enabling',
           r'(?i)Error occurred while disabling',
           r'(?i)generated an exception',
           r'(?i)Could not pass event',
           r'(?i)Invalid plugin\.yml',
           r'org\.bukkit\.plugin\.[A-Za-z.]*\('],
          '插件在启用/停用、事件处理或任务执行中出错',
          '更新或移除报错插件；确认其与核心版本、前置库兼容'),

    _rule('nms_incompatible', '插件/核心版本不兼容', 'plugin', '插件', 'ERROR', 63,
          [r'(?i)Could not setup a NMS hook',
           r'(?i)Unsupported server version',
           r'(?i)this plugin is not compatible'],
          '插件依赖的 NMS/API 与当前服务端核心版本不匹配',
          '升级插件到适配当前核心的版本，或降级核心'),

    _rule('datapack_error', '数据包/配置错误', 'datapack', '数据包', 'ERROR', 65,
          [r'(?i)Errors in currently selected datapacks',
           r'(?i)Failed to load datapacks?',
           r'(?i)datapack .+ (?:caused|failed)',
           r'(?i)Couldn.t parse .+ from datapack',
           r'com\.google\.gson\.stream\.MalformedJsonException',
           r'com\.google\.gson\.JsonSyntaxException'],
          '数据包或数据驱动配置的 JSON 语法错误或加载失败',
          '按提示定位数据包中的 JSON 语法错误；临时移除后逐个排查'),

    _rule('world_load_failed', '世界加载失败', 'world', '世界', 'ERROR', 68,
          [r'(?i)Failed to \(pre\)load world',
           r'(?i)Could not load world',
           r'(?i)Unable to load world'],
          '世界加载阶段失败，可能因存档缺失/损坏或分区插件异常',
          '确认世界目录存在且未损坏；优先用备份恢复后重试'),

    _rule('world_chunk_error', '世界/区块错误', 'world', '世界', 'ERROR', 70,
          [r"(?i)Couldn't load chunk",
           r'(?i)corrupt\w* chunk',
           r'(?i)Failed to save chunk',
           r'(?i)Could not save the server',
           r'(?i)Wrong located chunk'],
          '世界存档或区块数据损坏/读写失败',
          '优先用备份恢复；或使用区块修复工具处理损坏区块；检查磁盘健康'),

    _rule('database_error', '数据库错误', 'database', '数据库', 'ERROR', 75,
          [r'(?i)Communications link failure',
           r'(?i)Access denied for user',
           r'(?i)Unknown database',
           r'(?i)Failed to (?:get|create) (?:a )?connection',
           r'java\.sql\.SQLException',
           r'(?i)HikariPool.*Exception'],
          '数据库连接或查询失败（常见于使用 MySQL 存储的插件）',
          '检查数据库地址/端口/账号密码与服务状态；核对连接池配置'),

    _rule('disk_error', '磁盘/IO 错误', 'disk', '磁盘', 'ERROR', 80,
          [r'(?i)No space left on device',
           r'(?i)Disk quota exceeded',
           r'(?i)Read-only file system',
           r'java\.nio\.file\.(?:AccessDenied|NoSuchFile|FileSystemException)'],
          '磁盘空间不足、权限不足或文件系统异常',
          '清理磁盘空间；确认服务端进程对目录拥有读写权限'),

    _rule('config_error', '配置文件错误', 'config', '配置', 'ERROR', 85,
          [r'(?i)while parsing a block mapping',
           r'(?i)while scanning',
           r'(?i)Invalid configuration',
           r'(?i)Failed to load configuration',
           r'(?i)Could not load .+\.yml',
           r'(?i)Cannot load file .+\.yml'],
          '配置文件语法或格式错误（YAML 缩进、字段类型等）',
          '按提示的路径/行号检查对应配置文件；YAML 不允许用 Tab 缩进'),

    _rule('auth_error', '玩家认证失败', 'auth', '认证', 'WARN', 90,
          [r'(?i)Failed to verify username',
           r'(?i)invalid session'],
          '玩家正版验证失败，可能是网络问题或非正版客户端',
          '检查服务器网络与 online-mode 设置；离线环境需关闭正版验证'),

    _rule('auth_service_unavailable', '正版认证服务不可用', 'auth', '认证', 'WARN', 92,
          [r'AuthenticationUnavailableException',
           r'MinecraftClientHttpException'],
          '无法访问 Mojang 正版认证服务器','检查服务器公网出站连接与 DNS；必要时使用离线模式或认证代理'),

    _rule('network_error', '网络连接异常', 'network', '网络', 'WARN', 95,
          [r'(?i)Connection reset',
           r'(?i)read timed out',
           r'(?i)Took too long to log in',
           r'java\.net\.SocketException',
           r'java\.net\.ConnectException',
           r'java\.net\.UnknownHostException',
           r'(?i)Internal Exception: java\.io\.IOException'],
          '客户端/服务端之间的网络连接中断、超时或 DNS 解析失败',
          '偶发可直接忽略；频繁出现请检查网络质量、防火墙与端口映射'),

    _rule('tick_overload', '服务器卡顿', 'performance', '性能', 'WARN', 100,
          [r"(?i)Can't keep up! Is the server overloaded",
           r'(?i)Running \d+\s*ms behind, skipping',
           r'(?i)Mod .+ took .+ to run a deferred task'],
          '服务器运行落后于预期，出现掉刻（TPS 下降）或模组任务耗时过长',
          '检查实体/红石数量，优化或移除高负载模组插件；必要时提升硬件'),

    _rule('offline_mode_warning', '离线模式警告', 'security', '安全', 'WARN', 102,
          [r'(?i)running in offline mode',
           r'(?i)离线模式',
           r'(?i)online-mode.*true'],
          '服务器处于离线模式，未进行正版身份验证，存在冒名风险',
          '确认是否需要离线模式；公网服务器建议开启 online-mode 或加装登录插件'),

    _rule('java_exception', 'Java 异常', 'exception', 'Java异常', 'ERROR', 110,
          [],
          '检测到未归类的 Java 异常堆栈',
          '根据异常类名与堆栈定位来源组件；更新相关模组/插件或向作者反馈'),

    _rule('generic_error', '错误级别日志', 'other', '其他', 'ERROR', 120,
          [],
          '检测到 ERROR 级别日志，未匹配到已知错误类型',
          '结合上下文日志人工判断；必要时搜索该错误信息'),

    _rule('generic_warn', '警告级别日志', 'other', '其他', 'WARN', 130,
          [],
          '检测到 WARN 级别日志，未匹配到已知错误类型',
          '通常不影响运行；频繁出现时可关注'),
]

RULES_BY_ID = {r.rid: r for r in RULES}
ORDERED_RULES = sorted(RULES, key=lambda r: r.priority)

FALLBACK_EXCEPTION_RID = 'java_exception'
FALLBACK_ERROR_RID = 'generic_error'
FALLBACK_WARN_RID = 'generic_warn'