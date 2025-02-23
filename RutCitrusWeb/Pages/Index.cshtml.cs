using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RutCitrusWeb.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;

        public bool IsLoggedIn => HttpContext.Session.GetString("UserRole") != null;
        public string DashboardUrl => HttpContext.Session.GetString("UserRole") == "Admin" ? "/Admin" : "/User";

        public IndexModel(ILogger<IndexModel> logger)
        {
            _logger = logger;
        }

        public void OnGet()
        {
        }
    }
}
