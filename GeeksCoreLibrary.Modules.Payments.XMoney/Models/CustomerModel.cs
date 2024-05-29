using System.Text.Json.Serialization;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class CustomerModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; }
    [JsonPropertyName("first_name")]
    public string FirstName { get; set; }
    [JsonPropertyName("last_name")]
    public string LastName { get; set; }
    [JsonPropertyName("email")]
    public string Email { get; set; }
    [JsonPropertyName("billing_address")]
    public string BillingAddress { get; set; }
    [JsonPropertyName("address1")]
    public string Address1 { get; set; }
    [JsonPropertyName("address2")]
    public string Address2 { get; set; }
    [JsonPropertyName("city")]
    public string City { get; set; }
    [JsonPropertyName("state")]
    public string State { get; set; }
    [JsonPropertyName("postcode")]
    public string PostCode { get; set; }
    [JsonPropertyName("country")]
    public string Country { get; set; }
}