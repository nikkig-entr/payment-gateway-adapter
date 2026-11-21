using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PaymentGatewayAdapter.Models;

/// <summary>
/// Payment creation request (gateway-agnostic)
/// </summary>
public sealed record PaymentRequest
{
    /// <summary>
    /// Unique order ID in merchant system (max 128 chars)
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string OrderId { get; init; } = string.Empty;

    /// <summary>
    /// Amount in minor units (cents/kopiykas) — e.g., 10000 = 100.00 UAH
    /// </summary>
    [Range(1, long.MaxValue)]
    public long AmountMinorUnits { get; init; }

    /// <summary>
    /// ISO 4217 currency code (UAH, USD, EUR, etc.)
    /// </summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; init; } = "UAH";

    /// <summary>
    /// Payment description shown to customer
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Callback URL for webhook notifications
    /// </summary>
    [Required]
    [Url]
    public string CallbackUrl { get; init; } = string.Empty;

    /// <summary>
    /// Redirect URL after successful payment
    /// </summary>
    [Url]
    public string? SuccessUrl { get; init; }

    /// <summary>
    /// Redirect URL after failed payment
    /// </summary>
    [Url]
    public string? FailureUrl { get; init; }

    /// <summary>
    /// Idempotency key — MUST be unique per payment attempt
    /// Use: {OrderId}:{AttemptNumber} or UUID
    /// </summary>
    [MaxLength(128)]
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Additional metadata passed through to gateway
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Customer info for pre-fill / KYC
    /// </summary>
    public CustomerInfo? Customer { get; init; }

    /// <summary>
    /// Payment method restrictions (optional)
    /// </summary>
    public PaymentMethodOptions? PaymentMethods { get; init; }
}

/// <summary>
/// Customer information
/// </summary>
public sealed record CustomerInfo
{
    [MaxLength(100)]
    public string? Name { get; init; }

    [MaxLength(254)]
    [EmailAddress]
    public string? Email { get; init; }

    [MaxLength(20)]
    public string? Phone { get; init; }

    public Address? BillingAddress { get; init; }
    public Address? ShippingAddress { get; init; }
}

/// <summary>
/// Address information
/// </summary>
public sealed record Address
{
    [MaxLength(200)]
    public string? Line1 { get; init; }

    [MaxLength(200)]
    public string? Line2 { get; init; }

    [MaxLength(100)]
    public string? City { get; init; }

    [MaxLength(100)]
    public string? State { get; init; }

    [MaxLength(20)]
    public string? PostalCode { get; init; }

    [MaxLength(2)]
    public string? CountryCode { get; init; } = "UA";
}

/// <summary>
/// Payment method configuration
/// </summary>
public sealed record PaymentMethodOptions
{
    public bool? Card { get; init; } = true;
    public bool? ApplePay { get; init; }
    public bool? GooglePay { get; init; }
    public bool? BankTransfer { get; init; }
    public List<string>? DisabledMethods { get; init; }
}

/// <summary>
/// Payment creation response
/// </summary>
public sealed record PaymentResponse
{
    /// <summary>
    /// Whether payment was created successfully
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gateway transaction ID
    /// </summary>
    public string? TransactionId { get; init; }

    /// <summary>
    /// URL to redirect customer for payment
    /// </summary>
    public string? PaymentUrl { get; init; }

    /// <summary>
    /// QR code data (base64 PNG or SVG) for mobile payments
    /// </summary>
    public string? QrCode { get; init; }

    /// <summary>
    /// Payment expiration timestamp (UTC)
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Machine-readable error code
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Raw gateway response for debugging
    /// </summary>
    public string? RawResponse { get; init; }

    public static PaymentResponse Ok(string transactionId, string paymentUrl, DateTime? expiresAt = null, string? qrCode = null) =>
        new() { Success = true, TransactionId = transactionId, PaymentUrl = paymentUrl, ExpiresAt = expiresAt, QrCode = qrCode };

    public static PaymentResponse Fail(string errorMessage, string errorCode, string? rawResponse = null) =>
        new() { Success = false, ErrorMessage = errorMessage, ErrorCode = errorCode, RawResponse = rawResponse };
}

/// <summary>
/// Unified webhook event (gateway-agnostic)
/// </summary>
public sealed record WebhookEvent
{
    /// <summary>
    /// Event type: payment.created, payment.approved, payment.declined, payment.expired, payment.refunded, payment.reversed
    /// </summary>
    [Required]
    public string EventType { get; init; } = string.Empty;

    /// <summary>
    /// Gateway transaction ID
    /// </summary>
    [Required]
    public string TransactionId { get; init; } = string.Empty;

    /// <summary>
    /// Merchant order ID
    /// </summary>
    [Required]
    public string OrderId { get; init; } = string.Empty;

    /// <summary>
    /// Amount in minor units
    /// </summary>
    public long AmountMinorUnits { get; init; }

    /// <summary>
    /// Currency code
    /// </summary>
    public string Currency { get; init; } = "UAH";

    /// <summary>
    /// Payment status: pending, approved, declined, expired, refunded, reversed
    /// </summary>
    [Required]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Event timestamp (UTC)
    /// </summary>
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Raw gateway payload for audit
    /// </summary>
    public string RawPayload { get; init; } = string.Empty;

    /// <summary>
    /// Additional gateway-specific data
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Error details if status is declined/failed
    /// </summary>
    public WebhookError? Error { get; init; }
}

/// <summary>
/// Webhook error details
/// </summary>
public sealed record WebhookError
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Reason { get; init; }
}

/// <summary>
/// Statement retrieval response
/// </summary>
public sealed record StatementResponse
{
    public bool Success { get; init; }
    public List<TransactionRecord> Transactions { get; init; } = new();
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public DateTime RetrievedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Individual transaction record for reconciliation
/// </summary>
public sealed record TransactionRecord
{
    /// <summary>
    /// Gateway transaction ID
    /// </summary>
    [Required]
    public string TransactionId { get; init; } = string.Empty;

    /// <summary>
    /// Merchant order ID
    /// </summary>
    public string? OrderId { get; init; }

    /// <summary>
    /// Amount in minor units (positive for income, negative for refunds)
    /// </summary>
    public long AmountMinorUnits { get; init; }

    /// <summary>
    /// Currency code
    /// </summary>
    public string Currency { get; init; } = "UAH";

    /// <summary>
    /// Transaction type: payment, refund, reversal, fee, payout
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Status: completed, pending, failed, cancelled
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Transaction timestamp (UTC)
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Settlement timestamp (UTC) — when funds hit merchant account
    /// </summary>
    public DateTime? SettledAt { get; init; }

    /// <summary>
    /// Gateway fee in minor units
    /// </summary>
    public long? FeeMinorUnits { get; init; }

    /// <summary>
    /// Description
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Card mask (e.g., 4444********1111)
    /// </summary>
    public string? CardMask { get; init; }

    /// <summary>
    /// Raw gateway data
    /// </summary>
    public string? RawData { get; init; }
}

/// <summary>
/// Client info response (KYC, limits, etc.)
/// </summary>
public sealed record ClientInfoResponse
{
    public bool Success { get; init; }
    public string? ClientId { get; init; }
    public string? ClientName { get; init; }
    public string? Status { get; init; } // verified, pending, rejected
    public List<string>? AvailableCurrencies { get; init; }
    public Dictionary<string, long>? LimitsMinorUnits { get; init; } // per currency
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Gateway configuration options
/// </summary>
public abstract class GatewayOptions
{
    /// <summary>
    /// Merchant ID / Public Key
    /// </summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>
    /// Secret Key / Private Key
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Webhook secret for signature verification
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Base API URL (sandbox/production)
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Request timeout
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enable test/sandbox mode
    /// </summary>
    public bool Sandbox { get; set; } = true;

    /// <summary>
    /// Validate required fields
    /// </summary>
    public virtual void Validate()
    {
        if (string.IsNullOrWhiteSpace(MerchantId))
            throw new InvalidOperationException($"{GetType().Name}: MerchantId is required");
        if (string.IsNullOrWhiteSpace(SecretKey))
            throw new InvalidOperationException($"{GetType().Name}: SecretKey is required");
        if (string.IsNullOrWhiteSpace(WebhookSecret))
            throw new InvalidOperationException($"{GetType().Name}: WebhookSecret is required");
        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new InvalidOperationException($"{GetType().Name}: BaseUrl is required");
    }
}