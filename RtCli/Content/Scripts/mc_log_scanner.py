#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""RtCli 脚本集 — Minecraft 日志文件扫描统计。

项目调用契约（RtCli Scripts 引擎自动传入）:
    python -X utf8 mc_log_scanner.py "<日志文件或目录路径>" --event <事件名>

输入支持:
  - 日志文件路径（如 logs/latest.log，UTF-8/GBK 自动识别）
  - 目录路径（自动选取该目录或其 logs 子目录中最新的 .log 文件）
  - 直接传入日志文本

输出: 按类别聚合的错误统计报告打印到标准输出（由 RtCli 捕获并记录），
     附加 --json 参数时输出结构化 JSON；--top N 控制显示的问题类别数。

退出码: 0=扫描完成, 2=用法错误, 3=内部错误。
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mclog.cli import main

if __name__ == '__main__':
    sys.exit(main(sys.argv[1:], mode='scan', prog='mc_log_scanner.py'))
