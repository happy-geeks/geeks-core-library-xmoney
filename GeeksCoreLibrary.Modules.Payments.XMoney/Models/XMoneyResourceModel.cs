using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class XMoneyResourceModel
{
    [JsonPropertyName("reference")]
    [JsonProperty("reference")]
    public string Reference { get; set; }
    [JsonPropertyName("amount")]
    [JsonProperty("amount")]
    public string Amount { get; set; }
    [JsonPropertyName("currency")]
    [JsonProperty("currency")]
    public string Currency { get; set; }
}