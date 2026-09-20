# -*- coding: utf-8 -*-
"""mclog 通用命令行接口。

对接 RtCli Scripts 引擎的调用契约:
    python -X utf8 <脚本路径> "<input>" --event <事件名>

- input 为位置参数；玩家事件触发时 RtCli 会传入事件参数 JSON
  （含 event_name / player_name / message 等字段），此处自动提取其中的文本字段
- input 若是已存在的文件路径（如日志文件），自动读取文件内容
- 标准输出被 RtCli 捕获并写入日志；退出码 0=正常，2=用法错误，3=内部错误
"""

import argparse
import json
import os
import sys
import traceback

from .engine import analyze, scan
from .formatter import format_json, format_scan_text, format_text

MAX_FILE_BYTES = 64 * 1024 * 1024
USAGE_HINT = ('用法: python mc_error_analyzer.py "<控制台信息>" '
              '[--json] [--file 日志路径] [--tail N] [--selftest]')

EVENT_TEXT_KEYS = ('message', 'command', 'player_lost_reason')


class UsageError(Exception):
    pass


def _u8_stdio():
    for stream in (sys.stdout, sys.stderr):
        try:
            if stream and hasattr(stream, 'reconfigure'):
                stream.reconfigure(encoding='utf-8', errors='replace')
        except Exception:
            pass


def read_text_file(path):
    if not os.path.isfile(path):
        raise UsageError('文件不存在: {0}'.format(path))
    size = os.path.getsize(path)
    if size > MAX_FILE_BYTES:
        raise UsageError('文件过大({0}字节, 上限{1}字节): {2}'.format(size, MAX_FILE_BYTES, path))
    with open(path, 'rb') as fh:
        data = fh.read()
    for enc in ('utf-8-sig', 'utf-8', 'gbk'):
        try:
            return data.decode(enc)
        except (UnicodeDecodeError, LookupError):
            continue
    return data.decode('utf-8', errors='replace')


def extract_event_text(raw):
    """若输入是 RtCli 玩家事件 JSON，提取其中可分析的文本字段。"""
    stripped = (raw or '').strip()
    if not stripped.startswith('{'):
        return raw, False
    try:
        obj = json.loads(stripped)
    except ValueError:
        return raw, False
    if not isinstance(obj, dict):
        return raw, False
    for key in EVENT_TEXT_KEYS:
        val = obj.get(key)
        if isinstance(val, str) and val.strip():
            return val, True
    return raw, True


def resolve_input_text(args):
    if getattr(args, 'file', None):
        return read_text_file(args.file), 'file'
    if getattr(args, 'stdin', False):
        return sys.stdin.read(), 'stdin'
    raw = ' '.join(args.input or []).strip()
    if not raw:
        return '', 'empty'
    if os.path.isfile(raw):
        return read_text_file(raw), 'file'
    text, is_event = extract_event_text(raw)
    return text, ('event_json' if is_event else 'text')


def build_parser(prog, description, scanner=False):
    p = argparse.ArgumentParser(prog=prog, description=description,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('input', nargs='*', help='输入内容：控制台信息文本、事件JSON或日志文件路径')
    p.add_argument('--event', nargs='?', default='', help='RtCli 事件名称（由项目自动传入）')
    p.add_argument('--file', metavar='PATH', help='从指定文件读取日志')
    p.add_argument('--stdin', action='store_true', help='从标准输入读取')
    p.add_argument('--json', action='store_true', help='以 JSON 格式输出结果')
    p.add_argument('--tail', type=int, default=0, metavar='N', help='仅分析最后 N 行')
    p.add_argument('--selftest', action='store_true', help='运行内置自检样例')
    if scanner:
        p.add_argument('--top', type=int, default=5, metavar='N', help='最多显示 N 类问题')
    return p


def _find_newest_log(directory):
    candidates = []
    for sub in ('', 'logs'):
        base = os.path.join(directory, sub) if sub else directory
        if not os.path.isdir(base):
            continue
        for name in os.listdir(base):
            if name.lower().endswith('.log'):
                full = os.path.join(base, name)
                if os.path.isfile(full):
                    candidates.append(full)
    if not candidates:
        raise UsageError('目录中未找到 .log 文件: {0}'.format(directory))
    return max(candidates, key=os.path.getmtime)


def _resolve_scan_target(args):
    """返回 (text, target)。路径(文件/目录)优先，否则视为日志文本。"""
    raw = ' '.join(args.input or []).strip()
    if getattr(args, 'file', None):
        path = args.file
    elif raw and (os.path.isdir(raw) or os.path.isfile(raw)):
        path = raw
    else:
        path = ''
    if path:
        if os.path.isdir(path):
            path = _find_newest_log(path)
        return read_text_file(path), path
    if getattr(args, 'stdin', False):
        return sys.stdin.read(), 'stdin'
    if raw:
        return raw, 'text'
    raise UsageError('未提供扫描目标：请传入日志文件/目录路径，或使用 --file/--stdin')


def run_selftest(json_out=False):
    from .selftest import run as _run
    return _run(json_out=json_out)


def main(argv, mode='analyze', prog=None):
    _u8_stdio()
    if mode == 'analyze':
        prog = prog or 'mc_error_analyzer.py'
        description = 'Minecraft 控制台错误分析（RtCli 脚本集）\n' + USAGE_HINT
    else:
        prog = prog or 'mc_log_scanner.py'
        description = 'Minecraft 日志文件扫描统计（RtCli 脚本集）'
    parser = build_parser(prog, description, scanner=(mode == 'scan'))
    args = parser.parse_args(argv)

    try:
        if args.selftest:
            return run_selftest(json_out=args.json)
        if args.tail > 0:
            pass

        if mode == 'analyze':
            text, kind = resolve_input_text(args)
            if not text.strip():
                print('[MC控制台错误分析] 未提供输入 — {0}'.format(USAGE_HINT))
                return 0
            if args.tail > 0:
                text = '\n'.join(text.splitlines()[-args.tail:])
            result = analyze(text, event=args.event or '')
            result['input_kind'] = kind
            print(format_json(result) if args.json else format_text(result))
            return 0

        text, target = _resolve_scan_target(args)
        if args.tail > 0:
            text = '\n'.join(text.splitlines()[-args.tail:])
        result = scan(text, target=target, top=getattr(args, 'top', 5))
        print(format_json(result) if args.json else format_scan_text(result))
        return 0
    except UsageError as ex:
        sys.stderr.write('[用法错误] {0}\n{1}\n'.format(ex, USAGE_HINT))
        return 2
    except Exception:
        sys.stderr.write('[内部错误]\n{0}\n'.format(traceback.format_exc()))
        return 3
