using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using PacketDotNet;
using SharpPcap;

namespace Rt.Core.PacketEvents.Protocol
{
    /// <summary>
    /// Minecraft 连接所处的网络状态(阶段)。
    /// </summary>
    public enum McConnectionState
    {
        Handshaking,
        Status,
        Login,
        Configuration,
        Play
    }

    /// <summary>
    /// 数据包流向。
    /// </summary>
    public enum PacketFlow
    {
        /// <summary>客户端 → 服务端 (minecraft-data 中的 toServer)</summary>
        ServerBound,
        /// <summary>服务端 → 客户端 (minecraft-data 中的 toClient)</summary>
        ClientBound
    }

    /// <summary>
    /// 解码后的单个 MC 数据包详情(序列化为 JSON 返回给事件订阅者)。
    /// </summary>
    public sealed class DecodedMcPacket
    {
        public string Time { get; set; } = "";
        public string Direction { get; set; } = "";
        public string ClientIp { get; set; } = "";
        /// <summary>连接状态(handshaking/status/login/configuration/play)</summary>
        public string State { get; set; } = "";
        /// <summary>协议版本号(握手后可知), 未知为 null</summary>
        public int? ProtocolVersion { get; set; }
        /// <summary>数据包名称, 未知为 unknown</summary>
        public string PacketName { get; set; } = "";
        /// <summary>数据包 ID, 未知为 -1</summary>
        public int PacketId { get; set; } = -1;
        public string PacketIdHex { get; set; } = "";
        /// <summary>MC 帧长度字段(数据包长度 VarInt)</summary>
        public int PacketLength { get; set; }
        /// <summary>解压后(包ID+载荷)的字节数</summary>
        public int DataLength { get; set; }
        /// <summary>该包是否经过 zlib 压缩</summary>
        public bool Compressed { get; set; }
        /// <summary>载荷十六进制预览(不含包ID)</summary>
        public string DataHex { get; set; } = "";
    }

    /// <summary>
    /// 单条连接的 TCP 字节流缓冲(按方向)，含 TCP 序列号去重与乱序/缺口处理。
    /// </summary>
    internal sealed class ReassemblyBuffer
    {
        private readonly List<byte> _data = new();
        private long _expectedSeq = -1;

        public int Length => _data.Count;
        public byte this[int index] => _data[index];

        public void Append(byte[] payload, uint seq)
        {
            long s = seq;
            if (_expectedSeq < 0)
                _expectedSeq = s;
            if (s < _expectedSeq)
                return; // 重传/乱序重复, 忽略
            if (s > _expectedSeq)
            {
                // 存在缺口, 无法对齐, 重置缓冲
                _data.Clear();
                _expectedSeq = s;
            }
            _data.AddRange(payload);
            _expectedSeq = s + payload.Length;
        }

        public void Consume(int count)
        {
            if (count > 0 && count <= _data.Count)
                _data.RemoveRange(0, count);
        }

        public byte[] Slice(int start, int end) => _data.GetRange(start, end - start).ToArray();
    }

    /// <summary>
    /// 单条 MC 连接的解码状态：TCP 流重组、连接阶段跟踪、压缩阈值、协议版本。
    /// </summary>
    internal sealed class McConnection
    {
        private const int MaxFrameLength = 8 * 1024 * 1024; // 8MB 上限, 防止异常长度

        private readonly ReassemblyBuffer _serverBound = new();
        private readonly ReassemblyBuffer _clientBound = new();

        public McConnectionState State { get; private set; } = McConnectionState.Handshaking;
        public int ProtocolVersion { get; private set; } = -1;
        public long LastActivityTicks { get; private set; }

        private int _compressionThreshold = -1;
        private bool _compressionEnabled = false;

        public List<DecodedMcPacket> Feed(PacketFlow flow, byte[] payload, uint seq, DateTime timestamp, string clientIp)
        {
            LastActivityTicks = Environment.TickCount64;

            var buffer = flow == PacketFlow.ServerBound ? _serverBound : _clientBound;
            buffer.Append(payload, seq);

            var result = new List<DecodedMcPacket>();

            while (buffer.Length > 0)
            {
                if (!TryReadVarInt(buffer, 0, buffer.Length, out int frameLength, out int lenBytes))
                    break; // 长度字段不完整, 等待更多字节

                if (frameLength < 0 || frameLength > MaxFrameLength)
                {
                    buffer.Consume(buffer.Length); // 长度非法, 丢弃整个缓冲以重新对齐
                    break;
                }

                int frameStart = lenBytes;
                if (buffer.Length - frameStart < frameLength)
                    break; // 帧内容不完整, 等待更多字节

                int frameEnd = frameStart + frameLength;

                var stateBefore = State;
                var pkt = DecodeFrameContent(buffer, frameStart, frameEnd, flow);
                buffer.Consume(frameEnd);

                if (pkt != null)
                {
                    pkt.Time = timestamp.ToString("HH:mm:ss.fff");
                    pkt.Direction = flow == PacketFlow.ServerBound ? "C->S" : "S->C";
                    pkt.ClientIp = clientIp;
                    pkt.PacketLength = frameLength;
                    pkt.State = StateName(stateBefore);
                    pkt.ProtocolVersion = ProtocolVersion > 0 ? ProtocolVersion : null;
                    FillName(flow, pkt, stateBefore);
                    result.Add(pkt);
                }

                if (result.Count > 512)
                    break; // 安全上限
            }

            return result;
        }

        private DecodedMcPacket? DecodeFrameContent(ReassemblyBuffer buf, int start, int end, PacketFlow flow)
        {
            var pkt = new DecodedMcPacket();
            int p = start;

            int packetId;
            byte[] data;

            if (!_compressionEnabled)
            {
                if (!TryReadVarInt(buf, p, end, out packetId, out int idBytes)) return null;
                p += idBytes;
                data = buf.Slice(p, end);
                pkt.DataLength = end - start;
                pkt.Compressed = false;
            }
            else
            {
                if (!TryReadVarInt(buf, p, end, out int dataLength, out int dlBytes)) return null;
                p += dlBytes;

                if (dataLength == 0)
                {
                    if (!TryReadVarInt(buf, p, end, out packetId, out int idBytes)) return null;
                    p += idBytes;
                    data = buf.Slice(p, end);
                    pkt.DataLength = end - p;
                    pkt.Compressed = false;
                }
                else
                {
                    byte[] compressed = buf.Slice(p, end);
                    byte[] decompressed;
                    try { decompressed = DecompressZlib(compressed, dataLength); }
                    catch { return null; }

                    if (!TryReadVarInt(decompressed, 0, decompressed.Length, out packetId, out int idBytes)) return null;
                    data = Slice(decompressed, idBytes);
                    pkt.DataLength = dataLength;
                    pkt.Compressed = true;
                }
            }

            pkt.PacketId = packetId;
            pkt.PacketIdHex = "0x" + packetId.ToString("X2");
            pkt.DataHex = ToHex(data, 256);

            // 状态迁移(需解析载荷)
            if (State == McConnectionState.Handshaking && flow == PacketFlow.ServerBound && packetId == 0)
                ParseHandshake(data, pkt);
            else if (State == McConnectionState.Login && flow == PacketFlow.ClientBound && packetId == 3)
                ParseSetCompression(data);
            else if (State == McConnectionState.Login && flow == PacketFlow.ClientBound && packetId == 2)
                State = McConnectionState.Configuration;
            else if (State == McConnectionState.Configuration && flow == PacketFlow.ClientBound && packetId == 3)
                State = McConnectionState.Play;

            return pkt;
        }

        private void ParseHandshake(byte[] data, DecodedMcPacket pkt)
        {
            int p = 0;
            if (!TryReadVarInt(data, 0, data.Length, out int protocolVersion, out int pb)) return;
            p += pb;

            // 跳过 serverAddress 字符串
            if (!TryReadString(data, p, data.Length, out _, out int hb)) return;
            p += hb;

            // 跳过 serverPort (u16)
            if (p + 2 > data.Length) return;
            p += 2;

            if (!TryReadVarInt(data, p, data.Length, out int nextState, out _)) return;

            ProtocolVersion = protocolVersion;
            pkt.ProtocolVersion = protocolVersion;

            if (nextState == 1)
                State = McConnectionState.Status;
            else if (nextState == 2)
                State = McConnectionState.Login;
        }

        private void ParseSetCompression(byte[] data)
        {
            if (TryReadVarInt(data, 0, data.Length, out int threshold, out _))
            {
                _compressionThreshold = threshold;
                _compressionEnabled = threshold >= 0;
            }
        }

        private void FillName(PacketFlow flow, DecodedMcPacket pkt, McConnectionState state)
        {
            int proto = ProtocolVersion > 0 ? ProtocolVersion : 774; // 版本未知时按最新表给出名称(握手/状态包跨版本一致)
            string flowName = flow == PacketFlow.ServerBound ? "toServer" : "toClient";
            if (McPacketTables.TryGetName(proto, StateName(state), flowName, pkt.PacketId, out string name))
                pkt.PacketName = name;
            else
                pkt.PacketName = "unknown";
        }

        private static string StateName(McConnectionState s) => s switch
        {
            McConnectionState.Handshaking => "handshaking",
            McConnectionState.Status => "status",
            McConnectionState.Login => "login",
            McConnectionState.Configuration => "configuration",
            McConnectionState.Play => "play",
            _ => "play"
        };

        private static byte[] DecompressZlib(byte[] data, int expectedLength)
        {
            using var input = new MemoryStream(data);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expectedLength > 0 ? expectedLength : data.Length * 4);
            zlib.CopyTo(output);
            return output.ToArray();
        }

        private static bool TryReadVarInt(ReassemblyBuffer buf, int offset, int maxLen, out int value, out int consumed)
        {
            value = 0;
            consumed = 0;
            int result = 0;
            for (int i = 0; i < 5; i++)
            {
                if (offset + i >= maxLen)
                {
                    value = 0;
                    consumed = 0;
                    return false;
                }
                byte b = buf[offset + i];
                result |= (b & 0x7F) << (7 * i);
                consumed = i + 1;
                if ((b & 0x80) == 0)
                {
                    value = result;
                    return true;
                }
            }
            value = 0;
            consumed = 0;
            return false;
        }

        private static bool TryReadVarInt(byte[] data, int offset, int maxLen, out int value, out int consumed)
        {
            value = 0;
            consumed = 0;
            int result = 0;
            for (int i = 0; i < 5; i++)
            {
                if (offset + i >= maxLen || offset + i >= data.Length)
                {
                    value = 0;
                    consumed = 0;
                    return false;
                }
                byte b = data[offset + i];
                result |= (b & 0x7F) << (7 * i);
                consumed = i + 1;
                if ((b & 0x80) == 0)
                {
                    value = result;
                    return true;
                }
            }
            value = 0;
            consumed = 0;
            return false;
        }

        private static bool TryReadString(byte[] data, int offset, int maxLen, out string value, out int consumed)
        {
            value = "";
            consumed = 0;
            if (!TryReadVarInt(data, offset, maxLen, out int len, out int lb)) return false;
            if (len < 0) return false;
            int start = offset + lb;
            if (start + len > maxLen || start + len > data.Length) return false;
            value = Encoding.UTF8.GetString(data, start, len);
            consumed = lb + len;
            return true;
        }

        private static byte[] Slice(byte[] data, int start) => data.AsSpan(start).ToArray();

        private static string ToHex(byte[] data, int maxLength)
        {
            int count = Math.Min(maxLength, data.Length);
            if (count <= 0) return "";
            var sb = new StringBuilder(count * 2);
            for (int i = 0; i < count; i++)
                sb.Append(data[i].ToString("x2"));
            if (count < data.Length)
                sb.Append("...");
            return sb.ToString();
        }
    }

    /// <summary>
    /// MC 数据包解码器：按连接(客户端IP:端口)维护 TCP 重组与协议状态，输出解码后的数据包。
    /// </summary>
    public sealed class McDecoder
    {
        private const int PruneIntervalCalls = 1000;
        private const long PruneAfterTicks = 10 * 60 * 1000; // 10 分钟无活动则清理

        private readonly ConcurrentDictionary<string, McConnection> _connections = new();
        private int _callCount;

        public IReadOnlyList<DecodedMcPacket> Process(RawCapture raw, PacketFlow flow, string clientIp, DateTime timestamp)
        {
            if (!TryExtractSegment(raw, flow, out int clientPort, out byte[] payload, out uint seq))
                return Array.Empty<DecodedMcPacket>();

            string key = clientIp + ":" + clientPort;
            var conn = _connections.GetOrAdd(key, _ => new McConnection());

            List<DecodedMcPacket> result;
            lock (conn)
            {
                result = conn.Feed(flow, payload, seq, timestamp, clientIp);
            }

            if (Interlocked.Increment(ref _callCount) >= PruneIntervalCalls)
            {
                Interlocked.Exchange(ref _callCount, 0);
                Prune();
            }

            return result;
        }

        private void Prune()
        {
            long now = Environment.TickCount64;
            foreach (var kv in _connections)
            {
                if (now - kv.Value.LastActivityTicks > PruneAfterTicks)
                    _connections.TryRemove(kv.Key, out _);
            }
        }

        private static bool TryExtractSegment(RawCapture raw, PacketFlow flow, out int clientPort, out byte[] payload, out uint seq)
        {
            clientPort = 0;
            payload = Array.Empty<byte>();
            seq = 0;

            try
            {
                var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
                var tcp = packet.Extract<TcpPacket>();
                if (tcp == null) return false;

                payload = tcp.PayloadData ?? Array.Empty<byte>();
                if (payload.Length == 0) return false;

                seq = tcp.SequenceNumber;
                clientPort = flow == PacketFlow.ServerBound ? tcp.SourcePort : tcp.DestinationPort;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}