using System;
using System.Threading;

namespace RtCli.Modules.Unit
{
    /// <summary>
    /// 控制台交互门闩: 主命令行输入循环与后台线程(如 EULA 确认、看门狗确认)串行化使用控制台。
    /// 后台线程调用 Interact 请求主输入循环让出控制台(主循环轮询检测), 独占完成交互后归还;
    /// 主循环让出期间暂停读键, 用户已输入未提交的内容保留。
    /// </summary>
    public static class ConsoleGate
    {
        private static readonly object _interactLock = new();
        private static volatile bool _yieldRequested;
        private static readonly ManualResetEventSlim _yielded = new(false);
        private static readonly ManualResetEventSlim _resume = new(false);

        /// <summary>主输入循环轮询: 是否有后台交互请求让出控制台</summary>
        public static bool YieldRequested => _yieldRequested;

        /// <summary>
        /// 后台线程独占控制台执行交互。
        /// 主循环不在读键时无法让出, 等待超时后直接执行(此时无并发读者, 安全);
        /// 同一时间仅允许一个后台交互(互斥)。
        /// </summary>
        public static T Interact<T>(Func<T> interaction)
        {
            lock (_interactLock)
            {
                _yielded.Reset();
                _resume.Reset();
                _yieldRequested = true;
                // 等主循环进入让出点; 主循环读键轮询周期约15ms, 2秒超时兜底
                _yielded.Wait(2000);
                try
                {
                    return interaction();
                }
                finally
                {
                    _yieldRequested = false;
                    _resume.Set();
                }
            }
        }

        /// <summary>后台线程请求独占控制台弹出确认框(等待主输入循环让出)</summary>
        public static bool InteractConfirm(string question, bool defaultValue)
            => Interact(() => Spectre.Console.AnsiConsole.Confirm(question, defaultValue));

        /// <summary>主输入循环调用: 已到让出点, 通知后台可交互并等待其完成后继续读键</summary>
        internal static void YieldAndWait()
        {
            _yielded.Set();
            // 兜底超时: 即使后台交互异常未归还, 主循环也不永久停摆
            _resume.Wait(60000);
        }
    }
}
