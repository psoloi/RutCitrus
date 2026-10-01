using System;
using System.Collections.Generic;

namespace RtCli.Modules.Function
{
    /// <summary>
    /// 固定容量的日志环形缓冲区。
    /// 每条日志拥有一个单调递增的绝对序号(从 1 开始), 消费者只需记录"上次已消费的序号",
    /// 即可持续取得增量; 旧行被覆盖只会造成历史缺口, 不会像 List 下标方案那样在饱和后永久失效。
    /// 注意: 本类不做内部加锁, 所有调用必须持有 Analyzer._logBufferLock。
    /// </summary>
    internal sealed class LogRingBuffer
    {
        private readonly string[] _items;
        private int _head;          // 最旧一条所在位置
        private int _count;
        private long _totalWritten; // 已写入总条数 = 最新一条的绝对序号

        public LogRingBuffer(int capacity)
        {
            _items = new string[capacity > 0 ? capacity : 1];
        }

        /// <summary>已写入的总条数, 即最新一条日志的绝对序号(空缓冲为 0)</summary>
        public long LatestSequence => _totalWritten;

        /// <summary>当前保留的日志条数(不会超过容量)</summary>
        public int Count => _count;

        /// <summary>最旧一条日志的绝对序号; 缓冲区为空时返回 LatestSequence + 1</summary>
        public long OldestSequence => _totalWritten - _count + 1;

        public void Add(string line)
        {
            if (_count < _items.Length)
            {
                _items[(_head + _count) % _items.Length] = line;
                _count++;
            }
            else
            {
                // 已满: 覆盖最旧一条并前移 head, 容量恒定
                _items[_head] = line;
                _head = (_head + 1) % _items.Length;
            }
            _totalWritten++;
        }

        /// <summary>按相对下标访问(0 = 最旧一条, Count-1 = 最新一条)</summary>
        public string this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return _items[(_head + index) % _items.Length];
            }
        }

        /// <summary>取最近 count 条(不足则返回全部)</summary>
        public List<string> GetTail(int count)
        {
            int take = count < 0 ? 0 : Math.Min(count, _count);
            var result = new List<string>(take);
            for (int i = _count - take; i < _count; i++)
                result.Add(_items[(_head + i) % _items.Length]);
            return result;
        }

        /// <summary>取全部保留日志的快照(0 = 最旧)</summary>
        public List<string> Snapshot() => GetTail(_count);

        /// <summary>
        /// 取序号大于 afterSequence 的所有日志。
        /// 若 afterSequence 早于最旧序号(旧行已被覆盖), 只返回仍保留的部分;
        /// nextSequence 为本次读取后的最新序号, 可直接作为下次调用的 afterSequence。
        /// </summary>
        public List<string> GetSince(long afterSequence, out long nextSequence)
        {
            nextSequence = _totalWritten;
            if (_count == 0) return new List<string>();

            long first = OldestSequence;
            long startSequence = afterSequence + 1;
            if (startSequence < first) startSequence = first; // 已被覆盖的历史无法补发
            if (startSequence > _totalWritten) return new List<string>();

            int skip = (int)(startSequence - first);
            var result = new List<string>(_count - skip);
            for (int i = skip; i < _count; i++)
                result.Add(_items[(_head + i) % _items.Length]);
            return result;
        }
    }
}
