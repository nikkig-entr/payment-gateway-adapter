using PaymentGatewayAdapter.Models;

namespace PaymentGatewayAdapter.Interfaces;

/// <summary>
/// Universal payment gateway interface — implement for each provider
/// </summary>
public interface IPaymentGateway
{
    /// <summary>
    /// Creates a payment and returns payment URL for redirect
    /// </summary>
    /// <param name="request">Payment creation request</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Payment response with redirect URL and transaction ID</returns>
    Task<PaymentResponse> CreatePaymentAsync(PaymentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Verifies webhook signature authenticity
    /// </summary>
    /// <param name="payload">Raw webhook payload</param>
    /// <param name="signature">Signature from webhook header</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if signature is valid</returns>
    Task<bool> VerifyWebhookSignatureAsync(string payload, string signature, CancellationToken ct = default);

    /// <summary>
    /// Parses webhook payload into unified format
    /// </summary>
    /// <param name="payload">Raw webhook payload</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Parsed webhook event</returns>
    Task<WebhookEvent> ParseWebhookAsync(string payload, CancellationToken ct = default);

    /// <summary>
    /// Retrieves transaction statement for reconciliation
    /// </summary>
    /// <param name="from">Start date (inclusive)</param>
    /// <param name="to">End date (inclusive)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of transactions</returns>
    Task<StatementResponse> GetStatementAsync(DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// Gets gateway-specific client info (KYC, limits, etc.)
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Client information</returns>
    Task<ClientInfoResponse> GetClientInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// Gateway identifier (e.g., "monobank", "liqpay", "stripe")
    /// </summary>
    string GatewayId { get; }

    /// <summary>
    /// Human-readable gateway name
    /// </summary>
    string GatewayName { get; }
}