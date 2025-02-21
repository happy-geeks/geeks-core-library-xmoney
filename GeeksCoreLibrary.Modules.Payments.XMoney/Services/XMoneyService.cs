using System.Globalization;
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
using GeeksCoreLibrary.Modules.Payments.Enums;
using GeeksCoreLibrary.Modules.Payments.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Models;
using GeeksCoreLibrary.Modules.Payments.XMoney.Models;
using GeeksCoreLibrary.Modules.Payments.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using OrderProcessConstants = GeeksCoreLibrary.Components.OrderProcess.Models.Constants;

namespace GeeksCoreLibrary.Modules.Payments.XMoney.Services;

/// <inheritdoc cref="IPaymentServiceProviderService" />
public class XMoneyService(
    IDatabaseHelpersService databaseHelpersService,
    IDatabaseConnection databaseConnection,
    ILogger<PaymentServiceProviderBaseService> logger,
    IOptions<GclSettings> gclSettings,
    IShoppingBasketsService shoppingBasketsService,
    IWiserItemsService wiserItemsService,
    IHttpContextAccessor? httpContextAccessor = null)
    : PaymentServiceProviderBaseService(databaseHelpersService, databaseConnection, logger, httpContextAccessor),
        IPaymentServiceProviderService, IScopedService
{
    private readonly IDatabaseConnection databaseConnection = databaseConnection;
    private readonly ILogger<PaymentServiceProviderBaseService> logger = logger;
    private readonly IHttpContextAccessor? httpContextAccessor = httpContextAccessor;
    private readonly GclSettings gclSettings = gclSettings.Value;
    
    private string? webHookContents;

    private readonly JsonSerializerSettings? jsonSerializerSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore
    };


    /// <inheritdoc />
    public async Task<PaymentRequestResult> HandlePaymentRequestAsync(ICollection<(WiserItemModel Main, List<WiserItemModel> Lines)> conceptOrders, WiserItemModel userDetails, PaymentMethodSettingsModel paymentMethodSettings, string invoiceNumber)
    {
        var failUrl = "";
        try
        {
            var xMoneySettings = (XMoneySettingsModel)paymentMethodSettings.PaymentServiceProvider;
            var validationResult = ValidateXMoneySettings(xMoneySettings);
            failUrl = xMoneySettings.FailUrl;
            if (!validationResult.Valid)
            {
                logger.LogError($"Validation in 'HandlePaymentRequestAsync' of '{nameof(XMoneyService)}' failed because: {validationResult.Message}");
                return new PaymentRequestResult
                {
                    Successful = false,
                    Action = PaymentRequestActions.Redirect,
                    ActionData = failUrl
                };
            }
        
            // Build and execute payment request.
            var restClient = CreateRestClient();
            var restRequest = await CreateRestRequestAsync(xMoneySettings, invoiceNumber, conceptOrders);
            var restResponse = await restClient.ExecuteAsync(restRequest);
            if (restResponse.Content == null)
            {
                return new PaymentRequestResult
                {
                    Successful = false,
                    Action = PaymentRequestActions.Redirect,
                    ErrorMessage = "No response received from the xMoney API.",
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
                    ErrorMessage = "No response received from the xMoney API..",
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
                ActionData = (responseSuccessful) ? xMoneyResponse.Data.Attributes.RedirectUrl : xMoneySettings.FailUrl
            };
        }
        catch (Exception exception)
        {
            // Log any exceptions that may have occurred.
            logger.LogError(exception, "Error handling XMoney payment request");
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
        var error = String.Empty;
        var statusCode = 0;
        var webHookResponseBody = String.Empty;
        var xMoneySettings = (XMoneySettingsModel)paymentMethodSettings.PaymentServiceProvider;
        XMoneyWebhookModel? model = null;
        try
        {
            if (httpContextAccessor?.HttpContext == null)
            {
                error = "No HTTP context available; unable to process status update.";
                return new StatusUpdateResult
                {
                    Successful = false,
                    Status = error,
                    StatusCode = 0
                };
            }
            
            (model, webHookResponseBody) = await GetXMoneyWebhookModelAsync(true, xMoneySettings);
            
            switch (model.State)
            {
                case "completed":
                case "received":
                case "detected":
                    // No error, do nothing
                    break;
                default:
                    error = "State is not completed, received or detected.";
                    statusCode = model.StatusCode;
                    return new StatusUpdateResult
                    {
                        Successful = false,
                        Status = error,
                        StatusCode = statusCode
                    };
            }
            
            return new StatusUpdateResult
            {
                Successful = true,
                Status = "SUCCESS"
            };
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            // Log any exceptions that may have occurred.
            logger.LogError(exception, "Error processing xMoney payment update.");
            return new StatusUpdateResult
            {
                Successful = false,
                Status = "Error processing xMoney payment update.",
                StatusCode = 500
            };
        }
        finally
        {
            await LogIncomingPaymentActionAsync(PaymentServiceProviders.XMoney, model?.Resource.Reference, statusCode, responseBody: webHookResponseBody, error: error);
        }
    }

    /// <inheritdoc />
    public async Task<PaymentServiceProviderSettingsModel> GetProviderSettingsAsync(PaymentServiceProviderSettingsModel paymentServiceProviderSettings)
    {
        databaseConnection.AddParameter("id", paymentServiceProviderSettings.Id);
        const string query = $"""
                              SELECT xMoneyApiKeyLive.`value` AS xMoneyApiKeyLive,
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
                              WHERE paymentServiceProvider.id = ?id
                              """;
        
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
    public async Task<string> GetInvoiceNumberFromRequestAsync()
    {
        try
        {
            if (httpContextAccessor?.HttpContext?.Request.Body == null)
            {
                throw new InvalidOperationException("No HTTP context available.");
            }

            var webhookModel = await GetXMoneyWebhookModelAsync(false);
            
            webhookModel.Model.StatusCode = httpContextAccessor.HttpContext.Response.StatusCode;
            var invoiceId = webhookModel.Model.Resource.Reference;
            if (String.IsNullOrEmpty(invoiceId))
            {
                throw new BadHttpRequestException("No invoice id found in body of xMoney webhook.");
            }
            return invoiceId;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error getting invoice number from request.");
            throw;
        }
    }
    
    #region Helper functions

    private RestClient CreateRestClient()
    {
        var baseUrl = gclSettings.Environment.InList(Environments.Development, Environments.Test) ?  "https://merchants.api.sandbox.crypto.xmoney.com" : "https://merchants.api.crypto.xmoney.com";
        return new RestClient(new RestClientOptions(baseUrl));
    }
    
    private (bool Valid, string? Message) ValidateXMoneySettings(XMoneySettingsModel xMoneySettings)
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
        
        var tax = await shoppingBasketsService.GetPriceAsync(conceptOrders.FirstOrDefault().Main, conceptOrders.FirstOrDefault().Lines, basketSettings, ShoppingBasket.PriceTypes.VatOnly);
        var discount = await shoppingBasketsService.GetPriceAsync(conceptOrders.FirstOrDefault().Main, conceptOrders.FirstOrDefault().Lines, basketSettings, ShoppingBasket.PriceTypes.DiscountInVat);
        var hasShippingAddress = !String.IsNullOrWhiteSpace(conceptOrders.FirstOrDefault().Main.GetDetailValue<string>(ConstantsModel.ShippingPostalCode));
        
        var restRequest = new RestRequest("/api/stores/orders", Method.Post);
        restRequest.AddHeader("Authorization", $"Bearer {xMoneySettings.ApiKey}");
        restRequest.AddHeader("Content-Type", "application/json");
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
                             Total = Math.Round(totalPrice, 2).ToString("0.##", CultureInfo.InvariantCulture),
                             Currency = xMoneySettings.Currency,
                             Details = new DetailsModel
                             {
                                 Subtotal = Math.Round(subTotaal, 2).ToString("0.##", CultureInfo.InvariantCulture),
                                 Tax = Math.Round(tax, 2).ToString("0.##", CultureInfo.InvariantCulture),
                                 Discount = Math.Round(discount, 2).ToString("0.##", CultureInfo.InvariantCulture)
                             }
                         },
                         ReturnUrls = new ReturnUrlsModel
                         {
                             ReturnUrl = xMoneySettings.SuccessUrl,
                             CancelUrl = xMoneySettings.FailUrl,
                             CallbackUrl = xMoneySettings.CallbackUrl
                         },
                         LineItems = []
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
                    Price = price.ToString("0.##", CultureInfo.InvariantCulture),
                    Currency = xMoneySettings.Currency,
                    Quantity = quantity
                };
                xMoneyCreateOrderRequest.Data.Attributes.Order.LineItems.Add(lineItems);
            }
        }
        restRequest.AddJsonBody(xMoneyCreateOrderRequest);
        
        return restRequest;
    }

    private static bool VerifySignature(JObject jsonObject, XMoneySettingsModel xMoneySettings)
    {
        var signatureContent = GenerateStringForSignature(jsonObject);
        var signature = GenerateSignature(xMoneySettings.WebhookSecret, signatureContent);
        
        var requestSignature = jsonObject["signature"];

        if (requestSignature is null)
        {
            throw new InvalidOperationException("No signature found");
        }

        return signature == requestSignature.ToString();
    }
    
    private static string GenerateStringForSignature(JObject jsonObject, string keyPrefix = "")
    {
        var result = new StringBuilder();
        foreach (var jsonProperty in jsonObject.Properties().OrderBy(jp => jp.Name))
        {
            if (String.IsNullOrWhiteSpace(jsonProperty.Name) || String.Equals(jsonProperty.Name, "signature", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
        if (String.IsNullOrWhiteSpace(webhookSecret))
            throw new InvalidOperationException("No xMoney secret key found in Wiser settings!");

        using HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(content));

        if (asBase64String)
            return Convert.ToBase64String(hashBytes);

        StringBuilder hashString = new StringBuilder();
        for (var index = 0; index <= hashBytes.Length - 1; index++)
            hashString.Append(hashBytes[index].ToString("x2"));

        return hashString.ToString();
    }
    
    private async Task<(XMoneyWebhookModel Model, string webHookResponseBody)> GetXMoneyWebhookModelAsync(bool verifySignature, XMoneySettingsModel? xMoneySettings = null)
    {
        if (String.IsNullOrWhiteSpace(webHookContents))
        {
            using StreamReader reader = new(httpContextAccessor!.HttpContext!.Request.Body);
            webHookContents = await reader.ReadToEndAsync();
            
            if (String.IsNullOrWhiteSpace(webHookContents))
            {
                throw new BadHttpRequestException("No JSON found in body of XMoney webhook.");
            }
        }
        
        JObject jObject = JObject.Parse(webHookContents);

        if (verifySignature)
        {
            if (xMoneySettings is null)
            {
                throw new ArgumentNullException(nameof(xMoneySettings), "xMoneySettings cannot be null. If verifySignature is true");
            }
            
            if (!VerifySignature(jObject, xMoneySettings))
            {
                throw new BadHttpRequestException("Signature verification failed.");
            }
        }
        
        var webhookData = jObject.ToObject<XMoneyWebhookModel>();
        if (webhookData == null)
        {
            throw new BadHttpRequestException("Invalid JSON found in body of XMoney webhook.");
        }
        
        return (webhookData, webHookContents);
    }
    
    #endregion
}