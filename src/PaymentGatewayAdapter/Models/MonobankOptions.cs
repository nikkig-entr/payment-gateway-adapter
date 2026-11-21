using PaymentGatewayAdapter.Models;

namespace PaymentGatewayAdapter.Models;

/// <summary>
/// Monobank API configuration options
/// Supports both Personal API (statement) and Business API (acquiring)
/// </summary>
public sealed class MonobankOptions : GatewayOptions
{
    /// <summary>
    /// API Token for Personal API (statement retrieval)
    /// Get from: https://api.monobank.ua/ (Personal cabinet)
    /// </summary>
    public string? PersonalToken { get; set; }

    /// <summary>
    /// Merchant ID for Business API (acquiring)
    /// Get from Monobank Business cabinet
    /// </summary>
    public string? MerchantId { get; set; }

    /// <summary>
    /// Secret key for Business API (acquiring)
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// Webhook secret for signature verification (Business API)
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Base URL for Business API (acquiring)
    /// </summary>
    public string BusinessBaseUrl { get; set; } = "https://api.monobank.ua/api/merchant/";

    /// <summary>
    /// Base URL for Personal API (statement)
    /// </summary>
    public string PersonalBaseUrl { get; set; } = "https://api.monobank.ua/personal/";

    /// <summary>
    /// Callback URL for payment notifications
    /// </summary>
    public string? CallbackUrl { get; set; }

    /// <summary>
    /// Success redirect URL
    /// </summary>
    public string? SuccessUrl { get; set; }

    /// <summary>
    /// Failure redirect URL
    /// </summary>
    public string? FailureUrl { get; set; }

    public override void Validate()
    {
        // For acquiring (Business API)
        if (!string.IsNullOrWhiteSpace(MerchantId) || !string.IsNullOrWhiteSpace(SecretKey))
        {
            if (string.IsNullOrWhiteSpace(MerchantId))
                throw new InvalidOperationException("MonobankOptions: MerchantId is required for Business API");
            if (string.IsNullOrWhiteSpace(SecretKey))
                throw new InvalidOperationException("MonobankOptions: SecretKey is required for Business API");
            if (string.IsNullOrWhiteSpace(WebhookSecret))
                throw new InvalidOperationException("MonobankOptions: WebhookSecret is required for Business API");
            if (string.IsNullOrWhiteSpace(CallbackUrl))
                throw new InvalidOperationException("MonobankOptions: CallbackUrl is required for Business API");
        }

        // For statement (Personal API)
        if (!string.IsNullOrWhiteSpace(PersonalToken))
        {
            if (string.IsNullOrWhiteSpace(PersonalToken))
                throw new InvalidOperationException("MonobankOptions: PersonalToken is required for Personal API");
        }

        // At least one API must be configured
        if (string.IsNullOrWhiteSpace(MerchantId) && string.IsNullOrWhiteSpace(PersonalToken))
        {
            throw new InvalidOperationException("MonobankOptions: Either MerchantId (Business API) or PersonalToken (Personal API) must be configured");
        }
    }

    /// <summary>
    /// Whether Business API (acquiring) is configured
    /// </summary>
    public bool HasBusinessApi => !string.IsNullOrWhiteSpace(MerchantId) && !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>
    /// Whether Personal API (statement) is configured
    /// </summary>
    public bool HasPersonalApi => !string.IsNullOrWhiteSpace(PersonalToken);
}