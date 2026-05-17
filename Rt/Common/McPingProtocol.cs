using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Rt.Common
{
    /// <summary>
    /// Minecraft 服务器状态响应数据模型
    /// </summary>
    public class McStatusResponse
    {
        public McVersion? Version { get; set; }
        public McPlayers? Players { get; set; }
        public JsonElement Description { get; set; }
        public McFavicon? Favicon { get; set; }
        public int? PreviewsChat { get; set; }
        public int? EnforcesSecureChat { get; set; }

        public string GetDescriptionText()
        {
            try
            {
                if (Description.ValueKind == JsonValueKind.String)
                    return Description.GetString() ?? "";
                if (Description.ValueKind == JsonValueKind.Object)
                    return Description.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
            }
            catch { }
            return "";
        }
    }

    public class McVersion
    {
        public string Name { get; set; } = "";
        public int Protocol { get; set; }
    }

    public class McPlayers
    {
        public int Max { get; set; }
        public int Online { get; set; }
        public McPlayerSample[]? Sample { get; set; }
    }

    public class McPlayerSample
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = "";
    }

    public class McFavicon
    {
        public string? Data { get; set; }
    }

    /// <summary>
    /// Minecraft Server List Ping 协议实现
    /// 协议参考: https://wiki.vg/Server_List_Ping
    /// </summary>
    public static class McPingProtocol
    {
        /// <summary>
        /// 查询MC服务器状态，返回(状态, 延迟ms)。失败抛出异常。
        /// </summary>
        public static (McStatusResponse Status, int LatencyMs) Query(string host, int port, int timeoutSeconds = 5)
        {
            using var client = new TcpClient();
            var connectResult = client.BeginConnect(host, port, null, null);
            bool connected = connectResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(timeoutSeconds));
            if (!connected)
            {
                client.Dispose();
                throw new TimeoutException($"连接服务器超时: {host}:{port}");
            }
            client.EndConnect(connectResult);

            using var stream = client.GetStream();
            stream.ReadTimeout = timeoutSeconds * 1000;
            stream.WriteTimeout = timeoutSeconds * 1000;

            // 1. 发送握手包 (Handshake, 0x00)
            //    Protocol Version = -1 (Status), Next State = 1 (Status)
            using (var handshakeMs = new MemoryStream())
            {
                WriteVarInt(handshakeMs, 0x00);       // Packet ID
                WriteVarInt(handshakeMs, -1);          // Protocol Version (-1 = any)
                WriteString(handshakeMs, host);        // Server Address
                WriteUShort(handshakeMs, (ushort)port);// Server Port
                WriteVarInt(handshakeMs, 1);           // Next State = 1 (Status)
                SendPacket(stream, handshakeMs.ToArray());
            }

            // 2. 发送状态请求包 (Status Request, 0x00, 空内容)
            SendPacket(stream, new byte[] { 0x00 });

            // 3. 读取状态响应包
            byte[] statusPayload = ReadPacket(stream);
            using (var rs = new MemoryStream(statusPayload))
            {
                int packetId = ReadVarInt(rs);
                if (packetId != 0x00)
                    throw new InvalidDataException($"期望状态响应包ID 0x00，实际: 0x{packetId:X2}");

                string json = ReadString(rs);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var status = JsonSerializer.Deserialize<McStatusResponse>(json, options)
                    ?? throw new InvalidDataException("状态响应JSON解析失败");

                // 4. 发送Ping包 (0x01, 8字节payload)
                long payload = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using (var pingMs = new MemoryStream())
                {
                    WriteVarInt(pingMs, 0x01);                // Packet ID
                    WriteLong(pingMs, payload);                // Payload
                    SendPacket(stream, pingMs.ToArray());
                }

                // 5. 读取Pong包，计算延迟
                byte[] pongPayload = ReadPacket(stream);
                using (var ps = new MemoryStream(pongPayload))
                {
                    int pongId = ReadVarInt(ps);
                    if (pongId != 0x01)
                        throw new InvalidDataException($"期望Pong包ID 0x01，实际: 0x{pongId:X2}");
                    long pongPayloadValue = ReadLong(ps);
                    int latency = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pongPayloadValue);
                    return (status, latency);
                }
            }
        }

        // ===== VarInt / 包读写辅助方法 =====

        /// <summary>写入带长度前缀的包到流</summary>
        private static void SendPacket(NetworkStream stream, byte[] payload)
        {
            using var ms = new MemoryStream();
            WriteVarInt(ms, payload.Length);
            ms.Write(payload, 0, payload.Length);
            byte[] data = ms.ToArray();
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        /// <summary>读取一个完整包(长度前缀+载荷)</summary>
        private static byte[] ReadPacket(NetworkStream stream)
        {
            int length = ReadVarInt(stream);
            if (length < 0 || length > 1 << 21)
                throw new InvalidDataException($"无效的包长度: {length}");

            byte[] buffer = new byte[length];
            int totalRead = 0;
            while (totalRead < length)
            {
                int read = stream.Read(buffer, totalRead, length - totalRead);
                if (read == 0)
                    throw new EndOfStreamException("连接在读取包时关闭");
                totalRead += read;
            }
            return buffer;
        }

        public static int ReadVarInt(Stream stream)
        {
            int value = 0;
            int length = 0;
            byte b;
            do
            {
                int read = stream.ReadByte();
                if (read < 0)
                    throw new EndOfStreamException("读取VarInt时连接关闭");
                b = (byte)read;
                value |= (b & 0x7F) << (length * 7);
                length++;
                if (length > 5)
                    throw new InvalidDataException("VarInt过长");
            } while ((b & 0x80) != 0);
            return value;
        }

        public static int ReadVarInt(NetworkStream stream)
        {
            int value = 0;
            int length = 0;
            byte b;
            do
            {
                int read = stream.ReadByte();
                if (read < 0)
                    throw new EndOfStreamException("读取VarInt时连接关闭");
                b = (byte)read;
                value |= (b & 0x7F) << (length * 7);
                length++;
                if (length > 5)
                    throw new InvalidDataException("VarInt过长");
            } while ((b & 0x80) != 0);
            return value;
        }

        public static void WriteVarInt(Stream stream, int value)
        {
            // 将负数转为无符号表示
            uint v = (uint)value;
            while (true)
            {
                if ((v & ~0x7Fu) == 0)
                {
                    stream.WriteByte((byte)v);
                    return;
                }
                stream.WriteByte((byte)((v & 0x7F) | 0x80));
                v >>= 7;
            }
        }

        public static void WriteString(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteVarInt(stream, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        public static string ReadString(Stream stream)
        {
            int length = ReadVarInt(stream);
            if (length < 0 || length > 1 << 21)
                throw new InvalidDataException($"无效的字符串长度: {length}");
            byte[] buffer = new byte[length];
            int totalRead = 0;
            while (totalRead < length)
            {
                int read = stream.Read(buffer, totalRead, length - totalRead);
                if (read == 0)
                    throw new EndOfStreamException("读取字符串时连接关闭");
                totalRead += read;
            }
            return Encoding.UTF8.GetString(buffer);
        }

        public static void WriteUShort(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value & 0xFF));
        }

        public static void WriteLong(Stream stream, long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            stream.Write(bytes, 0, bytes.Length);
        }

        public static long ReadLong(Stream stream)
        {
            byte[] buffer = new byte[8];
            int totalRead = 0;
            while (totalRead < 8)
            {
                int read = stream.Read(buffer, totalRead, 8 - totalRead);
                if (read == 0)
                    throw new EndOfStreamException("读取Long时连接关闭");
                totalRead += read;
            }
            if (BitConverter.IsLittleEndian)
                Array.Reverse(buffer);
            return BitConverter.ToInt64(buffer, 0);
        }
    }
}
