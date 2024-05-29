using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class OrderResponseAttributesModel
{
    [JsonPropertyName("redirect_url")]
    [JsonProperty("redirect_url")]
    public string RedirectUrl { get; set; }
}