using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using PaymentGatewayAdapter.Abstractions;
using PaymentGatewayAdapter.Adapters;
using PaymentGatewayAdapter.Configuration;

namespace PaymentGatewayAdapter.Extensions;

/// <summary>
/// DI extensions for Monobank adapter registration
/// </summary>
public static class MonobankServiceCollectionExtensions
{
    /// <summary>
    /// Registers Monobank adapter with HttpClient and Polly resilience policies
    /// </summary>
    public static IServiceCollection AddMonobankAdapter(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = MonobankOptions.SectionName)
    {
        // Bind configuration
        services.Configure<MonobankOptions>(configuration.GetSection(sectionName));
        services.AddSingleton<IValidateOptions<MonobankOptions>, MonobankOptionsValidator>();

        // Register HttpClient with resilience
        services.AddHttpClient<MonobankAdapter>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<MonobankOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("User-Agent", "PaymentGatewayAdapter/1.0");
        })
        .AddStandardResilienceHandler(options =>
        {
            // Retry policy
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.Delay = TimeSpan.FromSeconds(1);
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;

            // Circuit breaker
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.MinimumThroughput = 5;
            options.CircuitBreaker.FailureRatio = 0.5;

            // Timeout
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        });

        // Register adapter as IPaymentGateway
        services.AddKeyedScoped<IPaymentGateway, MonobankAdapter>("Monobank");

        return services;
    }

    /// <summary>
    /// Registers Monobank adapter with custom HttpClient configuration
    /// </summary>
    public static IServiceCollection AddMonobankAdapter(
        this IServiceCollection services,
        Action<MonobankOptions> configureOptions,
        Action<HttpClient>? configureHttpClient = null)
    {
        services.Configure(configureOptions);
        services.AddSingleton<IValidateOptions<MonobankOptions>, MonobankOptionsValidator>();

        var httpClientBuilder = services.AddHttpClient<MonobankAdapter>();

        if (configureHttpClient != null)
        {
            httpClientBuilder.ConfigureHttpClient(configureHttpClient);
        }

        httpClientBuilder.AddStandardResilienceHandler();

        services.AddKeyedScoped<IPaymentGateway, MonobankAdapter>("Monobank");

        return services;
    }
}

/// <summary>
/// Validates Monobank options at startup
/// </summary>
internal sealed class MonobankOptionsValidator : IValidateOptions<MonobankOptions>
{
    public ValidateOptionsResult Validate(string? name, MonobankOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            failures.Add($"{nameof(options.BaseUrl)} is required");
        }

        if (string.IsNullOrWhiteSpace(options.ApiToken) && string.IsNullOrWhiteSpace(options.BusinessToken))
        {
            failures.Add("At least one of ApiToken (Personal) or BusinessToken must be configured");
        }

        if (!string.IsNullOrWhiteSpace(options.WebhookUrl) && !Uri.IsWellFormedUriString(options.WebhookUrl, UriKind.Absolute))
        {
            failures.Add($"{nameof(options.WebhookUrl)} must be a valid absolute URL");
        }

        if (!string.IsNullOrWhiteSpace(options.SuccessRedirectUrl) && !Uri.IsWellFormedUriString(options.SuccessRedirectUrl, UriKind.Absolute))
        {
            failures.Add($"{nameof(options.SuccessRedirectUrl)} must be a valid absolute URL");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}