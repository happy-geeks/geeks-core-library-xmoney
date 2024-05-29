using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class XMoneyWebhookModel
{
    [JsonPropertyName("event_type")]
    [JsonProperty("event_type")]
    public string EventType { get; set; }
    [JsonPropertyName("resource")]
    [JsonProperty("resource")]
    public XMoneyResourceModel Resource { get; set; }
    [JsonPropertyName("signature")]
    [JsonProperty("signature")]
    public string Signature { get; set; }
    [JsonPropertyName("state")]
    [JsonProperty("state")]
    public string State { get; set; }
    
    public int StatusCode { get; set; }
}