using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Cryptography;
using System.Text;

public class LoginModel : PageModel
{
    private const int MaxLoginAttempts = 5;
    private const int LockoutDurationMinutes = 30;
    private static readonly Dictionary<string, (int attempts, DateTime lastAttempt)> LoginAttempts = new();

    [BindProperty]
    public string Username { get; set; }
    
    [BindProperty]
    public string Password { get; set; }
    
    [BindProperty]
    public string Captcha { get; set; }

    [BindProperty]
    public string LoginType { get; set; }
    
    public string ErrorMessage { get; set; }
    public bool IsLocked { get; set; }
    public int LockoutMinutes { get; set; }

    public IActionResult OnPost()
    {
        // 验证码检查
        var sessionCaptcha = HttpContext.Session.GetString("Captcha");
        if (string.IsNullOrEmpty(sessionCaptcha) || sessionCaptcha != Captcha?.ToUpper())
        {
            ErrorMessage = "验证码错误";
            return Page();
        }

        // 检查是否被锁定
        if (IsAccountLocked(Username))
        {
            IsLocked = true;
            LockoutMinutes = GetRemainingLockoutMinutes(Username);
            ErrorMessage = $"账户已被锁定，请 {LockoutMinutes} 分钟后重试";
            return Page();
        }

        // 验证登录
        if (ValidateAdminLogin(Username, Password))
        {
            ResetLoginAttempts(Username);
            HttpContext.Session.SetString("UserRole", "Admin");
            HttpContext.Session.SetString("UserIP", GetClientIP());
            HttpContext.Session.SetString("Username", Username);
            return RedirectToPage("/Admin");
        }

        // 登录失败处理
        IncrementLoginAttempts(Username);
        ErrorMessage = "用户名或密码错误";
        return Page();
    }

    private bool ValidateAdminLogin(string username, string password)
    {
        // 这里应该使用配置文件或数据库存储管理员信息
        var hashedPassword = HashPassword("167789ghh"); // 实际应用中不应该硬编码
        return username == "psoloi" && HashPassword(password) == hashedPassword;
    }

    private bool ValidateUserLogin(string username, string password)
    {
        // 实现普通用户的验证逻辑
        return true; // 简化示例
    }

    private string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(hashedBytes);
    }

    private bool IsAccountLocked(string username)
    {
        if (LoginAttempts.TryGetValue(username, out var attempts))
        {
            if (attempts.attempts >= MaxLoginAttempts)
            {
                var timeSinceLastAttempt = DateTime.Now - attempts.lastAttempt;
                return timeSinceLastAttempt.TotalMinutes < LockoutDurationMinutes;
            }
        }
        return false;
    }

    private int GetRemainingLockoutMinutes(string username)
    {
        if (LoginAttempts.TryGetValue(username, out var attempts))
        {
            var timeSinceLastAttempt = DateTime.Now - attempts.lastAttempt;
            return Math.Max(0, LockoutDurationMinutes - (int)timeSinceLastAttempt.TotalMinutes);
        }
        return 0;
    }

    private void IncrementLoginAttempts(string username)
    {
        if (LoginAttempts.ContainsKey(username))
        {
            var (attempts, _) = LoginAttempts[username];
            LoginAttempts[username] = (attempts + 1, DateTime.Now);
        }
        else
        {
            LoginAttempts[username] = (1, DateTime.Now);
        }
    }

    private void ResetLoginAttempts(string username)
    {
        LoginAttempts.Remove(username);
    }

    public IActionResult OnPostLocalLogin()
    {
        string macAddress = GetMacAddress();
        string clientIP = GetClientIP();
        
        HttpContext.Session.SetString("UserRole", "User");
        HttpContext.Session.SetString("UserIP", clientIP);
        HttpContext.Session.SetString("Username", macAddress);
        
        return RedirectToPage("/User");
    }

    private string GetClientIP()
    {
        var forwardedHeader = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedHeader))
        {
            return forwardedHeader.Split(',')[0];
        }
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    }

    private string GetMacAddress()
    {
        try
        {
            var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            var activeInterface = networkInterfaces.FirstOrDefault(ni => 
                ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && 
                ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);

            if (activeInterface != null)
            {
                return activeInterface.GetPhysicalAddress().ToString();
            }
        }
        catch { }
        
        return "Unknown-MAC";
    }
} 