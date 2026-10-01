using System;
using System.Collections.Generic;
using System.Linq;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 插件版本号比较。用于判断 Modrinth 上的版本是否比本地插件新。
    /// 支持 1.2.3 / v1.2.3 / 1.20.1-R0.1-SNAPSHOT / 2.22.0-beta.1 这类常见写法。
    /// </summary>
    public static class PluginVersion
    {
        /// <summary>a &gt; b 返回正数, a &lt; b 返回负数, 相等返回 0。</summary>
        public static int Compare(string? a, string? b)
        {
            var ta = Tokenize(a);
            var tb = Tokenize(b);
            if (ta.Count == 0 && tb.Count == 0) return 0;
            if (ta.Count == 0) return -1;
            if (tb.Count == 0) return 1;

            int n = Math.Min(ta.Count, tb.Count);
            for (int i = 0; i < n; i++)
            {
                int cmp = CompareToken(ta[i], tb[i]);
                if (cmp != 0) return Math.Sign(cmp);
            }

            // 前缀相同, 只剩"谁更长":
            //  1) 多出来的段全是 0 -> 相等(1.0.0 == 1.0.0.0)
            //  2) 多出来的段含文本 -> 是预发布标记, 正式版更新(1.0.0 > 1.0.0-beta)
            //  3) 多出来的段是数字 -> 段数多的更新(1.0.0 < 1.0.0.1)
            bool aLonger = ta.Count > tb.Count;
            var rest = aLonger ? ta.Skip(n) : tb.Skip(n);
            bool restIsZero = rest.All(t => t.IsNumber && t.Text.All(c => c == '0'));
            if (restIsZero) return 0;
            if (rest.Any(t => !t.IsNumber)) return aLonger ? -1 : 1;
            return ta.Count.CompareTo(tb.Count);
        }

        /// <summary>是否"a 比 b 新"。</summary>
        public static bool IsNewer(string? a, string? b) => Compare(a, b) > 0;

        private readonly struct Token
        {
            public readonly bool IsNumber;
            public readonly string Text;
            public Token(bool isNumber, string text) { IsNumber = isNumber; Text = text; }
        }

        private static List<Token> Tokenize(string? version)
        {
            var list = new List<Token>();
            if (string.IsNullOrWhiteSpace(version)) return list;

            string s = version.Trim();
            // 去掉常见前缀 v/V, 以及文件名里可能带的 jar 后缀
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            if (s.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            if (s.Length == 0) return list;

            var current = new System.Text.StringBuilder();
            bool? currentIsNum = null;
            foreach (char c in s)
            {
                bool isNum = char.IsDigit(c);
                if (!char.IsLetterOrDigit(c))
                {
                    Flush(list, current, ref currentIsNum);
                    continue;
                }
                if (currentIsNum.HasValue && currentIsNum.Value != isNum)
                    Flush(list, current, ref currentIsNum);
                currentIsNum ??= isNum;
                current.Append(c);
            }
            Flush(list, current, ref currentIsNum);
            return list;
        }

        private static void Flush(List<Token> list, System.Text.StringBuilder sb, ref bool? isNum)
        {
            if (sb.Length > 0) list.Add(new Token(isNum ?? false, sb.ToString()));
            sb.Clear();
            isNum = null;
        }

        private static int CompareToken(Token a, Token b)
        {
            if (a.IsNumber && b.IsNumber) return CompareNumber(a.Text, b.Text);
            // 数字段 > 文本段: 1.0.0 比 1.0.0-beta 新
            if (a.IsNumber != b.IsNumber) return a.IsNumber ? 1 : -1;
            return string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareNumber(string a, string b)
        {
            string x = a.TrimStart('0');
            string y = b.TrimStart('0');
            if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
            return string.CompareOrdinal(x, y);
        }
    }
}
