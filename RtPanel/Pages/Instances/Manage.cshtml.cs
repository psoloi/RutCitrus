using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RtPanel.Pages.Instances
{
    public class ManageModel : PageModel
    {
        [BindProperty(SupportsGet = true)]
        public string Id { get; set; } = "";

        public void OnGet()
        {
        }
    }
}
