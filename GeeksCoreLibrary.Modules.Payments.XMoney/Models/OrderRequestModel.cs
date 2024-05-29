using System.Text.Json.Serialization;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class OrderRequestModel
{
    [JsonPropertyName("data")]
    public DataModel Data { get; set; }
}