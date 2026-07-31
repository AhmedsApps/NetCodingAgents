using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace CodingAgents.Server.Services;

public class WhatsAppService
{
    private readonly string _phone;
    private readonly string _apiKey;
    private readonly HttpClient _client;
    private readonly bool _isEnabled;

    public WhatsAppService(WhatsAppConfig config)
    {
        if (string.IsNullOrEmpty(config.Phone) || string.IsNullOrEmpty(config.ApiKey))
        {
            _phone = string.Empty;
            _apiKey = string.Empty;
            _isEnabled = false;
            Console.WriteLine("[WhatsAppService] WARNING: WhatsApp configurations are incomplete. Service is disabled.");
        }
        else
        {
            _phone = config.Phone;
            _apiKey = config.ApiKey;
            _isEnabled = true;
        }
        _client = new HttpClient();
    }

    public async Task SendNotificationAsync(string message)
    {
        if (!_isEnabled)
        {
            Console.WriteLine("[WhatsAppService] Message not sent: Service is disabled due to missing configuration.");
            return;
        }
        try
        {
            // Escape message text for safety inside URL query parameters
            string escapedMsg = Uri.EscapeDataString(message);
            string url = $"https://api.callmebot.com/whatsapp.php?phone={_phone}&text={escapedMsg}&apikey={_apiKey}";

            var response = await _client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[WhatsAppService] Message sent to {_phone} successfully.");
            }
            else
            {
                string error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[WhatsAppService] Failed sending WhatsApp. Status: {response.StatusCode}, Details: {error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WhatsAppService] Error sending WhatsApp message: {ex.Message}");
        }
    }
}
