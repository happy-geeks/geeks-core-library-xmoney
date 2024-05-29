using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class OrderResponseDataModel
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; set; }
    [JsonPropertyName("type")]
    [JsonProperty("type")]
    public string Type { get; set; }
    [JsonProperty("attributes")]
    [JsonPropertyName("attributes")]
    public OrderResponseAttributesModel Attributes { get; set; }
}