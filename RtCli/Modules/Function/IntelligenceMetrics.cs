using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RtCli.Modules.Unit;

namespace RtCli.Modules.Function
{
    /// <summary>
    /// AI 自动化的指标采样与触发判定(重构 P2)。
    ///
    /// 解决的问题: 原先触发条件直接在 cron 触发那一刻取"瞬时值",
    /// 于是 "tps &lt; 18" 的真实语义变成"每小时整点那一秒 TPS&lt;18" ——
    /// 掉帧 5 分钟又恢复的故障永远采不到, 而整点恰好采到毛刺又会误触发昂贵的AI分析。
    ///
    /// 现在把"采样"与"判断"解耦: 后台每 30 秒记录一次指标,
    /// 触发判定改为"最近若干次采样连续越限", 既去毛刺又不会漏掉持续故障。
    /// </summary>
    internal partial class Intelligence
    {
        internal partial class AiAutoRunner
        {
            /// <summary>一次指标采样</summary>
            internal readonly record struct MetricSample(
                DateTime Time, double Tps, double Cpu, double MemPercent, int Players);

            /// <summary>指标采样器(进程内单例)</summary>
            internal static class MetricSampler
            {
                private const int SampleIntervalSeconds = 30;
                private const int MaxSamples = 480;   // 约 4 小时

                private static readonly List<MetricSample> _samples = new();
                private static readonly object _samplesLock = new();
                private static readonly object _startLock = new();
                private static Timer? _timer;

                /// <summary>启动采样(幂等)</summary>
                public static void EnsureStarted()
                {
                    lock (_startLock)
                    {
                        if (_timer != null) return;
                        _timer = new Timer(_ => SampleOnce(), null,
                            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(SampleIntervalSeconds));
                        Output.Debug($"指标采样器已启动(每 {SampleIntervalSeconds}s)", "AiAuto");
                    }
                }

                /// <summary>停止采样并清空历史</summary>
                public static void Stop()
                {
                    lock (_startLock)
                    {
                        _timer?.Dispose();
                        _timer = null;
                    }
                    lock (_samplesLock) { _samples.Clear(); }
                }

                public static bool IsRunning
                {
                    get { lock (_startLock) return _timer != null; }
                }

                /// <summary>只读快照(按时间升序)</summary>
                public static List<MetricSample> Snapshot()
                {
                    lock (_samplesLock) return new List<MetricSample>(_samples);
                }

                /// <summary>
                /// 判定"最近 minConsecutive 次采样是否全部满足条件"。
                /// 返回 false 表示样本不足以判定(调用方应回退到瞬时值), 此时 sustained 不可用。
                /// </summary>
                public static bool TrySustained(int minConsecutive, TimeSpan window,
                    Func<MetricSample, bool> predicate, out bool sustained)
                {
                    sustained = false;
                    if (minConsecutive <= 0) return false;

                    List<MetricSample> snapshot;
                    lock (_samplesLock)
                    {
                        var cutoff = DateTime.Now - window;
                        snapshot = _samples.Where(s => s.Time >= cutoff).ToList();
                    }

                    if (snapshot.Count < minConsecutive) return false;

                    int start = snapshot.Count - minConsecutive;
                    for (int i = start; i < snapshot.Count; i++)
                    {
                        if (!predicate(snapshot[i])) { sustained = false; return true; }
                    }
                    sustained = true;
                    return true;
                }

                private static void SampleOnce()
                {
                    try
                    {
                        double tps = TryGetCurrentTps();
                        double cpu = AiTools.GetCpuUsage();
                        var (used, total) = AiTools.GetMemoryUsage();
                        double memPercent = total > 0 ? used / total * 100 : -1;
                        int players = GetOnlinePlayerCount();

                        var sample = new MetricSample(DateTime.Now, tps, cpu, memPercent, players);
                        lock (_samplesLock)
                        {
                            _samples.Add(sample);
                            if (_samples.Count > MaxSamples)
                                _samples.RemoveRange(0, _samples.Count - MaxSamples);
                        }
                    }
                    catch (Exception ex)
                    {
                        Output.Debug($"指标采样失败: {ex.Message}", "AiAuto");
                    }
                }
            }

            /// <summary>统一的比较运算符求值(供触发条件与采样判定共用)</summary>
            internal static bool CompareOp(double value, string op, double threshold) => op switch
            {
                ">" => value > threshold,
                ">=" => value >= threshold,
                "<" => value < threshold,
                "<=" => value <= threshold,
                "=" or "==" => Math.Abs(value - threshold) < 0.01,
                _ => false
            };
        }
    }
}
