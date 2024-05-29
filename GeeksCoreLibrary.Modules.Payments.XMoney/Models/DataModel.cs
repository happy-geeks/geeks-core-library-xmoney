using System.Text.Json.Serialization;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class DataModel
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("attributes")]
    public AttributesModel Attributes { get; set; }
}