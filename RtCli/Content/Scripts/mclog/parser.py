# -*- coding: utf-8 -*-
"""mclog 日志行解析器。

识别 Minecraft 服务端常见的日志行格式，提取时间戳 / 级别 / 线程 / Logger / 消息正文，
并从多行文本中提取 Java 异常链与堆栈帧。

支持的行格式:
  [12:34:56 INFO]: msg                                  Paper/Spigot 控制台
  12:34:56 [Server thread/INFO]: msg                    Vanilla 1.12+
  2026-09-19 12:34:56,789 [Server thread/INFO]: msg     log4j 文件格式
  [12:34:56] [Server thread/ERROR] [modid/LOGGER]: msg  Forge
  2026-09-19 12:34:56 [SEVERE] msg                      旧 Bukkit
  12:34:56 msg                                          旧 Vanilla 控制台
  (其他)                                                原样作为消息正文
"""

import re
from dataclasses import dataclass, field

ANSI_RE = re.compile(r'\x1b\[[0-9;]*[A-Za-z]')

SEVERITY_ORDER = {'DEBUG': 0, 'TRACE': 0, 'INFO': 1, 'WARN': 2, 'ERROR': 3, 'FATAL': 4}

_LEVEL_ALIASES = {
    'SEVERE': 'ERROR',
    'ERR': 'ERROR',
    'WARNING': 'WARN',
    'CONFIG': 'DEBUG',
    'FINE': 'DEBUG',
    'FINER': 'DEBUG',
    'FINEST': 'DEBUG',
}


@dataclass
class LogLine:
    raw: str
    message: str = ''
    timestamp: str = ''
    level: str = ''
    thread: str = ''
    logger: str = ''
    index: int = 0

    @property
    def normalized_level(self):
        lv = (self.level or '').strip().upper()
        lv = _LEVEL_ALIASES.get(lv, lv)
        return lv if lv in SEVERITY_ORDER else ''


@dataclass
class ExceptionInfo:
    exc_class: str
    exc_message: str
    line_index: int = -1
    origin: str = 'line'  # line / thread / caused_by / suppressed
    frames: list = field(default_factory=list)  # [(完整方法路径, 位置)]


_LINE_PATTERNS = [
    (re.compile(r'^\[(\d{2}:\d{2}:\d{2})\s+([A-Za-z]+)\]:\s?(.*)$'),
     ('ts', 'level', 'msg')),
    (re.compile(r'^\[(\d{2}:\d{2}:\d{2})\]\s*\[([^\]/]+?)(?:/([A-Za-z]+))?\]\s*(?:\[([^\]]*)\])?:\s?(.*)$'),
     ('ts', 'thread', 'level', 'logger', 'msg')),
    (re.compile(r'^(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2}:\d{2})[,.]\d+\s*\[([^\]/]+?)(?:/([A-Za-z]+))?\](?:\s*\[([^\]]*)\])?:\s?(.*)$'),
     ('date', 'ts', 'thread', 'level', 'logger', 'msg')),
    (re.compile(r'^(\d{2}:\d{2}:\d{2})\s*\[([^\]/]+?)(?:/([A-Za-z]+))?\](?:\s*\[([^\]]*)\])?:\s?(.*)$'),
     ('ts', 'thread', 'level', 'logger', 'msg')),
    (re.compile(r'^\d{4}-\d{2}-\d{2}[ T](\d{2}:\d{2}:\d{2})\s*\[([A-Za-z]+)\]\s?(.*)$'),
     ('ts', 'level', 'msg')),
    (re.compile(r'^(\d{2}:\d{2}:\d{2})\s+(.*)$'),
     ('ts', 'msg')),
]


def parse_line(raw, index=0):
    line = ANSI_RE.sub('', raw or '').rstrip('\r\n').rstrip()
    ll = LogLine(raw=line, index=index)
    if not line.strip():
        return ll
    for pattern, keys in _LINE_PATTERNS:
        m = pattern.match(line)
        if m:
            for key, val in zip(keys, m.groups()):
                val = (val or '').strip()
                if key == 'msg':
                    ll.message = val
                elif key == 'ts':
                    ll.timestamp = val
                elif key == 'thread':
                    ll.thread = val
                elif key == 'level':
                    ll.level = val
                elif key == 'logger':
                    ll.logger = val
            return ll
    ll.message = line.strip()
    return ll


def parse_lines(text):
    return [parse_line(rl, i) for i, rl in enumerate((text or '').splitlines())]


STACK_FRAME_RE = re.compile(r'^\s*at\s+([\w$.]+)\(([^)]*)\)')
CAUSED_BY_RE = re.compile(r'^Caused by:\s*(.*)$')
SUPPRESSED_RE = re.compile(r'^Suppressed:\s*(.*)$')
EXC_IN_THREAD_RE = re.compile(r'^Exception in thread "([^"]+)"\s*(.*)$')
EXCEPTION_HEAD_RE = re.compile(
    r'^((?:[\w$]+\.){1,}[A-Z][\w$]*(?:Exception|Error|Throwable))(?::\s?(.*))?$')
MORE_FRAMES_RE = re.compile(r'^\s*\.\.\.\s*\d+\s+more')

FRAME_NOISE_PREFIX = (
    'java.', 'jdk.', 'sun.', 'javax.',
    'org.apache.logging.', 'io.netty.',
)


def _match_head(s):
    m = EXCEPTION_HEAD_RE.match((s or '').strip())
    if m:
        return m.group(1), (m.group(2) or '').strip()
    return None


def _match_exception(stripped):
    m = EXC_IN_THREAD_RE.match(stripped)
    if m:
        head = _match_head(m.group(2))
        if head:
            return head[0], head[1], 'thread'
        return None
    m = CAUSED_BY_RE.match(stripped)
    if m:
        head = _match_head(m.group(1))
        if head:
            return head[0], head[1], 'caused_by'
        return None
    m = SUPPRESSED_RE.match(stripped)
    if m:
        head = _match_head(m.group(1))
        if head:
            return head[0], head[1], 'suppressed'
        return None
    if stripped.startswith('at ') or stripped.startswith('...'):
        return None
    head = _match_head(stripped)
    if head:
        return head[0], head[1], 'line'
    return None


def extract_exceptions(lines):
    exceptions = []
    current = None
    for ln in lines:
        stripped = (ln.message or ln.raw).strip()
        if not stripped:
            current = None
            continue
        if MORE_FRAMES_RE.match(ln.raw):
            continue
        fm = STACK_FRAME_RE.match(ln.raw)
        if fm:
            if current is not None:
                current.frames.append((fm.group(1), fm.group(2)))
            continue
        hit = _match_exception(stripped)
        if hit:
            exc = ExceptionInfo(exc_class=hit[0], exc_message=hit[1],
                                line_index=ln.index, origin=hit[2])
            exceptions.append(exc)
            current = exc
        elif ln.raw[:1] in (' ', '\t'):
            continue
        else:
            current = None
    return exceptions


def pick_root(exceptions):
    """根因 = 最后一个 Caused by；无链时取首个异常。"""
    if not exceptions:
        return None
    for exc in reversed(exceptions):
        if exc.origin == 'caused_by':
            return exc
    return exceptions[0]


def pick_frames(exc, max_frames=3):
    """优先返回非框架噪声的堆栈帧（更可能指向模组/插件代码）。"""
    if exc is None:
        return []
    frames = exc.frames or []
    app = [f for f in frames if not f[0].startswith(FRAME_NOISE_PREFIX)]
    chosen = app if app else frames
    return chosen[:max_frames]
