using System.Text.Json.Serialization;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class OrderModel
{
    [JsonPropertyName("reference")]
    public string Reference { get; set; }
    [JsonPropertyName("amount")]
    public AmountModel Amount { get; set; }
    [JsonPropertyName("return_urls")]
    public ReturnUrlsModel ReturnUrls { get; set; }
    [JsonPropertyName("line_items")]
    public List<LineItemModel> LineItems { get; set; }
}