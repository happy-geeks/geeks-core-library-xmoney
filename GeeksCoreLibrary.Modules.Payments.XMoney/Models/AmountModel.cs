using System.Text.Json.Serialization;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class AmountModel
{
    [JsonPropertyName("total")]
    public string Total { get; set; }
    [JsonPropertyName("currency")]
    public string Currency { get; set; }
    [JsonPropertyName("details")]
    public DetailsModel Details { get; set; }
}