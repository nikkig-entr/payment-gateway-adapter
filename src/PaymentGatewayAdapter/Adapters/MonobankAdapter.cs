using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentGatewayAdapter.Interfaces;
using PaymentGatewayAdapter.Models;

namespace PaymentGatewayAdapter.Adapters;

/// <summary>
/// Monobank Payment Gateway Adapter
/// Supports: Business API (acquiring) + Personal API (statement retrieval)
/// Docs: https://api.monobank.ua/docs/
/// </summary>
public sealed class MonobankAdapter : IPaymentGateway
{
    private readonly HttpClient _httpClient;
    private readonly MonobankOptions _options;
    private readonly ILogger<MonobankAdapter> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public string GatewayId => "monobank";
    public string GatewayName => "Monobank";

    public MonobankAdapter(
        HttpClient httpClient,
        IOptions<MonobankOptions> options,
        ILogger<MonobankAdapter> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        _options.Validate();

        // Configure base address based on which API is available
        if (_options.HasBusinessApi)
        {
            _httpClient.BaseAddress = new Uri(_options.BusinessBaseUrl);
        }
        else if (_options.HasPersonalApi)
        {
            _httpClient.BaseAddress = new Uri(_options.PersonalBaseUrl);
        }
    }

    /// <inheritdoc />
    public async Task<PaymentResponse> CreatePaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        if (!_options.HasBusinessApi)
        {
            return PaymentResponse.Fail("Business API not configured", "CONFIG_ERROR");
        }

        try
        {
            // Build Monobank invoice request
            var invoiceRequest = new MonobankInvoiceRequest
            {
                Amount = request.AmountMinorUnits,
                Ccy = CurrencyCodeFromString(request.Currency),
                OrderId = request.OrderId,
                Description = request.Description,
                CallbackUrl = request.CallbackUrl ?? _options.CallbackUrl!,
                SuccessUrl = request.SuccessUrl ?? _options.SuccessUrl,
                FailureUrl = request.FailureUrl ?? _options.FailureUrl,
                Metadata = request.Metadata,
                IdempotencyKey = request.IdempotencyKey ?? $"{request.OrderId}:1"
            };

            _logger.LogDebug("Creating Monobank invoice for order {OrderId}", request.OrderId);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "invoice/create")
            {
                Content = JsonContent.Create(invoiceRequest, options: _jsonOptions)
            };

            // Add idempotency key header
            if (!string.IsNullOrWhiteSpace(invoiceRequest.IdempotencyKey))
            {
                httpRequest.Headers.Add("X-Idempotency-Key", invoiceRequest.IdempotencyKey);
            }

            var response = await _httpClient.SendAsync(httpRequest, ct);

            var responseContent = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                var invoiceResponse = JsonSerializer.Deserialize<MonobankInvoiceResponse>(responseContent, _jsonOptions);
                if (invoiceResponse != null && !string.IsNullOrEmpty(invoiceResponse.PageUrl))
                {
                    _logger.LogInformation("Monobank invoice created: {InvoiceId} for order {OrderId}",
                        invoiceResponse.InvoiceId, request.OrderId);

                    return PaymentResponse.Ok(
                        transactionId: invoiceResponse.InvoiceId,
                        paymentUrl: invoiceResponse.PageUrl,
                        expiresAt: invoiceResponse.ExpirationDate?.ToDateTime(),
                        qrCode: invoiceResponse.QrCode);
                }

                _logger.LogError("Monobank invoice response missing pageUrl: {Response}", responseContent);
                return PaymentResponse.Fail("Invalid gateway response", "INVALID_RESPONSE", responseContent);
            }

            var error = ParseErrorResponse(responseContent);
            _logger.LogWarning("Monobank invoice creation failed for order {OrderId}: {StatusCode} - {Error}",
                request.OrderId, response.StatusCode, error.Message);

            return PaymentResponse.Fail(error.Message, error.Code, responseContent);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _logger.LogError("Monobank invoice creation timeout for order {OrderId}", request.OrderId);
            return PaymentResponse.Fail("Payment gateway timeout", "TIMEOUT");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monobank invoice creation exception for order {OrderId}", request.OrderId);
            return PaymentResponse.Fail("Internal payment error", "INTERNAL_ERROR");
        }
    }

    /// <inheritdoc />
    public async Task<bool> VerifyWebhookSignatureAsync(string payload, string signature, CancellationToken ct = default)
    {
        if (!_options.HasBusinessApi || string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Webhook verification skipped: Business API or WebhookSecret not configured");
            return false;
        }

        try
        {
            // Monobank uses HMAC-SHA256 with webhook secret as key
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToBase64String(computedHash);

            // Constant-time comparison to prevent timing attacks
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook signature verification failed");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<WebhookEvent> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        try
        {
            var webhook = JsonSerializer.Deserialize<MonobankWebhookPayload>(payload, _jsonOptions);
            if (webhook == null)
            {
                return CreateErrorEvent("INVALID_PAYLOAD", "Failed to deserialize webhook", payload);
            }

            // Map Monobank event types to unified format
            var (eventType, status) = MapEventType(webhook.Type);

            return new WebhookEvent
            {
                EventType = eventType,
                TransactionId = webhook.InvoiceId,
                OrderId = webhook.OrderId,
                AmountMinorUnits = webhook.Amount,
                Currency = CurrencyStringFromCode(webhook.Ccy),
                Status = status,
                OccurredAt = DateTimeOffset.FromUnixTimeSeconds(webhook.CreatedDate).UtcDateTime,
                RawPayload = payload,
                Metadata = new Dictionary<string, object>
                {
                    ["monobank_invoice_id"] = webhook.InvoiceId,
                    ["monobank_type"] = webhook.Type,
                    ["monobank_status"] = webhook.Status
                }
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Monobank webhook JSON");
            return CreateErrorEvent("PARSE_ERROR", "Invalid JSON payload", payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error parsing Monobank webhook");
            return CreateErrorEvent("INTERNAL_ERROR", "Webhook parsing failed", payload);
        }
    }

    /// <inheritdoc />
    public async Task<StatementResponse> GetStatementAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (!_options.HasPersonalApi)
        {
            return new StatementResponse
            {
                Success = false,
                ErrorMessage = "Personal API not configured",
                ErrorCode = "CONFIG_ERROR"
            };
        }

        try
        {
            var fromUnix = ((DateTimeOffset)from).ToUnixTimeSeconds();
            var toUnix = ((DateTimeOffset)to).ToUnixTimeSeconds();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"statement/{fromUnix}/{toUnix}");
            request.Headers.Add("X-Token", _options.PersonalToken!);

            var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Monobank statement request failed: {StatusCode} - {Error}",
                    response.StatusCode, errorContent);

                return new StatementResponse
                {
                    Success = false,
                    ErrorMessage = $"Statement request failed: {response.StatusCode}",
                    ErrorCode = "STATEMENT_ERROR"
                };
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var transactions = JsonSerializer.Deserialize<List<MonobankStatementTransaction>>(content, _jsonOptions);

            return new StatementResponse
            {
                Success = true,
                Transactions = transactions?.Select(MapToTransactionRecord).ToList() ?? new(),
                RetrievedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monobank statement retrieval failed");
            return new StatementResponse
            {
                Success = false,
                ErrorMessage = "Statement retrieval failed",
                ErrorCode = "INTERNAL_ERROR"
            };
        }
    }

    /// <inheritdoc />
    public async Task<ClientInfoResponse> GetClientInfoAsync(CancellationToken ct = default)
    {
        if (!_options.HasPersonalApi)
        {
            return new ClientInfoResponse
            {
                Success = false,
                ErrorMessage = "Personal API not configured",
                ErrorCode = "CONFIG_ERROR"
            };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "client-info");
            request.Headers.Add("X-Token", _options.PersonalToken!);

            var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                return new ClientInfoResponse
                {
                    Success = false,
                    ErrorMessage = $"Client info request failed: {response.StatusCode}",
                    ErrorCode = "CLIENT_INFO_ERROR"
                };
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var clientInfo = JsonSerializer.Deserialize<MonobankClientInfo>(content, _jsonOptions);

            return new ClientInfoResponse
            {
                Success = true,
                ClientId = clientInfo?.ClientId,
                ClientName = clientInfo?.Name,
                Status = clientInfo?.Status,
                AvailableCurrencies = clientInfo?.Accounts?.Select(a => CurrencyStringFromCode(a.CurrencyCode)).Distinct().ToList(),
                LimitsMinorUnits = clientInfo?.Accounts?.ToDictionary(
                    a => CurrencyStringFromCode(a.CurrencyCode),
                    a => a.CreditLimit)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monobank client info retrieval failed");
            return new ClientInfoResponse
            {
                Success = false,
                ErrorMessage = "Client info retrieval failed",
                ErrorCode = "INTERNAL_ERROR"
            };
        }
    }

    // ============================================
    // PRIVATE HELPERS
    // ============================================

    private static (string EventType, string Status) MapEventType(string monobankType)
    {
        return monobankType.ToLowerInvariant() switch
        {
            "invoicecreated" => ("payment.created", "pending"),
            "invoiceapproved" => ("payment.approved", "approved"),
            "invoicedeclined" => ("payment.declined", "declined"),
            "invoiceexpired" => ("payment.expired", "expired"),
            "invoicereversed" => ("payment.reversed", "reversed"),
            _ => ("payment.unknown", "unknown")
        };
    }

    private static int CurrencyCodeFromString(string currency)
    {
        return currency.ToUpperInvariant() switch
        {
            "UAH" => 980,
            "USD" => 840,
            "EUR" => 978,
            "GBP" => 826,
            "PLN" => 985,
            _ => 980 // Default UAH
        };
    }

    private static string CurrencyStringFromCode(int code)
    {
        return code switch
        {
            980 => "UAH",
            840 => "USD",
            978 => "EUR",
            826 => "GBP",
            985 => "PLN",
            _ => "UAH"
        };
    }

    private static (string Code, string Message) ParseErrorResponse(string content)
    {
        try
        {
            var error = JsonSerializer.Deserialize<MonobankErrorResponse>(content, _jsonOptions);
            if (error != null)
            {
                return (error.ErrorCode ?? "GATEWAY_ERROR", error.ErrorMessage ?? "Gateway error");
            }
        }
        catch { }

        return ("GATEWAY_ERROR", "Unknown gateway error");
    }

    private static WebhookEvent CreateErrorEvent(string errorCode, string errorMessage, string payload)
    {
        return new WebhookEvent
        {
            EventType = "payment.error",
            TransactionId = string.Empty,
            OrderId = string.Empty,
            Status = "error",
            RawPayload = payload,
            Error = new WebhookError { Code = errorCode, Message = errorMessage }
        };
    }

    private static TransactionRecord MapToTransactionRecord(MonobankStatementTransaction t)
    {
        return new TransactionRecord
        {
            TransactionId = t.Id,
            AmountMinorUnits = t.Amount,
            Currency = CurrencyStringFromCode(t.CurrencyCode),
            Type = t.Amount >= 0 ? "payment" : "refund",
            Status = "completed",
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(t.Time).UtcDateTime,
            Description = t.Description,
            CardMask = t.MaskedPan,
            RawData = JsonSerializer.Serialize(t, _jsonOptions)
        };
    }

    // ============================================
    // INTERNAL DTOs
    // ============================================

    private sealed record MonobankInvoiceRequest
    {
        public long Amount { get; init; }
        public int Ccy { get; init; }
        public string OrderId { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string CallbackUrl { get; init; } = string.Empty;
        public string? SuccessUrl { get; init; }
        public string? FailureUrl { get; init; }
        public Dictionary<string, object>? Metadata { get; init; }
        public string IdempotencyKey { get; init; } = string.Empty;
    }

    private sealed record MonobankInvoiceResponse
    {
        public string InvoiceId { get; init; } = string.Empty;
        public string PageUrl { get; init; } = string.Empty;
        public long? ExpirationDate { get; init; }
        public string? QrCode { get; init; }
    }

    private sealed record MonobankWebhookPayload
    {
        public string Type { get; init; } = string.Empty;
        public string InvoiceId { get; init; } = string.Empty;
        public string OrderId { get; init; } = string.Empty;
        public long Amount { get; init; }
        public int Ccy { get; init; }
        public string Status { get; init; } = string.Empty;
        public long CreatedDate { get; init; }
    }

    private sealed record MonobankErrorResponse
    {
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }

    private sealed record MonobankStatementTransaction
    {
        public string Id { get; init; } = string.Empty;
        public long Time { get; init; }
        public long Amount { get; init; }
        public int CurrencyCode { get; init; }
        public string Description { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? MaskedPan { get; init; }
    }

    private sealed record MonobankClientInfo
    {
        public string? ClientId { get; init; }
        public string? Name { get; init; }
        public string? Status { get; init; }
        public List<MonobankAccount>? Accounts { get; init; }
    }

    private sealed record MonobankAccount
    {
        public string Id { get; init; } = string.Empty;
        public int CurrencyCode { get; init; }
        public long Balance { get; init; }
        public long CreditLimit { get; init; }
        public string Type { get; init; } = string.Empty;
    }
}