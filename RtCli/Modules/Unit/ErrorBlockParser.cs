using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 错误块切分规则单一来源: 命中 Handler 正则 = 块开始;
    /// 堆栈行(at /Caused by/.../Suppressed)与空白行 = 块延续; 其他行 = 块结束。
    /// ErrorStreamDetector(实时流式识别)与面板的实例日志分析共用此规则, 避免两套实现行为分叉。
    /// </summary>
    internal static class ErrorBlockParser
    {
        /// <summary>编译 Handler 正则; 模式为空或无效时返回 null(调用方按配置错误处理)</summary>
        public static Regex? CreateHandlerRegex(string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return null;
            try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase); }
            catch { return null; }
        }

        /// <summary>是否为错误块的延续行(堆栈/异常链/空白)。handlerRegex 用于兜底命中(理论上到不了)</summary>
        public static bool IsContinuation(string line, Regex handlerRegex)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("at ", StringComparison.Ordinal)
                || trimmed.StartsWith("Caused by", StringComparison.Ordinal)
                || trimmed.StartsWith("...", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(line)
                || line.Contains("Suppressed")
                || handlerRegex.IsMatch(line);
        }

        /// <summary>
        /// 按流式状态机切分错误块(与 ErrorStreamDetector.Feed 同一规则)。
        /// 返回各块文本(已Trim), 末尾未闭合的块冲刷返回。
        /// </summary>
        public static List<string> ParseBlocks(IEnumerable<string?> lines, Regex handlerRegex)
        {
            var blocks = new List<string>();
            var buffer = new StringBuilder();
            bool inError = false;

            foreach (var raw in lines)
            {
                string line = raw ?? "";
                if (handlerRegex.IsMatch(line))
                {
                    // 新错误行: 先收尾上一块
                    if (inError && buffer.Length > 0)
                    {
                        blocks.Add(buffer.ToString().Trim());
                        buffer.Clear();
                    }
                    inError = true;
                    buffer.AppendLine(line);
                }
                else if (inError)
                {
                    if (IsContinuation(line, handlerRegex))
                    {
                        buffer.AppendLine(line);
                    }
                    else
                    {
                        if (buffer.Length > 0)
                        {
                            blocks.Add(buffer.ToString().Trim());
                            buffer.Clear();
                        }
                        inError = false;
                    }
                }
            }

            if (inError && buffer.Length > 0)
                blocks.Add(buffer.ToString().Trim());

            return blocks;
        }
    }
}
