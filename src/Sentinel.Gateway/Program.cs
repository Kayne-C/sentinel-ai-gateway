using Sentinel.Application;
using Sentinel.Guardrails;
using Sentinel.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddGuardrails(builder.Configuration)
    .AddApplication(builder.Configuration)
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();
await app.Services.InitializeDatabaseAsync();
app.MapGet("/health/live", () => Results.Ok());
await app.RunAsync();

/// <summary>Entry point marker for <c>WebApplicationFactory</c>.</summary>
public partial class Program;
