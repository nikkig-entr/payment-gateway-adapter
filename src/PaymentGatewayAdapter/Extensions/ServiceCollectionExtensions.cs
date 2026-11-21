using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using PaymentGatewayAdapter.Adapters;
using PaymentGatewayAdapter.Interfaces;
using PaymentGatewayAdapter.Models;
using Polly;
using Polly.Extensions.Http;

namespace PaymentGatewayAdapter.Extensions;

/// <summary>
/// DI extensions for registering payment gateway adapters with resilience policies
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Monobank adapter with full resilience (retry, circuit breaker, timeout)
    /// </summary>
    public static IServiceCollection AddMonobankAdapter(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "Monobank")
    {
        services.Configure<MonobankOptions>(configuration.GetSection(sectionName));

        services.AddHttpClient<MonobankAdapter>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<MonobankOptions>>().Value;
            client.BaseAddress = options.HasBusinessApi
                ? new Uri(options.BusinessBaseUrl)
                : new Uri(options.PersonalBaseUrl);
            client.Timeout = options.Timeout;
            client.DefaultRequestHeaders.Add("User-Agent", "PaymentGatewayAdapter/1.0");
        })
        .AddPolicyHandler(GetRetryPolicy())
        .AddPolicyHandler(GetCircuitBreakerPolicy())
        .AddPolicyHandler(GetTimeoutPolicy());

        services.AddScoped<IPaymentGateway>(sp => sp.GetRequiredService<MonobankAdapter>());

        return services;
    }

    /// <summary>
    /// Registers all available payment gateway adapters
    /// </summary>
    public static IServiceCollection AddPaymentGateways(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Monobank
        if (configuration.GetSection("Monobank").Exists())
        {
            services.AddMonobankAdapter(configuration, "Monobank");
        }

        // LiqPay (placeholder for future implementation)
        // if (configuration.GetSection("LiqPay").Exists())
        // {
        //     services.AddLiqPayAdapter(configuration, "LiqPay");
        // }

        // Stripe (placeholder)
        // if (configuration.GetSection("Stripe").Exists())
        // {
        //     services.AddStripeAdapter(configuration, "Stripe");
        // }

        // Register gateway factory for dynamic selection
        services.AddScoped<IPaymentGatewayFactory, PaymentGatewayFactory>();

        return services;
    }

    /// <summary>
    /// Adds Polly resilience policies for HTTP clients
    /// </summary>
    public static IServiceCollection AddPaymentGatewayResilience(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "PaymentGatewayResilience")
    {
        services.Configure<ResilienceOptions>(configuration.GetSection(sectionName));
        return services;
    }

    // ============================================
    // POLLY POLICIES
    // ============================================

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)),
                onRetry: (outcome, timespan, attempt, context) =>
                {
                    var logger = context.GetLogger<MonobankAdapter>();
                    logger?.LogWarning("Monobank retry {Attempt} after {Delay}ms: {Reason}",
                        attempt, timespan.TotalMilliseconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                });
    }

    private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(60),
                onBreak: (outcome, timespan) =>
                {
                    Console.WriteLine($"[Monobank] Circuit breaker OPEN for {timespan.TotalSeconds}s: " +
                        $"{outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString()}");
                },
                onReset: () => Console.WriteLine("[Monobank] Circuit breaker CLOSED"),
                onHalfOpen: () => Console.WriteLine("[Monobank] Circuit breaker HALF-OPEN"));
    }

    private static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy()
    {
        return Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(30));
    }
}

/// <summary>
/// Factory for selecting payment gateway at runtime
/// </summary>
public interface IPaymentGatewayFactory
{
    IPaymentGateway GetGateway(string gatewayId);
    IEnumerable<IPaymentGateway> GetAllGateways();
}

/// <summary>
/// Default implementation of payment gateway factory
/// </summary>
public sealed class PaymentGatewayFactory : IPaymentGatewayFactory
{
    private readonly IEnumerable<IPaymentGateway> _gateways;
    private readonly Dictionary<string, IPaymentGateway> _gatewayMap;

    public PaymentGatewayFactory(IEnumerable<IPaymentGateway> gateways)
    {
        _gateways = gateways;
        _gatewayMap = gateways.ToDictionary(g => g.GatewayId, g => g, StringComparer.OrdinalIgnoreCase);
    }

    public IPaymentGateway GetGateway(string gatewayId)
    {
        if (_gatewayMap.TryGetValue(gatewayId, out var gateway))
        {
            return gateway;
        }
        throw new InvalidOperationException($"Payment gateway '{gatewayId}' not registered. Available: {string.Join(", ", _gatewayMap.Keys)}");
    }

    public IEnumerable<IPaymentGateway> GetAllGateways() => _gateways;
}

/// <summary>
/// Extension to get logger from Polly context
/// </summary>
internal static class PollyContextExtensions
{
    public static ILogger<T>? GetLogger<T>(this Context context)
    {
        if (context.TryGetValue("logger", out var obj) && obj is ILogger<T> logger)
        {
            return logger;
        }
        return null;
    }
}