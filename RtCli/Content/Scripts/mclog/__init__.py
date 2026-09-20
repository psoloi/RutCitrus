# -*- coding: utf-8 -*-
"""mclog — RtCli Minecraft 控制台日志分析库。

对外 Python API:
    from mclog import analyze, analyze_line, scan
    result = analyze("控制台文本")   # -> dict
"""

from .engine import analyze, analyze_line, scan
from .formatter import format_json, format_scan_text, format_text
from .parser import parse_line, parse_lines

__version__ = '1.0.0'

__all__ = [
    'analyze', 'analyze_line', 'scan',
    'parse_line', 'parse_lines',
    'format_text', 'format_scan_text', 'format_json',
    '__version__',
]
