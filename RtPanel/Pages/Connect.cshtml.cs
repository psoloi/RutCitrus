using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RtPanel.Services;

namespace RtPanel.Pages
{
    public class ConnectModel : PageModel
    {
        private readonly RtCliClientService _client;

        public ConnectModel(RtCliClientService client)
        {
            _client = client;
        }

        public void OnGet()
        {
        }
    }
}
