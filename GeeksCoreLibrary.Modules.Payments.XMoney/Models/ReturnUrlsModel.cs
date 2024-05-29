using System.Text.Json.Serialization;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class ReturnUrlsModel
{
    [JsonPropertyName("return_url")]
    public string ReturnUrl { get; set; }
    [JsonPropertyName("cancel_url")]
    public string CancelUrl { get; set; }
    [JsonPropertyName("callback_url")]
    public string CallbackUrl { get; set; }
}