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
    public class MinecraftServerInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public string WindowTitle { get; set; } = "";
        public string JarPath { get; set; } = "";
        public string CommandLine { get; set; } = "";
        /// <summary>Java 可执行文件实际路径(来自 WMI ExecutablePath 或 MainModule.FileName)</summary>
        public string JavaExePath { get; set; } = "";
        /// <summary>与配置 JavaPath 的匹配优先级(越高越优先, 100=完全匹配)</summary>
        public int MatchScore { get; set; }
    }

    /// <summary>日志流类型</summary>
    internal enum LogStreamType
    {
        /// <summary>MC 服务端实时数据流(服务器启动时开始缓存)</summary>
        Server,
        /// <summary>外部导入的日志文件流</summary>
        External
    }

    /// <summary>
    /// 单条日志流缓存条目。
    /// 生命周期：服务器启动时创建，服务器停止后保留(供 .fx get 分析)，
    /// 仅当数据流过多(单流/全局超限)或程序重启时清空。
    /// </summary>
    internal class ClientGuideMatch
    {
        public int Index { get; set; }
        public ClientGuideEntry Entry { get; set; } = null!;
        public double Similarity { get; set; }
        public string MatchedMessage { get; set; } = "";
    }

    internal class ConfigDiffEntry
    {
        public int Index { get; set; }
        public string RelativePath { get; set; } = "";
        public string BackupPath { get; set; } = "";
        public string CurrentPath { get; set; } = "";
    }

    internal class PluginEntry
    {
        public int Index { get; set; }
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDisabled { get; set; }
    }

    internal class ModEntry
    {
        public int Index { get; set; }
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDisabled { get; set; }
    }

    /// <summary>RM 管理协议连接状态</summary>
}
