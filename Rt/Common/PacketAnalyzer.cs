using System.Net;
using PacketDotNet;
using SharpPcap;

namespace Rt.Common
{
    /// <summary>
    /// 数据包方向
    /// </summary>
    public enum PacketDirection
    {
        /// <summary>客户端 → 服务端 (玩家发包)</summary>
        ClientToServer,
        /// <summary>服务端 → 客户端</summary>
        ServerToClient,
        /// <summary>不相关</summary>
        Irrelevant
    }

    /// <summary>
    /// 分析后的数据包信息
    /// </summary>
    public class AnalyzedPacket
    {
        /// <summary>客户端IP(若相关)</summary>
        public string ClientIp { get; set; } = "";
        /// <summary>方向</summary>
        public PacketDirection Direction { get; set; }
        /// <summary>TCP载荷字节数</summary>
        public int PayloadLength { get; set; }
        /// <summary>是否为MC相关包</summary>
        public bool IsMcRelevant { get; set; }
    }

    /// <summary>
    /// 数据包分析器：从捕获的原始包中提取MC客户端→服务端的TCP数据包信息
    /// </summary>
    public static class PacketAnalyzer
    {
        /// <summary>
        /// 分析一个捕获的数据包，判断是否为发往指定MC服务端的TCP包。
        /// 当 serverIp 为空/"0.0.0.0"/"any" 时按端口通配匹配(用于MC服务器监听0.0.0.0的场景)。
        /// </summary>
        /// <param name="capture">SharpPcap捕获的原始包</param>
        /// <param name="serverIp">MC服务器IP，留空或"0.0.0.0"/"any"则按端口通配</param>
        /// <param name="serverPort">MC服务器端口</param>
        public static AnalyzedPacket Analyze(RawCapture capture, string serverIp, int serverPort)
        {
            var result = new AnalyzedPacket();

            try
            {
                var packet = PacketDotNet.Packet.ParsePacket(capture.LinkLayerType, capture.Data);
                var tcpPacket = packet.Extract<TcpPacket>();
                if (tcpPacket == null)
                    return result;

                var ipPacket = packet.Extract<IPPacket>();
                if (ipPacket == null)
                    return result;

                string srcIp = ipPacket.SourceAddress.ToString();
                string dstIp = ipPacket.DestinationAddress.ToString();
                int srcPort = tcpPacket.SourcePort;
                int dstPort = tcpPacket.DestinationPort;
                int payloadLen = tcpPacket.PayloadData?.Length ?? 0;

                // serverIp 为通配时只按端口匹配(用于MC服务器监听 0.0.0.0 的场景)
                bool wildcard = string.IsNullOrEmpty(serverIp)
                    || serverIp == "0.0.0.0"
                    || serverIp == "any"
                    || serverIp == "::";

                // 客户端 → 服务端 (目的端口=服务器端口)
                bool isC2S = wildcard
                    ? (dstPort == serverPort)
                    : (dstIp == serverIp && dstPort == serverPort);

                // 服务端 → 客户端 (源端口=服务器端口)
                bool isS2C = wildcard
                    ? (srcPort == serverPort)
                    : (srcIp == serverIp && srcPort == serverPort);

                if (isC2S)
                {
                    result.ClientIp = srcIp;
                    result.Direction = PacketDirection.ClientToServer;
                    result.PayloadLength = payloadLen;
                    result.IsMcRelevant = true;
                }
                else if (isS2C)
                {
                    result.ClientIp = dstIp;
                    result.Direction = PacketDirection.ServerToClient;
                    result.PayloadLength = payloadLen;
                    result.IsMcRelevant = true;
                }
            }
            catch
            {
                // 解析失败忽略
            }

            return result;
        }

        /// <summary>
        /// 判断 serverIp 是否为通配地址(空/"0.0.0.0"/"any"/"::")。
        /// 通配时只按端口过滤，用于MC服务器监听 0.0.0.0 的场景。
        /// </summary>
        public static bool IsWildcardIp(string? serverIp)
        {
            return string.IsNullOrEmpty(serverIp)
                || serverIp == "0.0.0.0"
                || serverIp == "any"
                || serverIp == "::";
        }

        /// <summary>
        /// 构建 BPF 过滤器。
        /// serverIp 为空/通配时: tcp and port {port}
        /// 否则: tcp and host {ip} and port {port}
        /// </summary>
        public static string BuildBpfFilter(string? serverIp, int serverPort)
        {
            return IsWildcardIp(serverIp)
                ? $"tcp and port {serverPort}"
                : $"tcp and host {serverIp} and port {serverPort}";
        }
    }
}
