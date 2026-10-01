using RtCli.Modules.Extension;
using RtCli.Modules.Unit;
using Spectre.Console;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RtCli.Modules.Function
{
    internal class RconClient
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private int _requestId = 0;
        private readonly object _sendLock = new object();

        public bool IsConnected => _tcpClient?.Connected == true;

        private const int PacketTypeLogin = 3;
        private const int PacketTypeCommand = 2;

        public bool Connect(string host, int port, string password)
        {
            try
            {
                _tcpClient = new TcpClient();
                var result = _tcpClient.BeginConnect(host, port, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));

                if (!success)
                {
                    _tcpClient.Dispose();
                    _tcpClient = null;
                    return false;
                }

                _tcpClient.EndConnect(result);
                _stream = _tcpClient.GetStream();

                SendPacket(PacketTypeLogin, password);
                var response = ReadPacket();

                if (response == null || response.Type == -1)
                {
                    Disconnect();
                    return false;
                }

                return true;
            }
            catch
            {
                Disconnect();
                return false;
            }
        }

        public string SendCommand(string command)
        {
            lock (_sendLock)
            {
                if (!IsConnected || _stream == null)
                    throw new InvalidOperationException("RCON 未连接");

                _requestId++;
                SendPacket(PacketTypeCommand, command);

                var response = ReadPacket();
                return response?.Body ?? "";
            }
        }

        public void Disconnect()
        {
            try
            {
                _stream?.Close();
                _stream = null;
                _tcpClient?.Close();
                _tcpClient = null;
            }
            catch { }
        }

        private void SendPacket(int type, string body)
        {
            if (_stream == null) return;

            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            int length = 4 + 4 + bodyBytes.Length + 2;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(length);
            writer.Write(_requestId);
            writer.Write(type);
            writer.Write(bodyBytes);
            writer.Write((byte)0);
            writer.Write((byte)0);

            _stream.Write(ms.ToArray(), 0, (int)ms.Length);
            _stream.Flush();
        }

        private RconPacket? ReadPacket()
        {
            if (_stream == null) return null;

            try
            {
                var lengthBuffer = new byte[4];
                int read = _stream.Read(lengthBuffer, 0, 4);
                if (read < 4) return null;

                int length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length <= 0 || length > 65536) return null;

                var dataBuffer = new byte[length];
                int totalRead = 0;
                while (totalRead < length)
                {
                    int bytesRead = _stream.Read(dataBuffer, totalRead, length - totalRead);
                    if (bytesRead == 0) return null;
                    totalRead += bytesRead;
                }

                int requestId = BitConverter.ToInt32(dataBuffer, 0);
                int type = BitConverter.ToInt32(dataBuffer, 4);

                string body = "";
                if (length > 8)
                {
                    int bodyLength = length - 8 - 2;
                    if (bodyLength > 0)
                    {
                        body = Encoding.UTF8.GetString(dataBuffer, 8, bodyLength);
                    }
                }

                return new RconPacket { RequestId = requestId, Type = type, Body = body };
            }
            catch
            {
                return null;
            }
        }

        private class RconPacket
        {
            public int RequestId { get; set; }
            public int Type { get; set; }
            public string Body { get; set; } = "";
        }
    }

}
