# -*- coding: utf-8 -*-
"""mclog 内置自检：用代表性样例验证引擎识别结果。"""

from .cli import extract_event_text
from .engine import analyze
from .formatter import format_json

SELFTEST_CASES = [
    ('内存不足',
     '12:34:56 [Server thread/ERROR]: java.lang.OutOfMemoryError: Java heap space\n'
     '\tat net.minecraft.server.MinecraftServer.tickChildren(MinecraftServer.java:900)',
     'out_of_memory'),
    ('端口占用',
     '[12:34:56 ERROR]: Failed to bind to port\n'
     'java.net.BindException: Address already in use\n'
     '[12:34:56 ERROR]: Perhaps a server is already running on that port?',
     'port_conflict'),
    ('崩溃报告',
     '[13:00:00] [Server thread/FATAL]: Minecraft crash report\n'
     '---- Minecraft Crash Report ----\n'
     'Description: Ticking block entity',
     'crash_report'),
    ('EULA未同意',
     '[12:00:00 INFO]: You need to agree to the EULA in order to run the server. Go to eula.txt for more info.',
     'eula_not_agreed'),
    ('Java版本不匹配',
     'java.lang.UnsupportedClassVersionError: com/example/Foo has been compiled by a more recent '
     'version of the Java Runtime (class file version 65.0), this version of the Java Runtime only '
     'recognizes class file versions up to 61.0',
     'java_version_mismatch'),
    ('模组依赖缺失',
     'Caused by: net.fabricmc.loader.impl.FormattedException: Some of your mods caused a problem\n'
     '\tMissing or unsupported mandatory dependencies:\n'
     '\t  - fabric-api',
     'mod_dependency_missing'),
    ('缺失类/库',
     'java.lang.NoClassDefFoundError: com/github/benmanes/caffeine/cache/Caffeine',
     'class_not_found'),
    ('插件依赖缺失',
     '[12:00:00 ERROR]: Cannot load expansion playerpoints due to a missing plugin: PlayerPoints',
     'plugin_dependency_missing'),
    ('插件重复',
     "[12:00:00 ERROR]: Ambiguous plugin name 'CMILib' for files 'plugins\\CMILib1.4.6.1.jar' and 'plugins\\CMILib1.4.4.2.jar' in 'plugins'",
     'ambiguous_plugin'),
    ('插件加载失败',
     "[12:00:00 ERROR]: Could not load 'plugins\\Matrix_7.3.12A.jar' in folder 'plugins'",
     'plugin_invalid'),
    ('插件/核心版本不兼容',
     '[12:00:00 ERROR]: Could not setup a NMS hook for your server version!',
     'nms_incompatible'),
    ('数据包JSON错误',
     '[Worker-Main-2/ERROR]: Couldn.t parse supply station config from datapack: dragonrise_reforge:default\n'
     'Caused by: com.google.gson.stream.MalformedJsonException: Expected name at line 362',
     'datapack_error'),
    ('世界加载失败',
     "[12:00:00 ERROR]: Failed to (pre)load world: em_adventurers_guild",
     'world_load_failed'),
    ('插件错误',
     '[12:00:00 ERROR]: Error occurred while enabling MyPlugin v1.0 (Is it up to date?)\n'
     'java.lang.NullPointerException: Cannot invoke "X.y()" because the return value of "Z.w()" is null',
     'plugin_error'),
    ('世界区块错误',
     "[12:00:00 ERROR]: Couldn't load chunk 12, 34, in world 'world'",
     'world_chunk_error'),
    ('数据库错误',
     '[12:00:00 ERROR]: HikariPool-1 - Exception during pool initialization\n'
     'com.mysql.cj.jdbc.exceptions.CommunicationsException: Communications link failure',
     'database_error'),
    ('磁盘空间不足',
     'java.io.IOException: No space left on device',
     'disk_error'),
    ('服务器卡顿',
     "[12:00:01 WARN]: Can't keep up! Is the server overloaded? Running 21415ms behind, skipping 427 tick(s)",
     'tick_overload'),
    ('看门狗/单刻超时',
     '[12:00:00] [Server thread/WARN]: A single server tick took 60.00 seconds (should be max 0.05)',
     'server_unresponsive'),
    ('未归类Java异常',
     '12:34:56 [Server thread/ERROR]: java.lang.IllegalStateException: The world is unloaded\n'
     '\tat net.minecraft.server.MinecraftServer.a(MinecraftServer.java:50)',
     'java_exception'),
    ('正版认证服务不可用',
     '[12:00:00 WARN]: com.mojang.authlib.exceptions.AuthenticationUnavailableException: The authentication servers are currently not reachable',
     'auth_service_unavailable'),
    ('离线模式警告',
     '[12:00:00 WARN]: The server is running in offline mode! This is a security risk',
     'offline_mode_warning'),
    ('看门狗-服务器停止响应',
     '[12:10:00 WARN]: The server has stopped responding!',
     'server_unresponsive'),
    ('正常启动完成',
     '[12:00:00 INFO]: Done (3.503s)! For help, type "help"',
     None),
    ('玩家聊天事件JSON',
     '{"event_name":"PlayerChatEvent","player_name":"Steve","message":"hello world"}',
     None),
]

EVENT_JSON_CASE = ('{"event_name":"PlayerLostEvent","player_name":"Alex",'
                   '"player_trigger_time":"12:00:00","player_lost_reason":"Internal Exception: '
                   'java.io.IOException: Connection reset by peer"}',
                   'network_error')


def run(json_out=False):
    passed = 0
    failed = 0
    rows = []

    json_text, _ = extract_event_text(EVENT_JSON_CASE[0])
    cases = list(SELFTEST_CASES) + [('玩家掉线事件JSON', json_text, EVENT_JSON_CASE[1])]

    for name, text, expected in cases:
        result = analyze(text)
        actual = result.get('rule_id') if result.get('is_error') else None
        ok = (actual == expected)
        if ok:
            passed += 1
        else:
            failed += 1
        rows.append({
            'case': name,
            'expected': expected or '无错误',
            'actual': actual or ('无错误' if not result.get('is_error') else result.get('severity', '')),
            'ok': ok,
        })

    if json_out:
        print(format_json({'passed': passed, 'failed': failed, 'cases': rows}))
    else:
        for row in rows:
            mark = 'PASS' if row['ok'] else 'FAIL'
            print('[{0}] {1}: 期望 {2} / 实际 {3}'.format(
                mark, row['case'], row['expected'], row['actual']))
        print('自检完成: {0} 通过, {1} 失败'.format(passed, failed))
    return 0 if failed == 0 else 1
