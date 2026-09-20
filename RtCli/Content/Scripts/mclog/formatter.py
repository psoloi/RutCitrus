# -*- coding: utf-8 -*-
"""mclog 输出格式化：分析结果 -> 中文文本 / JSON。

文本输出会被 RtCli 的 Scripts 引擎捕获（[Python:脚本名] 前缀）并写入日志，
因此默认保持简洁：一行结论 + 原因 + 建议，最多三行。
完整字段（堆栈/来源/异常链等）请用 --json 输出。
"""

import json

SEV_LABEL = {'FATAL': '致命', 'ERROR': '错误', 'WARN': '警告', 'INFO': '信息'}


def _clip(s, n=200):
    s = (s or '').strip()
    if len(s) <= n:
        return s
    return s[:max(0, n - 3)] + '...'


def _tag(severity):
    return SEV_LABEL.get(severity, '错误')


def format_text(r):
    if not r.get('is_error'):
        stats = r.get('level_stats') or {}
        warn = stats.get('WARN', 0)
        if warn:
            return '[警告] 未发现错误，存在 {0} 条 WARN 日志'.format(warn)
        return '[正常] 未检测到错误'

    sev = r.get('severity', '')
    msg = _clip(r.get('error_message') or r.get('error_type', ''), 160)
    lines = ['[{0}] {1}: {2}'.format(
        _tag(sev), r.get('rule_name', ''), msg)]
    if r.get('cause'):
        lines.append('  原因: {0}'.format(r['cause']))
    if r.get('suggestion'):
        lines.append('  建议: {0}'.format(r['suggestion']))
    return '\n'.join(lines)


def format_scan_text(r):
    stats = r.get('level_stats') or {}
    stat_txt = ' '.join('{0}×{1}'.format(k, v) for k, v in sorted(stats.items()))
    out = ['[扫描] {0}'.format(r.get('target') or '(未指定)')]
    out.append('  {0} 行 | {1}'.format(r.get('lines_scanned', 0), stat_txt or '无级别'))
    issues = r.get('issues') or []
    if not issues:
        out.append('  未发现错误或警告')
        return '\n'.join(out)
    out.append('  共 {0} 类问题:'.format(len(issues)))
    for i, iss in enumerate(issues, 1):
        out.append('    {0}. [{1}] {2} ×{3}'.format(
            i, SEV_LABEL.get(iss['severity'], iss['severity']), iss['name'], iss['count']))
    out.append('  首要建议: {0}'.format(issues[0].get('suggestion', '')))
    return '\n'.join(out)


def format_json(obj):
    return json.dumps(obj, ensure_ascii=False, indent=2)