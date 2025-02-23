using Microsoft.AspNetCore.SignalR;

namespace RutCitrusWeb.Hubs
{
    public class NotificationHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();
        }
    }
} 