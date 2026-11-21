namespace PaymentGatewayAdapter.Configuration;

/// <summary>
/// Configuration options for Monobank API adapter
/// </summary>
public sealed class MonobankOptions
{
    public const string SectionName = "Monobank";

    /// <summary>
    /// Base URL for Monobank API (default: https://api.monobank.ua/)
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.monobank.ua/";

    /// <summary>
    /// Personal API token (from https://api.monobank.ua/ - "Personal API token")
    /// Used for: client-info, statement, webhook management
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Business API token (from Monobank Business dashboard - "API token")
    /// Used for: invoice/create, invoice/status, merchant operations
    /// </summary>
    public string BusinessToken { get; set; } = string.Empty;

    /// <summary>
    /// Webhook secret for signature verification (X-Sign header)
    /// Set in Monobank Business dashboard → Webhook settings
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Webhook URL for receiving payment notifications
    /// Must be HTTPS, publicly accessible
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Redirect URL after successful payment
    /// </summary>
    public string SuccessRedirectUrl { get; set; } = string.Empty;

    /// <summary>
    /// Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Enable request/response logging (for debugging)
    /// </summary>
    public bool EnableLogging { get; set; } = false;
}