using GeeksCoreLibrary.Components.OrderProcess.Models;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Models;

public class XMoneySettingsModel: PaymentServiceProviderSettingsModel
{
    public string ApiKey { get; set; }
    public string CallbackUrl { get; set; }
    public string WebhookSecret { get; set; }
}