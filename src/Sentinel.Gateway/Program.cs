using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Sentinel.Application;
using Sentinel.Application.Diagnostics;
using Sentinel.Gateway.Demo;
using Sentinel.Gateway.Endpoints;
using Sentinel.Gateway.Http;
using Sentinel.Gateway.Proxy;
using Sentinel.Gateway.Security;
using Sentinel.Guardrails;
using Sentinel.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = ApiEndpoints.DocumentBodyLimit + 1024; // per-endpoint limits are stricter
});

builder.Services
    .AddGuardrails(builder.Configuration)
    .AddApplication(builder.Configuration)
    .AddInfrastructure(builder.Configuration)
    .AddGatewayAuthentication(builder.Configuration)
    .AddGatewayRateLimiting(builder.Configuration);

builder.Services.AddOptions<OpenAiProxyOptions>().Bind(builder.Configuration.GetSection(OpenAiProxyOptions.Section));
builder.Services.AddHttpForwarder();
builder.Services.TryAddSingleton<IUpstreamInvoker, SocketsUpstreamInvoker>();
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GatewayExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddInfrastructureHealthChecks();

// Loaded on first use, so a production deployment never reads (or even ships) the demo corpus.
builder.Services.AddSingleton(sp => DemoCorpus.Load(sp.GetRequiredService<IConfiguration>()["Demo:CorpusPath"] ?? DemoCorpus.DefaultPath));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "sentinel-gateway"))
    .WithTracing(tracing => tracing
        .AddSource(SentinelTelemetry.SourceName)
        .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddMeter(SentinelTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation());

if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

var app = builder.Build();
var authMode = app.Services.GetRequiredService<IOptions<GatewayAuthOptions>>().Value.Mode;

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    // Answers contain tenant data: never cached by browsers or intermediaries, never sniffed, never framed.
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api") || path.StartsWithSegments("/v1") || path.StartsWithSegments("/dev"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    }

    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().DisableRateLimiting();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous().DisableRateLimiting();

app.MapApiEndpoints();
app.MapOpenAiProxy();

if (authMode == AuthMode.Development)
{
    app.MapDemoEndpoints();
}

if (app.Configuration.GetValue("Gateway:ExposeApiDocs", app.Environment.IsDevelopment()))
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

await app.Services.InitializeDatabaseAsync();
if (authMode == AuthMode.Development && app.Configuration.GetValue("Demo:SeedOnStartup", false))
{
    await DemoSeeder.SeedAsync(app.Services, app.Services.GetRequiredService<DemoCorpus>(), app.Logger, app.Lifetime.ApplicationStopping);
}

await app.RunAsync();

/// <summary>Entry point marker for <c>WebApplicationFactory</c>.</summary>
public partial class Program;
