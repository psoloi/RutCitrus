using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RtPanel.Services;

namespace RtPanel.Pages
{
    public class ExtensionsModel : PageModel
    {
        private readonly RtCliClientService _client;

        public ExtensionsModel(RtCliClientService client)
        {
            _client = client;
        }

        public void OnGet()
        {
        }
    }
}
