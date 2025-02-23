using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class UserModel : PageModel
{
    public string Username { get; set; }
    public string UserIP { get; set; }

    public IActionResult OnGet()
    {
        var role = HttpContext.Session.GetString("UserRole");
        if (role != "User")
        {
            return RedirectToPage("/Login");
        }

        Username = HttpContext.Session.GetString("Username");
        UserIP = HttpContext.Session.GetString("UserIP");
        return Page();
    }
} 