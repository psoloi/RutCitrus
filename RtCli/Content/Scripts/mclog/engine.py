# -*- coding: utf-8 -*-
"""mclog 分析引擎：控制台文本 / 日志文本 -> 结构化错误识别结果。"""

import re
from collections import Counter

from .parser import (
    SEVERITY_ORDER, STACK_FRAME_RE, extract_exceptions, parse_line, parse_lines,
    pick_frames, pick_root,
)
from .rules import (
    FALLBACK_ERROR_RID, FALLBACK_EXCEPTION_RID, FALLBACK_WARN_RID,
    ORDERED_RULES, RULES_BY_ID,
)

EXC_IN_TEXT_RE = re.compile(r'((?:[\w$]+\.){1,}[A-Z][\w$]*(?:Exception|Error|Throwable))')
DESCRIPTION_RE = re.compile(r'(?i)^\s*Description:\s*(.+)$')
PLUGIN_JAR_RE = re.compile(r'(?i)(?:plugins[/\\])([\w.\- ]+?\.jar)')
MODS_JAR_RE = re.compile(r'(?i)(?:mods[/\\])([\w.\- ]+?\.jar)')
SYSTEM_LOGGERS = {
    'minecraft', 'server', 'main', 'system', 'fml', 'modlauncher', 'loading',
    'launch', 'std', 'netty', 'java', 'jvm', 'mixin', 'paper', 'bukkit',
    'spigot', 'craftbukkit', 'sponge', 'vanilla', 'serverthread',
}

SEVERITY_LABEL = {'FATAL': '致命', 'ERROR': '错误', 'WARN': '警告', 'INFO': '信息'}

# 噪声行：看门狗线程转储、堆栈帧、Windows 性能计数器等，不应计入错误统计
_NOISE_RE = re.compile(
    r'Current Thread:'
    r'|^\s*PID:\s*\d+'
    r'|Thread is waiting on monitor'
    r'|\b(?:Stack|Locked on|Suspended|Native|State):\s'
    r'|Memory using:'
    r'|Server thread dump'
    r'|Entire Thread Dump'
    r'|\bPDH (?:Counter|counter)'
    r'|COM exception'
    r'|WmiQueryHandler'
    r'|PerfCounter'
    r'|DO NOT REPORT THIS'
    r'|^\s*(?:-{4,}|={4,})\s*$'
    r'|^\s+\S+\([^)]*\)\s*$',
    re.IGNORECASE,
)


def _is_noise(line):
    raw = line.raw
    if len(raw.strip()) < 4:
        return True
    return _NOISE_RE.search(raw) is not None


def _sev_rank(sev):
    return SEVERITY_ORDER.get(sev, 0)


def _sev_max(a, b):
    if not a:
        return b
    if not b:
        return a
    return a if _sev_rank(a) >= _sev_rank(b) else b


def _clip(s, n=240):
    s = (s or '').strip()
    if len(s) <= n:
        return s
    return s[:max(0, n - 3)] + '...'


def _base_result(event=''):
    return {
        'ok': True,
        'is_error': False,
        'severity': '',
        'rule_id': '',
        'rule_name': '',
        'category_id': '',
        'category_name': '',
        'error_type': '',
        'error_message': '',
        'cause': '',
        'suggestion': '',
        'source': '',
        'timestamp': '',
        'matched_line': '',
        'stack_summary': [],
        'exception_chain': [],
        'other_issues': [],
        'level_stats': {},
        'lines_total': 0,
        'lines_scanned': 0,
        'event': event or '',
    }


def _detect_source(lines, root_exc, primary_line):
    for ln in lines:
        m = PLUGIN_JAR_RE.search(ln.raw)
        if m:
            return '插件 {}'.format(m.group(1))
    for ln in lines:
        m = MODS_JAR_RE.search(ln.raw)
        if m:
            return '模组 {}'.format(m.group(1))
    for ln in lines:
        if ln.logger:
            head = ln.logger.split('/')[0].strip()
            if head and head.lower() not in SYSTEM_LOGGERS:
                return '模组/组件 {}'.format(head)
    frames = pick_frames(root_exc) if root_exc else []
    if frames:
        pkg = frames[0][0]
        if pkg.startswith(('net.minecraft', 'com.mojang')):
            return '服务端核心 (net.minecraft)'
        if pkg.startswith('io.netty'):
            return '网络库 (io.netty)'
        return '第三方组件 ({})'.format(pkg)
    if primary_line is not None:
        if ('Exception in thread "main"' in primary_line.raw
                or 'Could not find or load main class' in primary_line.raw):
            return 'Java 运行环境'
    return '服务端'


def analyze(text, event='', max_other=5):
    result = _base_result(event)
    all_lines = parse_lines(text)
    lines = [ln for ln in all_lines if ln.raw.strip() and not _is_noise(ln)]
    result['lines_total'] = len(all_lines)
    result['lines_scanned'] = len(lines)
    if not lines:
        return result

    level_stats = Counter()
    for ln in lines:
        if ln.normalized_level:
            level_stats[ln.normalized_level] += 1
    result['level_stats'] = dict(level_stats)
    max_level = ''
    for lv in ('FATAL', 'ERROR', 'WARN', 'INFO'):
        if lv in level_stats:
            max_level = lv
            break

    exceptions = extract_exceptions(all_lines)
    root_exc = pick_root(exceptions)

    # 每行只归属优先级最高且首个命中的规则；首个出现的主规则即主判定
    line_rule = {}
    for rule in ORDERED_RULES:
        if not rule.patterns:
            continue
        for ln in lines:
            if ln.index in line_rule:
                continue
            if rule.match(ln.raw):
                line_rule[ln.index] = rule.rid

    primary_rid = ''
    primary_line = None
    if line_rule:
        for rule in ORDERED_RULES:
            candidates = [all_lines[idx] for idx, rid in line_rule.items() if rid == rule.rid]
            if candidates:
                primary_rid = rule.rid
                non_frame = [ln for ln in candidates if not STACK_FRAME_RE.match(ln.raw)]
                primary_line = non_frame[0] if non_frame else candidates[0]
                break
        if primary_line is not None and STACK_FRAME_RE.match(primary_line.raw) and root_exc is not None:
            primary_line = all_lines[root_exc.line_index]

    if not primary_rid:
        if root_exc is not None:
            primary_rid = FALLBACK_EXCEPTION_RID
            primary_line = all_lines[root_exc.line_index]
        elif max_level in ('ERROR', 'FATAL'):
            primary_rid = FALLBACK_ERROR_RID
            primary_line = next((ln for ln in lines if ln.normalized_level == max_level), lines[0])
        elif max_level == 'WARN':
            primary_rid = FALLBACK_WARN_RID
            primary_line = next((ln for ln in lines if ln.normalized_level == 'WARN'), lines[0])

    if not primary_rid:
        return result

    rule = RULES_BY_ID[primary_rid]
    severity = rule.severity
    if max_level:
        severity = _sev_max(severity, max_level)

    error_type = ''
    error_message = ''
    stack_summary = []
    exception_chain = [('{0}: {1}'.format(e.exc_class, e.exc_message)).strip(': ')
                       for e in exceptions[:5]]

    if primary_rid == FALLBACK_EXCEPTION_RID and root_exc is not None:
        error_type = root_exc.exc_class
        error_message = root_exc.exc_message
        stack_summary = ['{0}({1})'.format(cls, loc)
                         for cls, loc in pick_frames(root_exc)]
    else:
        m = EXC_IN_TEXT_RE.search(primary_line.raw)
        if m:
            error_type = m.group(1)
        if primary_rid == 'crash_report':
            desc = ''
            for ln in lines:
                dm = DESCRIPTION_RE.match(ln.raw)
                if dm:
                    desc = dm.group(1).strip()
                    break
            if root_exc is not None:
                error_type = error_type or root_exc.exc_class
                error_message = root_exc.exc_message or desc
            elif desc:
                error_type = error_type or 'Minecraft Crash Report'
                error_message = desc
        if root_exc is not None and primary_line is not None and root_exc.line_index == primary_line.index:
            error_message = root_exc.exc_message
        if not error_message:
            error_message = primary_line.message or primary_line.raw
        if root_exc is not None and not stack_summary:
            stack_summary = ['{0}({1})'.format(cls, loc)
                             for cls, loc in pick_frames(root_exc)]

    other_counts = {}
    for idx, rid in line_rule.items():
        if rid == primary_rid:
            continue
        entry = other_counts.setdefault(rid, {'count': 0, 'sample': all_lines[idx].raw})
        entry['count'] += 1
    other_issues = []
    for rule2 in ORDERED_RULES:
        if rule2.rid in other_counts:
            e = other_counts[rule2.rid]
            other_issues.append({
                'rule_id': rule2.rid,
                'name': rule2.name,
                'severity': rule2.severity,
                'count': e['count'],
                'sample': _clip(e['sample'], 160),
            })
    other_issues = other_issues[:max_other]

    result.update({
        'is_error': True,
        'severity': severity,
        'rule_id': rule.rid,
        'rule_name': rule.name,
        'category_id': rule.category_id,
        'category_name': rule.category_name,
        'error_type': error_type or rule.name,
        'error_message': _clip(error_message, 300),
        'cause': rule.cause,
        'suggestion': rule.suggestion,
        'source': _detect_source(lines, root_exc, primary_line),
        'timestamp': primary_line.timestamp or '',
        'matched_line': _clip(primary_line.raw),
        'stack_summary': stack_summary,
        'exception_chain': exception_chain,
        'other_issues': other_issues,
    })
    return result


def analyze_line(line_text, event=''):
    return analyze(line_text, event=event)


def scan(text, target='', top=5):
    """扫描整份日志文本，返回按类别聚合的错误统计报告。"""
    all_lines = parse_lines(text)
    lines = [ln for ln in all_lines if ln.raw.strip() and not _is_noise(ln)]
    result = {
        'ok': True,
        'target': target or '',
        'lines_total': len(all_lines),
        'lines_scanned': len(lines),
        'level_stats': {},
        'time_range': [],
        'issue_total': 0,
        'issues': [],
    }
    if not lines:
        return result

    level_stats = Counter()
    timestamps = [ln.timestamp for ln in lines if ln.timestamp]
    for ln in lines:
        if ln.normalized_level:
            level_stats[ln.normalized_level] += 1
    result['level_stats'] = dict(level_stats)
    if timestamps:
        result['time_range'] = [timestamps[0], timestamps[-1]]

    line_rule = {}
    for rule in ORDERED_RULES:
        if not rule.patterns:
            continue
        for ln in lines:
            if ln.index in line_rule:
                continue
            if rule.match(ln.raw):
                line_rule[ln.index] = rule.rid

    # 兜底：异常行未命中规则时归入 java_exception；其余按级别归入 generic_*
    exceptions = extract_exceptions(all_lines)
    exc_line_ids = {e.line_index for e in exceptions}
    for ln in lines:
        if ln.index in line_rule or ln.index in exc_line_ids:
            continue
        lv = ln.normalized_level
        if lv in ('ERROR', 'FATAL'):
            line_rule[ln.index] = FALLBACK_ERROR_RID
        elif lv == 'WARN':
            line_rule[ln.index] = FALLBACK_WARN_RID
    for e in exceptions:
        if e.line_index not in line_rule:
            line_rule[e.line_index] = FALLBACK_EXCEPTION_RID

    stats = {}
    for idx, rid in line_rule.items():
        rule = RULES_BY_ID[rid]
        entry = stats.setdefault(rid, {
            'rule': rule, 'count': 0, 'sample': '', 'first_ts': '',
        })
        entry['count'] += 1
        if not entry['sample']:
            entry['sample'] = _clip(all_lines[idx].raw, 160)
            entry['first_ts'] = all_lines[idx].timestamp

    issues = []
    for rid, entry in stats.items():
        rule = entry['rule']
        issues.append({
            'rule_id': rid,
            'name': rule.name,
            'category_name': rule.category_name,
            'severity': rule.severity,
            'count': entry['count'],
            'first_time': entry['first_ts'],
            'sample': entry['sample'],
            'suggestion': rule.suggestion,
        })
    issues.sort(key=lambda i: (-_sev_rank(i['severity']), -i['count']))
    result['issue_total'] = sum(i['count'] for i in issues)
    result['issues'] = issues[:top] if top > 0 else issues
    return result
