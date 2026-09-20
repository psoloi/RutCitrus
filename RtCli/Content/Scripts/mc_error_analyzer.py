#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""RtCli 脚本集 — Minecraft 控制台错误分析（主入口）。

项目调用契约（RtCli Scripts 引擎自动传入）:
    python -X utf8 mc_error_analyzer.py "<控制台信息>" --event <事件名>

输入支持:
  - 控制台信息文本（单行或多行，由项目/AI 通过 input 传入）
  - RtCli 玩家事件参数 JSON（事件触发时自动传入，自动提取 message 等字段）
  - 已存在的日志文件路径（自动读取文件内容分析）

输出: 分析结果打印到标准输出（由 RtCli 捕获并记录为日志），
     附加 --json 参数时输出结构化 JSON。

退出码: 0=分析完成（无论是否检测到错误）, 2=用法错误, 3=内部错误。
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mclog.cli import main

if __name__ == '__main__':
    sys.exit(main(sys.argv[1:], mode='analyze', prog='mc_error_analyzer.py'))
