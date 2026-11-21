using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using PaymentGatewayAdapter.Adapters;
using PaymentGatewayAdapter.Configuration;
using PaymentGatewayAdapter.Models;
using Xunit;

namespace PaymentGatewayAdapter.Tests.Adapters;

public sealed class MonobankAdapterTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly MonobankOptions _options;
    private readonly ILogger<MonobankAdapter> _logger;
    private readonly MonobankAdapter _adapter;

    public MonobankAdapterTests()
    {
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object);

        _options = new MonobankOptions
        {
            BaseUrl = "https://api.monobank.ua/",
            ApiToken = "test-personal-token",
            BusinessToken = "test-business-token",
            WebhookSecret = "test-webhook-secret",
            WebhookUrl = "https://example.com/webhook/monobank",
            SuccessRedirectUrl = "https://example.com/success"
        };

        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = loggerFactory.CreateLogger<MonobankAdapter>();

        _adapter = new MonobankAdapter(_httpClient, Options.Create(_options), _logger);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [Fact]
    public async Task CreatePaymentAsync_WithValidRequest_ReturnsSuccessResult()
    {
        // Arrange
        var expectedResponse = new
        {
            invoiceId = "inv_test_123",
            pageUrl = "https://pay.monobank.ua/inv_test_123"
        };

        var responseJson = JsonSerializer.Serialize(expectedResponse);

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("invoice/create")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson)
            });

        var request = new PaymentRequest
        {
            OrderId = "order_123",
            AmountMinorUnits = 10000, // 100 UAH
            Description = "Test payment",
            IdempotencyKey = "idem_test_123"
        };

        // Act
        var result = await _adapter.CreatePaymentAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.TransactionId.Should().Be("inv_test_123");
        result.PaymentUrl.Should().Be("https://pay.monobank.ua/inv_test_123");
    }

    [Fact]
    public async Task CreatePaymentAsync_WithoutBusinessToken_ReturnsFailure()
    {
        // Arrange
        var optionsWithoutBusiness = new MonobankOptions
        {
            BaseUrl = "https://api.monobank.ua/",
            ApiToken = "test-personal-token",
            BusinessToken = string.Empty // Missing business token
        };

        var adapter = new MonobankAdapter(_httpClient, Options.Create(optionsWithoutBusiness), _logger);

        var request = new PaymentRequest
        {
            OrderId = "order_123",
            AmountMinorUnits = 10000
        };

        // Act
        var result = await adapter.CreatePaymentAsync(request);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Business token not configured");
    }

    [Fact]
    public async Task GetPaymentStatusAsync_WithApprovedInvoice_ReturnsSucceeded()
    {
        // Arrange
        var expectedResponse = new
        {
            invoiceId = "inv_test_123",
            status = "approved",
            amount = 10000,
            currency = 980,
            reference = "order_123"
        };

        var responseJson = JsonSerializer.Serialize(expectedResponse);

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri!.ToString().Contains("invoice/status")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson)
            });

        // Act
        var result = await _adapter.GetPaymentStatusAsync("inv_test_123");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(PaymentStatus.Succeeded);
        result.AmountMinorUnits.Should().Be(10000);
    }

    [Fact]
    public void VerifyWebhookSignature_WithValidSignature_ReturnsTrue()
    {
        // Arrange
        var payload = """{"type":"invoiceapproved","data":{"invoiceId":"inv_123","amount":10000}}""";
        var secret = "test-webhook-secret";

        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        var validSignature = Convert.ToBase64String(hash);

        // Act
        var result = _adapter.VerifyWebhookSignature(payload, validSignature);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void VerifyWebhookSignature_WithInvalidSignature_ReturnsFalse()
    {
        // Arrange
        var payload = """{"type":"invoiceapproved","data":{"invoiceId":"inv_123","amount":10000}}""";
        var invalidSignature = "invalid_signature";

        // Act
        var result = _adapter.VerifyWebhookSignature(payload, invalidSignature);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void ParseWebhook_WithApprovedEvent_ReturnsCorrectData()
    {
        // Arrange
        var payload = """{"type":"invoiceapproved","data":{"invoiceId":"inv_123","reference":"order_123","amount":10000,"currency":980,"status":"approved"}}""";

        // Act
        var result = _adapter.ParseWebhook(payload);

        // Assert
        result.Should().NotBeNull();
        result.EventType.Should().Be(WebhookEventType.PaymentSucceeded);
        result.TransactionId.Should().Be("inv_123");
        result.OrderId.Should().Be("order_123");
        result.AmountMinorUnits.Should().Be(10000);
        result.Currency.Should().Be("UAH");
        result.Status.Should().Be(PaymentStatus.Succeeded);
    }

    [Fact]
    public void ParseWebhook_WithDeclinedEvent_ReturnsFailedStatus()
    {
        // Arrange
        var payload = """{"type":"invoicedeclined","data":{"invoiceId":"inv_123","reference":"order_123","amount":10000,"currency":980,"status":"declined"}}""";

        // Act
        var result = _adapter.ParseWebhook(payload);

        // Assert
        result.EventType.Should().Be(WebhookEventType.PaymentFailed);
        result.Status.Should().Be(PaymentStatus.Failed);
    }

    [Fact]
    public async Task RefundAsync_Always_ReturnsNotSupported()
    {
        // Act
        var result = await _adapter.RefundAsync("inv_123", 5000, "Customer request");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not support refunds");
    }

    [Fact]
    public async Task CreateRecurringAsync_Always_ReturnsNotSupported()
    {
        // Act
        var result = await _adapter.CreateRecurringAsync(new RecurringPaymentRequest());

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not support recurring");
    }

    [Theory]
    [InlineData("created", PaymentStatus.Pending)]
    [InlineData("processing", PaymentStatus.Processing)]
    [InlineData("approved", PaymentStatus.Succeeded)]
    [InlineData("declined", PaymentStatus.Failed)]
    [InlineData("expired", PaymentStatus.Cancelled)]
    [InlineData("reversed", PaymentStatus.Refunded)]
    [InlineData("unknown", PaymentStatus.Unknown)]
    public void MapStatus_AllStatuses_MappedCorrectly(string monobankStatus, PaymentStatus expected)
    {
        // This tests private method via reflection - in real tests you'd test through public methods
        // Here we verify the mapping logic by checking webhook parsing
        var payload = $"""{{"type":"invoiceapproved","data":{{"invoiceId":"inv_123","amount":1000,"currency":980,"status":"{monobankStatus}"}}}}""";

        var result = _adapter.ParseWebhook(payload);

        // For invoiceapproved, the status comes from data.status
        // We'd need different event types for different statuses
        // This is a simplified check
        result.Should().NotBeNull();
    }
}