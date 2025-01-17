using System.Text.Json.Serialization;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class AttributesModel
{
    [JsonPropertyName("order")]
    public OrderModel Order { get; set; } = new();

    [JsonPropertyName("customer")]
    public CustomerModel Customer { get; set; } = new();
}