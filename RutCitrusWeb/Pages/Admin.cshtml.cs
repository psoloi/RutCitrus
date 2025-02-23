using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Management;
using System.Net.Http;

public class AdminModel : PageModel
{
    public string Username { get; set; }
    public string UserIP { get; set; }
    public SystemInfo SystemInfo { get; set; } = new();
    public List<UserInfo> Users { get; set; } = new();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public IActionResult OnGet()
    {
        var role = HttpContext.Session.GetString("UserRole");
        if (role != "Admin")
        {
            return RedirectToPage("/Login");
        }

        Username = HttpContext.Session.GetString("Username");
        UserIP = HttpContext.Session.GetString("UserIP");
        UpdateSystemInfo();
        UpdateUsersList();
        return Page();
    }

    private void UpdateSystemInfo()
    {
        SystemInfo.OSArchitecture = RuntimeInformation.OSArchitecture.ToString();
        SystemInfo.OSDescription = RuntimeInformation.OSDescription;
        
        var process = Process.GetCurrentProcess();
        SystemInfo.ProcessMemoryUsage = process.WorkingSet64 / (1024 * 1024) + " MB";
        
        var memStatus = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(memStatus))
        {
            SystemInfo.AvailableMemory = (memStatus.ullAvailPhys / (1024 * 1024)) + " MB";
            SystemInfo.TotalMemory = (memStatus.ullTotalPhys / (1024 * 1024)) + " MB";
        }
        else
        {
            SystemInfo.AvailableMemory = "无法获取";
            SystemInfo.TotalMemory = "无法获取";
        }
        
        DriveInfo drive = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory));
        SystemInfo.DiskAvailable = (drive.AvailableFreeSpace / (1024 * 1024 * 1024)) + " GB";
        SystemInfo.DiskTotal = (drive.TotalSize / (1024 * 1024 * 1024)) + " GB";

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                SystemInfo.CPUInfo = obj["Name"].ToString();
                break;
            }
        }
        catch
        {
            SystemInfo.CPUInfo = "无法获取CPU信息";
        }
    }

    private void UpdateUsersList()
    {
        // 这里模拟获取用户列表，实际应该从数据库或缓存中获取
        var sessions = HttpContext.Session.GetString("ActiveSessions") ?? "[]";
        var activeSessions = System.Text.Json.JsonSerializer.Deserialize<List<string>>(sessions);

        // 添加当前用户
        Users.Add(new UserInfo
        {
            ComputerName = Environment.MachineName,
            PublicIP = GetPublicIP(),
            SystemInfo = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            IsOnline = true
        });

        // 这里可以添加其他用户的信息
        // 实际应用中，您需要实现用户会话管理系统
    }

    private string GetPublicIP()
    {
        try
        {
            // 首先尝试获取本地 IP
            var localIPs = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
                .AddressList
                .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(ip => ip.ToString())
                .ToList();

            if (localIPs.Any())
            {
                // 优先返回非回环的本地 IP
                var nonLoopbackIP = localIPs.FirstOrDefault(ip => !ip.StartsWith("127."));
                if (!string.IsNullOrEmpty(nonLoopbackIP))
                {
                    return nonLoopbackIP;
                }
                return localIPs.First();
            }

            // 如果获取本地 IP 失败，尝试获取公网 IP（可选）
            using var client = new HttpClient();
            var publicIP = client.GetStringAsync("https://api.ipify.org").Result.Trim();
            return !string.IsNullOrEmpty(publicIP) ? publicIP : "无法获取IP";
        }
        catch
        {
            return "无法获取IP";
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
public class MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;

    public MEMORYSTATUSEX()
    {
        dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
    }
}

public class SystemInfo
{
    public string OSArchitecture { get; set; }
    public string OSDescription { get; set; }
    public string ProcessMemoryUsage { get; set; }
    public string AvailableMemory { get; set; }
    public string TotalMemory { get; set; }
    public string CPUInfo { get; set; }
    public string DiskAvailable { get; set; }
    public string DiskTotal { get; set; }
}

public class UserInfo
{
    public string ComputerName { get; set; }
    public string PublicIP { get; set; }
    public string SystemInfo { get; set; }
    public bool IsOnline { get; set; }
} 