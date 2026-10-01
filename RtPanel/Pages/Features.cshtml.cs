using Microsoft.AspNetCore.Mvc.RazorPages;
using RtPanel.Services;

namespace RtPanel.Pages
{
    public class FeaturesModel : PageModel
    {
        private readonly RtCliClientService _client;

        public FeaturesModel(RtCliClientService client)
        {
            _client = client;
        }

        public void OnGet()
        {
        }
    }
}