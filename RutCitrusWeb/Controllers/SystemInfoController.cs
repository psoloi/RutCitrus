using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RutCitrusWeb.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SystemInfoController : ControllerBase
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [HttpGet]
        public IActionResult Get()
        {
            var process = Process.GetCurrentProcess();
            var memStatus = new MEMORYSTATUSEX();
            
            string availableMemory = "无法获取";
            if (GlobalMemoryStatusEx(memStatus))
            {
                availableMemory = (memStatus.ullAvailPhys / (1024 * 1024)) + " MB";
            }

            return Ok(new
            {
                ProcessMemoryUsage = process.WorkingSet64 / (1024 * 1024) + " MB",
                AvailableMemory = availableMemory
            });
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
} 