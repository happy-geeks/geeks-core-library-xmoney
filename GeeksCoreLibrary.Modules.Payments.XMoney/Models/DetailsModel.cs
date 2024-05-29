using System.Text.Json.Serialization;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class DetailsModel
{
    [JsonPropertyName("subtotal")]
    public string Subtotal { get; set; }
    [JsonPropertyName("shipping")]
    public string Shipping { get; set; }
    [JsonPropertyName("tax")]
    public string Tax { get; set; }
    [JsonPropertyName("discount")]
    public string Discount { get; set; }
}