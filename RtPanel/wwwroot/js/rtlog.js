// MC / RtCli 日志渲染器(控制台页与实例日志共用)
// 关键点: 多行错误的续行(堆栈 at ... / Caused by: / ... N more)必须并入上一条错误块,
//         否则堆栈会散成一堆无颜色的独立行 —— 之前的实现就是逐行正则着色, 漏掉了续行。
window.RtLog = (function () {
    // RtCli 包装格式: [21:47:48] |错误| (来源) 消息
    var RT_RE = /^\[(\d{2}:\d{2}:\d{2})\]\s*\|([^|]*)\|\s*\(([^)]*)\)\s?([\s\S]*)$/;
    // MC 原生格式: [21:47:48] [Server thread/INFO]: msg  或  [21:47:48 INFO]: msg
    var MC_RE = /^\[(\d{2}:\d{2}:\d{2})\]\s*(?:\[([^\]\/]*?)\/([A-Za-z]+)\]|\[([A-Za-z]+)\])\s*:?\s?([\s\S]*)$/;

    function esc(s) {
        return (s === null || s === undefined ? '' : String(s)).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function levelOf(name) {
        var n = (name || '').toUpperCase();
        if (n.indexOf('FATAL') >= 0) return 'fatal';
        if (n.indexOf('ERROR') >= 0 || n.indexOf('SEVERE') >= 0) return 'error';
        if (n.indexOf('WARN') >= 0) return 'warn';
        if (n.indexOf('DEBUG') >= 0 || n.indexOf('TRACE') >= 0) return 'debug';
        return 'info';
    }

    // 解析一行; 返回 null 表示这是"续行"(没有时间戳前缀)
    function parseLine(raw) {
        var m = RT_RE.exec(raw);
        if (m) {
            var inner = m[4] || '';
            var mc = MC_RE.exec(inner);
            if (mc) {
                return { kind: 'mc', time: mc[1], thread: mc[2] || '', level: levelOf(mc[3] || mc[4]), msg: mc[5] || '' };
            }
            return { kind: 'rt', time: m[1], level: levelOf(m[2]), source: (m[3] || '').trim(), msg: inner };
        }
        var mc2 = MC_RE.exec(raw);
        if (mc2) {
            return { kind: 'mc', time: mc2[1], thread: mc2[2] || '', level: levelOf(mc2[3] || mc2[4]), msg: mc2[5] || '' };
        }
        return null;
    }

    function renderBlock(b) {
        var h = b.head, lv = h.level || 'info';
        var out = '<div class="rtlog-line rtlog-' + lv + '">';
        if (h.kind === 'mc') {
            out += '<span class="rtlog-time">' + esc(h.time) + '</span>';
            if (h.thread) out += '<span class="rtlog-thread">[' + esc(h.thread) + ']</span>';
            out += '<span class="rtlog-msg">' + esc(h.msg) + '</span>';
        } else if (h.kind === 'rt') {
            out += '<span class="rtlog-time">' + esc(h.time) + '</span>';
            if (h.source) out += '<span class="rtlog-src">(' + esc(h.source) + ')</span>';
            out += '<span class="rtlog-msg">' + esc(h.msg) + '</span>';
        } else {
            out += '<span class="rtlog-msg">' + (esc(h.msg) || '&nbsp;') + '</span>';
        }
        if (b.cont.length) {
            // 续行继承所属块的级别 -> 错误堆栈整段都是红色
            out += '<div class="rtlog-cont">' +
                b.cont.map(function (l) { return '<div>' + (esc(l) || '&nbsp;') + '</div>'; }).join('') +
                '</div>';
        }
        return out + '</div>';
    }

    function render(lines) {
        var blocks = [];
        (lines || []).forEach(function (raw) {
            var p = parseLine(raw);
            if (p) { blocks.push({ head: p, cont: [] }); }
            else if (blocks.length) { blocks[blocks.length - 1].cont.push(raw); }
            else { blocks.push({ head: { kind: 'plain', level: 'info', msg: raw }, cont: [] }); }
        });
        return blocks.map(renderBlock).join('');
    }

    return { render: render, parseLine: parseLine, levelOf: levelOf, esc: esc };
})();
