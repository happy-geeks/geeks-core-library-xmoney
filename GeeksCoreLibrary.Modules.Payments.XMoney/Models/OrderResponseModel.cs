using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class OrderResponseModel
{
    [JsonPropertyName("data")]
    [JsonProperty("data")]
    public OrderResponseDataModel Data { get; set; }

}