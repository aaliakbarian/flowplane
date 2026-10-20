using System.Security.Claims;

using FlowPlane.Api.Authentication;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Infrastructure;
using FlowPlane.Providers.Ssis;
using FlowPlane.Providers.Ssis.Discovery;
using FlowPlane.Providers.Ssis.Validation;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    options.UseUtcTimestamp = true;
});

builder.Services.AddFlowPlanePostgreSql(builder.Configuration);
builder.Services.AddFlowPlaneAuthentication(builder.Configuration, builder.Environment);
SsisDiscoveryOptions.RejectObsoleteKeys(builder.Configuration
    .GetSection(SsisDiscoveryOptions.SectionName).GetChildren().Select(section => section.Key));
var ssisDiscoveryOptions = builder.Configuration
    .GetSection(SsisDiscoveryOptions.SectionName)
    .Get<SsisDiscoveryOptions>() ?? new SsisDiscoveryOptions();
var inventoryOptions = (builder.Configuration.GetSection(DiscoveryInventoryOptions.SectionName)
    .Get<DiscoveryInventoryOptions>() ?? new DiscoveryInventoryOptions()) with
{
    ScanTimeoutSeconds = ssisDiscoveryOptions.OverallTimeoutSeconds,
};
inventoryOptions.Validate();
builder.Services.AddSingleton(inventoryOptions);
builder.Services.AddFlowPlaneSsisValidation(
    builder.Environment.IsDevelopment(),
    new SsisValidationOptions(
        builder.Configuration.GetValue("FlowPlane:Ssis:Validation:ConnectionTimeoutSeconds", 5),
        builder.Configuration.GetValue("FlowPlane:Ssis:Validation:CommandTimeoutSeconds", 10),
        builder.Configuration.GetValue("FlowPlane:Ssis:Validation:OverallTimeoutSeconds", 30)),
    ssisDiscoveryOptions);
builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
{
    Subject = user.FindFirstValue("sub"),
    Name = user.FindFirstValue("name")
})).RequireAuthorization();

app.Run();

public partial class Program;
