using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SonicRelay.Api.Endpoints;
using SonicRelay.Api.Services;
using SonicRelay.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSonicRelayInfrastructure(builder.Configuration);
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TurnCredentialService>();
builder.Services.Configure<TurnOptions>(builder.Configuration.GetSection("Turn"));
// The deploy .env feeds coturn with flat variable names; accept those as a
// fallback so one .env configures both containers without duplication.
builder.Services.PostConfigure<TurnOptions>(options =>
{
    var configuration = builder.Configuration;
    options.StaticAuthSecret ??= configuration["TURN_STATIC_AUTH_SECRET"];
    if (options.TurnUris.Length == 0 && configuration["TURN_URIS"] is { Length: > 0 } turnUris)
    {
        options.TurnUris = turnUris.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    if (configuration["STUN_URIS"] is { Length: > 0 } stunUris)
    {
        options.StunUris = stunUris.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    if (configuration.GetValue<int?>("TURN_CREDENTIAL_TTL_SECONDS") is { } ttl && ttl > 0)
    {
        options.CredentialTtlSeconds = ttl;
    }
});
builder.Services.AddSingleton<SessionCleanupService>();
builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<SessionCleanupService>());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
        }

        var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SonicRelay.RateLimiting");
        logger.LogWarning("Rate limit rejected request for policy {PolicyName} on path {RequestPath}",
            policy, context.HttpContext.Request.Path);
        return ValueTask.CompletedTask;
    };

    options.AddPolicy("login", context => IpLimit(context, "RateLimits:Login", 5));
    options.AddPolicy("refresh", context => IpLimit(context, "RateLimits:Refresh", 5));
    options.AddPolicy("create-session", context => UserLimit(context, "RateLimits:CreateSession", 10));
    options.AddPolicy("join-session", context => UserLimit(context, "RateLimits:JoinSession", 10));
    options.AddPolicy("rotate-code", context => UserLimit(context, "RateLimits:RotateCode", 5));
});
builder.Services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, options =>
{
    options.BearerTokenExpiration = TimeSpan.FromMinutes(builder.Configuration.GetValue("Auth:AccessTokenMinutes", 15));
    options.RefreshTokenExpiration = TimeSpan.FromDays(builder.Configuration.GetValue("Auth:RefreshTokenDays", 30));
});
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Postgres") ?? string.Empty, name: "postgres")
    .AddRedis(builder.Configuration["Redis:ConnectionString"] ?? string.Empty, name: "redis");
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AuthenticatedUser", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanRegisterDevice", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanCreateSession", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanJoinSession", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanPublishSession", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanViewSession", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});

var app = builder.Build();

if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseWebSockets();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapSessionEndpoints();
app.MapWebRtcEndpoints();
app.MapSignalingWebSocketEndpoint();

app.Run();

RateLimitPartition<string> IpLimit(HttpContext context, string section, int defaultPermitLimit) =>
    CreateLimit(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", section, defaultPermitLimit);

RateLimitPartition<string> UserLimit(HttpContext context, string section, int defaultPermitLimit) =>
    CreateLimit(context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown", section, defaultPermitLimit);

RateLimitPartition<string> CreateLimit(string key, string section, int defaultPermitLimit) =>
    RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = builder.Configuration.GetValue($"{section}:PermitLimit", defaultPermitLimit),
        Window = TimeSpan.FromSeconds(builder.Configuration.GetValue($"{section}:WindowSeconds", 60)),
        QueueLimit = 0,
        AutoReplenishment = true
    });

public partial class Program;
