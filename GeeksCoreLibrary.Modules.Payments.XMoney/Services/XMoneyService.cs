using System.Net;
using System.Security.Cryptography;
using System.Text;
using GeeksCoreLibrary.Components.OrderProcess.Models;
using GeeksCoreLibrary.Components.ShoppingBasket;
using GeeksCoreLibrary.Components.ShoppingBasket.Interfaces;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Enums;
using GeeksCoreLibrary.Core.Extensions;
using GeeksCoreLibrary.Core.Interfaces;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using GeeksCoreLibrary.Modules.Objects.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Enums;
using GeeksCoreLibrary.Modules.Payments.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Models;
using GeeksCoreLibrary.Modules.Payments.XMoney.Models;
using GeeksCoreLibrary.Modules.Payments.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using RestSharp.Authenticators;
using OrderProcessConstants = GeeksCoreLibrary.Components.OrderProcess.Models.Constants;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Services;

/// <inheritdoc cref="IPaymentServiceProviderService" />
public class XMoneyService : PaymentServiceProviderBaseService, IPaymentServiceProviderService, IScopedService
{
    private readonly IDatabaseConnection databaseConnection;
    private readonly ILogger<PaymentServiceProviderBaseService> logger;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly GclSettings gclSettings;
    private readonly IShoppingBasketsService shoppingBasketsService;
    private readonly IWiserItemsService wiserItemsService;
    private readonly IObjectsService objectsService;
    private string webHookContents = null;
    private XMoneyWebhookModel? webhookData = null;
    private string webhookSecret = null;
    private string baseUrl = null;

    private readonly JsonSerializerSettings? jsonSerializerSettings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    public XMoneyService(
        IDatabaseHelpersService databaseHelpersService,
        IDatabaseConnection databaseConnection,
        ILogger<PaymentServiceProviderBaseService> logger,
        IOptions<GclSettings> gclSettings,
        IShoppingBasketsService shoppingBasketsService,
        IWiserItemsService wiserItemsService,
        IObjectsService objectsService,
        IHttpContextAccessor httpContextAccessor = null) : base(databaseHelpersService, databaseConnection, logger, httpContextAccessor)
    {
        this.databaseConnection = databaseConnection;
        this.logger = logger;
        this.shoppingBasketsService = shoppingBasketsService;
        this.wiserItemsService = wiserItemsService;
        this.objectsService = objectsService;
        this.gclSettings = gclSettings.Value;
        this.httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public async Task<PaymentRequestResult> HandlePaymentRequestAsync(ICollection<(WiserItemModel Main, List<WiserItemModel> Lines)> conceptOrders, WiserItemModel userDetails,
        PaymentMethodSettingsModel paymentMethodSettings, string invoiceNumber)
    {
        var failUrl = "";
        try
        {
            var xMoneySettings = (XMoneySettingsModel)paymentMethodSettings.PaymentServiceProvider;
            var validationResult = ValidatePayPalSettings(xMoneySettings);
            failUrl = xMoneySettings.FailUrl;
            if (!validationResult.Valid)
            {
                logger.LogError("Validation in 'HandlePaymentRequestAsync' of 'PayPalService' failed because: {Message}", validationResult.Message);
                return new PaymentRequestResult
                {
                    Successful = false,
                    Action = PaymentRequestActions.Redirect,
                    ActionData = failUrl
                };
            }
        
            // Build and execute payment request.
            baseUrl = gclSettings.Environment.InList(Environments.Development, Environments.Test) ?  "https://merchants.api.sandbox.crypto.xmoney.com" : "https://merchants.api.crypto.xmoney.com";
            var restClient = CreateRestClient(baseUrl);
            var restRequest = await CreateRestRequestAsync(xMoneySettings, invoiceNumber, conceptOrders);
            restRequest.AddHeader("Authorization", $"Bearer {xMoneySettings.ApiKey}");
            restRequest.AddHeader("Content-Type", "application/json");
            var restResponse = await restClient.ExecuteAsync(restRequest);
            if (restResponse.Content == null)
            {
                return new PaymentRequestResult
                {
                    Successful = false,
                    Action = PaymentRequestActions.Redirect,
                    ErrorMessage = "No response received from the PayPal",
                    ActionData = failUrl
                };
            }
            var xMoneyResponse = JsonConvert.DeserializeObject<OrderResponseModel>(restResponse.Content, jsonSerializerSettings);
            var responseSuccessful = restResponse.StatusCode == HttpStatusCode.Created;
            if (xMoneyResponse == null)
            {
                return new PaymentRequestResult
                {
                    Successful = false,
                    Action = PaymentRequestActions.Redirect,
                    ErrorMessage = "No response received from PayPal.",
                    ActionData = failUrl
                };
            }
            
            foreach (var conceptOrder in conceptOrders)
            {
                conceptOrder.Main.SetDetail(OrderProcessConstants.PaymentProviderTransactionId, xMoneyResponse.Data.Id);
                await wiserItemsService.SaveAsync(conceptOrder.Main, skipPermissionsCheck: true);
            }
            
            return new PaymentRequestResult
            {
                Successful = responseSuccessful,
                Action = PaymentRequestActions.Redirect,
                ErrorMessage = "No response received from PayPal.",
                ActionData = (responseSuccessful) ? xMoneyResponse.Data.Attributes.RedirectUrl : xMoneySettings.FailUrl
            };
        }
        catch (Exception exception)
        {
            // Log any exceptions that may have occurred.
            logger.LogError(exception, "Error handling PayPal payment request");
            return new PaymentRequestResult
            {
                Successful = false,
                Action = PaymentRequestActions.Redirect,
                ActionData = failUrl
            };
        }
    }

    /// <inheritdoc />
    public async Task<StatusUpdateResult> ProcessStatusUpdateAsync(OrderProcessSettingsModel orderProcessSettings, PaymentMethodSettingsModel paymentMethodSettings)
    {
        var error = "";
        var statusCode = 0;
        var responseBody = "";
        try
        {
            if (httpContextAccessor?.HttpContext == null)
            {
                error = "No HTTP context available; unable to process status update.";
                return new StatusUpdateResult
                {
                    Successful = false,
                    Status = error,
                    StatusCode = statusCode
                };
            }
            var jsonObject = JObject.Parse(webHookContents);
            var signatureString = GenerateStringForSignature(jsonObject);
            var ourSignature = GenerateSignature(webhookSecret, signatureString);

            if (webhookData == null)
            {
                error = "No webhook data found.";
                return new StatusUpdateResult
                {
                    Successful = false,
                    Status = error,
                    StatusCode = statusCode
                };
            }
            
            switch (webhookData.State)
            {
                case "completed":
                case "received":
                case "detected":
                    // No error, do nothing
                    break;
                default:
                    error = "State is not completed, received or detected.";
                    return new StatusUpdateResult
                    {
                        Successful = false,
                        Status = error,
                        StatusCode = statusCode
                    };
            }
            
            statusCode = webhookData.StatusCode;
            var successFul = webhookData.Signature == ourSignature;

            if (!successFul)
            {
                error = "The signature we have does not align with the signature found in the webhook data.";
                return new StatusUpdateResult
                {
                    Successful = false,
                    Status = error,
                    StatusCode = statusCode
                };
            }
            
            return new StatusUpdateResult
            {
                Successful = successFul,
                Status = "SUCCESS"
            };
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            // Log any exceptions that may have occurred.
            logger.LogError(exception, "Error processing PayPal payment update.");
            return new StatusUpdateResult
            {
                Successful = false,
                Status = "Error processing PayPal payment update.",
                StatusCode = 500
            };
        }
        finally
        {
            var invoiceNumber = webhookData?.Resource.Reference;
            await LogIncomingPaymentActionAsync(PaymentServiceProviders.XMoney, invoiceNumber, statusCode, responseBody: webHookContents, error: error);
        }
    }

    /// <inheritdoc />
    public async Task<PaymentServiceProviderSettingsModel> GetProviderSettingsAsync(PaymentServiceProviderSettingsModel paymentServiceProviderSettings)
    {
        databaseConnection.AddParameter("id", paymentServiceProviderSettings.Id);
        var query = $@"SELECT
        xMoneyApiKeyLive.`value` AS xMoneyApiKeyLive,
        xMoneyApiKeyTest.`value` AS xMoneyApiKeyTest,
        xMoneyNotifyUrlLive.`value` AS xMoneyNotifyUrlLive,
        xMoneyNotifyUrlTest.`value` AS xMoneyNotifyUrlTest,
        xMoneyWebhookSecretLive.`value` AS xMoneyWebhookSecretLive,
        xMoneyWebhookSecretTest.`value` AS xMoneyWebhookSecretTest
        FROM {WiserTableNames.WiserItem} AS paymentServiceProvider
        LEFT JOIN {WiserTableNames.WiserItemDetail} AS xMoneyApiKeyLive ON xMoneyApiKeyLive.item_id = paymentServiceProvider.id AND xMoneyApiKeyLive.`key` = '{ConstantsModel.XMoneyApiKeyLive}'
        LEFT JOIN {WiserTableNames.WiserItemDetail} AS xMoneyApiKeyTest ON xMoneyApiKeyTest.item_id = paymentServiceProvider.id AND xMoneyApiKeyTest.`key` = '{ConstantsModel.XMoneyApiKeyTest}'
        LEFT JOIN {WiserTableNames.WiserItemDetail} AS xMoneyNotifyUrlLive ON xMoneyNotifyUrlLive.item_id = paymentServiceProvider.id AND xMoneyNotifyUrlLive.`key` = '{ConstantsModel.XMoneyNotifyUrlLive}'
        LEFT JOIN {WiserTableNames.WiserItemDetail} AS xMoneyNotifyUrlTest ON xMoneyNotifyUrlTest.item_id = paymentServiceProvider.id AND xMoneyNotifyUrlTest.`key` = '{ConstantsModel.XMoneyNotifyUrlTest}'
        LEFT JOIN {WiserTableNames.WiserItemDetail} AS xMoneyWebhookSecretLive ON xMoneyWebhookSecretLive.item_id = paymentServiceProvider.id AND xMoneyWebhookSecretLive.`key` = '{ConstantsModel.xMoneyWebhookSecretLive}'
        LEFT JOIN {WiserTableNames.WiserItemDetail} AS xMoneyWebhookSecretTest ON xMoneyWebhookSecretTest.item_id = paymentServiceProvider.id AND xMoneyWebhookSecretTest.`key` = '{ConstantsModel.xMoneyWebhookSecretTest}'
        WHERE paymentServiceProvider.id = ?id";
        
        try
        {
            var result = new XMoneySettingsModel
            {
                Id = paymentServiceProviderSettings.Id,
                Title = paymentServiceProviderSettings.Title,
                Type = paymentServiceProviderSettings.Type,
                LogAllRequests = paymentServiceProviderSettings.LogAllRequests,
                OrdersCanBeSetDirectlyToFinished = paymentServiceProviderSettings.OrdersCanBeSetDirectlyToFinished,
                SkipPaymentWhenOrderAmountEqualsZero = paymentServiceProviderSettings.SkipPaymentWhenOrderAmountEqualsZero
            };
            var dataTable = await databaseConnection.GetAsync(query);

            if (dataTable.Rows.Count == 0)
            {
                return result;
            }
            var row = dataTable.Rows[0];

            var suffix = gclSettings.Environment.InList(Environments.Development, Environments.Test) ? "Test" : "Live";
            result.ApiKey = row.GetAndDecryptSecretKey($"xMoneyApiKey{suffix}");
            result.WebhookSecret = row.GetAndDecryptSecretKey($"xMoneyWebhookSecret{suffix}");
            webhookSecret = result.WebhookSecret;
            result.CallbackUrl = row.GetAndDecryptSecretKey($"xMoneyNotifyUrl{suffix}");
            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error getting provider settings.");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<String> GetInvoiceNumberFromRequestAsync()
    {
        try
        {
            if (httpContextAccessor.HttpContext?.Request.Body == null)
            {
                throw new Exception("No HTTP context available.");
            }
            
            using StreamReader reader = new(httpContextAccessor.HttpContext.Request.Body);
            webHookContents = await reader.ReadToEndAsync();
            if (String.IsNullOrWhiteSpace(webHookContents))
            {
                throw new Exception("No JSON found in body of PayPal webhook.");
            }

            webhookData = JsonConvert.DeserializeObject<XMoneyWebhookModel>(webHookContents, jsonSerializerSettings);
            if (webhookData == null)
            {
                throw new Exception("Invalid JSON found in body of PayPal webhook.");
            }
            
            webhookData.StatusCode = httpContextAccessor.HttpContext.Response.StatusCode;
            var invoiceId = webhookData.Resource.Reference;
            if (String.IsNullOrEmpty(invoiceId))
            {
                throw new Exception("No invoice id found in body of PayPal webhook.");
            }
            return invoiceId;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error getting invoice number from request.");
            throw;
        }
    }

    private static RestClient CreateRestClient(string baseUrl)
    {
        return new RestClient(new RestClientOptions(baseUrl));
    }
    
    private (bool Valid, string Message) ValidatePayPalSettings(XMoneySettingsModel xMoneySettings)
    {
        if (String.IsNullOrEmpty(xMoneySettings.ApiKey) || String.IsNullOrEmpty(xMoneySettings.CallbackUrl))
        {
            return (false, "xMoney misconfigured: No api key or callback url.");
        }

        return (true, null);
    }
    
    private async Task<RestRequest> CreateRestRequestAsync(XMoneySettingsModel xMoneySettings, string invoiceNumber, ICollection<(WiserItemModel Main, List<WiserItemModel> Lines)> conceptOrders)
    {
        var basketSettings = await shoppingBasketsService.GetSettingsAsync();
        var totalPrice = await shoppingBasketsService.GetPriceAsync(conceptOrders.FirstOrDefault().Main, conceptOrders.FirstOrDefault().Lines, basketSettings, ShoppingBasket.PriceTypes.PspPriceInVat);
        var subTotaal = await shoppingBasketsService.GetPriceAsync(conceptOrders.FirstOrDefault().Main, conceptOrders.FirstOrDefault().Lines, basketSettings, ShoppingBasket.PriceTypes.ExVatExDiscount);
        // ToDo
        //var shipping = await shoppingBasketsService.GetPriceAsync(conceptOrders.FirstOrDefault().Main, conceptOrders.FirstOrDefault().Lines, basketSettings, ShoppingBasket.PriceTypes.);
        var tax = await shoppingBasketsService.GetPriceAsync(conceptOrders.FirstOrDefault().Main, conceptOrders.FirstOrDefault().Lines, basketSettings, ShoppingBasket.PriceTypes.VatOnly);
        var discount = await shoppingBasketsService.GetPriceAsync(conceptOrders.FirstOrDefault().Main, conceptOrders.FirstOrDefault().Lines, basketSettings, ShoppingBasket.PriceTypes.DiscountInVat);
        var hasShippingAddress = !String.IsNullOrWhiteSpace(conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.ShippingPostalCode));
        
        var restRequest = new RestRequest("/api/stores/orders", Method.Post);
        var xMoneyCreateOrderRequest = new OrderRequestModel
        {
            Data = new DataModel
            {
                 Type = "orders",
                 Attributes = new AttributesModel
                 {
                     Order = new OrderModel
                     {
                         Reference = invoiceNumber,
                         Amount = new AmountModel
                         {
                             Total = Math.Round(totalPrice, 2).ToString("0.##").Replace(",", ".").Replace(".", "."),
                             Currency = xMoneySettings.Currency,
                             Details = new DetailsModel
                             {
                                 Subtotal = Math.Round(subTotaal, 2).ToString("0.##").Replace(",", ".").Replace(".", "."),
                                 Tax = Math.Round(tax, 2).ToString("0.##").Replace(",", ".").Replace(".", "."),
                                 Discount = Math.Round(discount, 2).ToString("0.##").Replace(",", ".").Replace(".", ".")
                             }
                         },
                         ReturnUrls = new ReturnUrlsModel
                         {
                             
                             ReturnUrl = xMoneySettings.SuccessUrl,
                             CancelUrl = xMoneySettings.FailUrl,
                             CallbackUrl = xMoneySettings.CallbackUrl
                         },
                         LineItems = new List<LineItemModel>()
                     },
                     Customer = new CustomerModel
                     {
                         Name = $"{conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.GivenName)} {conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.Surname)}",
                         FirstName = conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.GivenName),
                         LastName = conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.Surname),
                         Email = conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.EmailAddress),
                         BillingAddress = $"{conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(hasShippingAddress ? ConstantsModel.ShippingStreet : ConstantsModel.Street)} {conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(hasShippingAddress ? ConstantsModel.ShippingHouseNumber : ConstantsModel.HouseNumber)}",
                         Address1 = $"{conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.Street)} {conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.HouseNumber)}{conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.HouseNumberSuffix)}",
                         Address2 = $"{conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.ShippingStreet)} {conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.ShippingHouseNumber)}{conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.ShippingHouseNumberSuffix)}",
                         City = conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.City),
                         PostCode = conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.PostalCode),
                         Country = conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.Country).ToUpper(),
                     }
                 }
            }
        };
        
        foreach (var conceptOrder in conceptOrders)
        {
            foreach (var orderLine in conceptOrder.Lines)
            {
                var price = await shoppingBasketsService.GetLinePriceAsync(conceptOrder.Main, orderLine, basketSettings, ShoppingBasket.PriceTypes.ExVatExDiscount, true);
                var quantity = orderLine.GetDetailValue<int>(ConstantsModel.Quantity);
                
                var lineItems = new LineItemModel
                {
                    Name = orderLine.GetDetailValue<string>(ConstantsModel.Title),
                    Price = price.ToString("0.##").Replace(",", ".").Replace(".", "."),
                    Currency = xMoneySettings.Currency,
                    Quantity = quantity
                };
                xMoneyCreateOrderRequest.Data.Attributes.Order.LineItems.Add(lineItems);
            }
        }
        restRequest.AddJsonBody(xMoneyCreateOrderRequest);
        
        return restRequest;
    }
    
    private string GenerateStringForSignature(JObject jsonObject, string keyPrefix = "")
    {
        var result = new StringBuilder();
        foreach (var jsonProperty in jsonObject.Properties().OrderBy(jp => jp.Name))
        {
            if (string.IsNullOrWhiteSpace(jsonProperty.Name) || string.Equals(jsonProperty.Name, "signature", StringComparison.OrdinalIgnoreCase))
                continue;

            if (jsonProperty.Value.Type == JTokenType.Object)
            {
                result.Append(GenerateStringForSignature((JObject)jsonProperty.Value, $"{keyPrefix}{jsonProperty.Name}"));
            }
            else
            {
                result.Append($"{keyPrefix}{jsonProperty.Name}{jsonProperty.Value.ToString()}");
            }
        }
        return result.ToString();
    }
    
    private static string GenerateSignature(string webhookSecret, string content, bool asBase64String = false)
    {
        var secret = webhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
            throw new Exception("No XMoney secret key found in Wiser settings!");

        using HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(content));

        if (asBase64String)
            return Convert.ToBase64String(hashBytes);

        StringBuilder hashString = new StringBuilder();
        for (int index = 0; index <= hashBytes.Length - 1; index++)
            hashString.Append(hashBytes[index].ToString("x2"));

        return hashString.ToString();
    }
}