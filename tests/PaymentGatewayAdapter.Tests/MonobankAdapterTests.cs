using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Moq;
using Moq.Protected;
using PaymentGatewayAdapter.Adapters;
using PaymentGatewayAdapter.Interfaces;
using PaymentGatewayAdapter.Models;
using Xunit;

namespace PaymentGatewayAdapter.Tests;

public class MonobankAdapterTests
{
    private readonly MonobankOptions _options;
    private readonly Mock<HttpMessageHandler> _handlerMock;
    private readonly HttpClient _httpClient;
    private readonly MonobankAdapter _adapter;

    public MonobankAdapterTests()
    {
        _options = new MonobankOptions
        {
            MerchantId = "test_merchant",
            SecretKey = "test_secret",
            WebhookSecret = "test_webhook_secret",
            BusinessBaseUrl = "https://api.monobank.ua/api/merchant/",
            CallbackUrl = "https://example.com/callback",
            SuccessUrl = "https://example.com/success",
            FailureUrl = "https://example.com/failure"
        };

        _handlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_handlerMock.Object)
        {
            BaseAddress = new Uri(_options.BusinessBaseUrl)
        };

        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<MonobankAdapter>();

        _adapter = new MonobankAdapter(_httpClient, Options.Create(_options), logger);
    }

    [Fact]
    public async Task CreatePaymentAsync_Success_ReturnsPaymentResponse()
    {
        // Arrange
        var request = new PaymentRequest
        {
            OrderId = "ORDER-123",
            AmountMinorUnits = 10000,
            Currency = "UAH",
            Description = "Test payment",
            CallbackUrl = "https://example.com/callback"
        };

        var expectedResponse = new
        {
            invoiceId = "inv_abc123",
            pageUrl = "https://pay.monobank.ua/inv_abc123",
            expirationDate = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            qrCode = "base64qr=="
        };

        SetupHttpResponse(HttpStatusCode.OK, expectedResponse);

        // Act
        var result = await _adapter.CreatePaymentAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("inv_abc123", result.TransactionId);
        Assert.Equal("https://pay.monobank.ua/inv_abc123", result.PaymentUrl);
        Assert.NotNull(result.ExpiresAt);
        Assert.Equal("base64qr==", result.QrCode);
    }

    [Fact]
    public async Task CreatePaymentAsync_InvalidOrder_ReturnsError()
    {
        // Arrange
        var request = new PaymentRequest
        {
            OrderId = "",
            AmountMinorUnits = 10000,
            Currency = "UAH",
            Description = "Test",
            CallbackUrl = "https://example.com/callback"
        };

        var errorResponse = new { errorCode = "INVALID_ORDER", errorMessage = "Order ID is required" };
        SetupHttpResponse(HttpStatusCode.BadRequest, errorResponse);

        // Act
        var result = await _adapter.CreatePaymentAsync(request);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("INVALID_ORDER", result.ErrorCode);
        Assert.Contains("Order ID is required", result.ErrorMessage);
    }

    [Fact]
    public async Task CreatePaymentAsync_InsufficientFunds_ReturnsDeclined()
    {
        // Arrange
        var request = new PaymentRequest
        {
            OrderId = "ORDER-123",
            AmountMinorUnits = 1000000,
            Currency = "UAH",
            Description = "Large payment",
            CallbackUrl = "https://example.com/callback"
        };

        var errorResponse = new { errorCode = "INSUFFICIENT_FUNDS", errorMessage = "Insufficient funds" };
        SetupHttpResponse(HttpStatusCode.PaymentRequired, errorResponse);

        // Act
        var result = await _adapter.CreatePaymentAsync(request);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("INSUFFICIENT_FUNDS", result.ErrorCode);
    }

    [Fact]
    public async Task VerifyWebhookSignature_ValidSignature_ReturnsTrue()
    {
        // Arrange
        var payload = """{"type":"invoiceapproved","invoiceId":"inv_123","orderId":"ORDER-123","amount":10000,"ccy":980,"status":"approved","createdDate":1699999999}""";

        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret!));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var signature = Convert.ToBase64String(hash);

        // Act
        var result = await _adapter.VerifyWebhookSignatureAsync(payload, signature);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task VerifyWebhookSignature_InvalidSignature_ReturnsFalse()
    {
        // Arrange
        var payload = """{"type":"invoiceapproved","invoiceId":"inv_123"}""";
        var invalidSignature = "invalid_signature";

        // Act
        var result = await _adapter.VerifyWebhookSignatureAsync(payload, invalidSignature);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task VerifyWebhookSignature_TamperedPayload_ReturnsFalse()
    {
        // Arrange
        var originalPayload = """{"type":"invoiceapproved","invoiceId":"inv_123","amount":10000}""";
        var tamperedPayload = """{"type":"invoiceapproved","invoiceId":"inv_123","amount":999999}""";

        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret!));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(originalPayload));
        var signature = Convert.ToBase64String(hash);

        // Act
        var result = await _adapter.VerifyWebhookSignatureAsync(tamperedPayload, signature);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ParseWebhookAsync_InvoiceApproved_ReturnsApprovedEvent()
    {
        // Arrange
        var payload = """{"type":"invoiceapproved","invoiceId":"inv_abc123","orderId":"ORDER-123","amount":10000,"ccy":980,"status":"approved","createdDate":1699999999}""";

        // Act
        var result = await _adapter.ParseWebhookAsync(payload);

        // Assert
        Assert.Equal("payment.approved", result.EventType);
        Assert.Equal("inv_abc123", result.TransactionId);
        Assert.Equal("ORDER-123", result.OrderId);
        Assert.Equal(10000, result.AmountMinorUnits);
        Assert.Equal("UAH", result.Currency);
        Assert.Equal("approved", result.Status);
        Assert.NotNull(result.Metadata);
        Assert.Equal("inv_abc123", result.Metadata!["monobank_invoice_id"]);
    }

    [Fact]
    public async Task ParseWebhookAsync_InvoiceDeclined_ReturnsDeclinedEvent()
    {
        // Arrange
        var payload = """{"type":"invoicedeclined","invoiceId":"inv_abc123","orderId":"ORDER-123","amount":10000,"ccy":980,"status":"declined","createdDate":1699999999}""";

        // Act
        var result = await _adapter.ParseWebhookAsync(payload);

        // Assert
        Assert.Equal("payment.declined", result.EventType);
        Assert.Equal("declined", result.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_InvoiceExpired_ReturnsExpiredEvent()
    {
        // Arrange
        var payload = """{"type":"invoiceexpired","invoiceId":"inv_abc123","orderId":"ORDER-123","amount":10000,"ccy":980,"status":"expired","createdDate":1699999999}""";

        // Act
        var result = await _adapter.ParseWebhookAsync(payload);

        // Assert
        Assert.Equal("payment.expired", result.EventType);
        Assert.Equal("expired", result.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_InvalidJson_ReturnsErrorEvent()
    {
        // Arrange
        var payload = "not valid json";

        // Act
        var result = await _adapter.ParseWebhookAsync(payload);

        // Assert
        Assert.Equal("payment.error", result.EventType);
        Assert.Equal("error", result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal("PARSE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GatewayId_ReturnsMonobank()
    {
        Assert.Equal("monobank", _adapter.GatewayId);
    }

    [Fact]
    public async Task GatewayName_ReturnsMonobank()
    {
        Assert.Equal("Monobank", _adapter.GatewayName);
    }

    // ============================================
    // HELPERS
    // ============================================

    private void SetupHttpResponse(HttpStatusCode statusCode, object content)
    {
        var json = JsonSerializer.Serialize(content, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}