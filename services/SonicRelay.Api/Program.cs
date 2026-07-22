using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Prometheus;
using SonicRelay.Api.Authorization;
using SonicRelay.Api.Endpoints;
using SonicRelay.Api.Services;
using SonicRelay.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var deviceIdentityEnabled = builder.Configuration.GetValue("DeviceIdentity:Enabled", true);

// The signaling receive loop polls session state every second per socket, which
// floods the console with EF `SELECT Status, CodeExpiresAt` command logs. Keep
// SQL at Warning+ so app logs (SonicRelay.*, request diagnostics) stay readable.
// Overridable via Logging:LogLevel configuration if full SQL is ever needed.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSonicRelayInfrastructure(builder.Configuration);
builder.Services.AddSingleton<SonicRelay.Api.Observability.SonicRelayMetrics>();
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
    // The deploy .env template only names the relay's public host; derive the
    // standard turn: URIs (and a matching stun:) from it so TURN works without
    // hand-writing TURN_URIS. A turns:5349 entry is intentionally not derived —
    // it requires a TLS certificate mounted into coturn, so it stays opt-in via
    // an explicit TURN_URIS.
    var turnPublicHost = configuration["TURN_PUBLIC_HOST"];
    if (options.TurnUris.Length == 0 && !string.IsNullOrWhiteSpace(turnPublicHost))
    {
        options.TurnUris =
        [
            $"turn:{turnPublicHost}:3478?transport=udp",
            $"turn:{turnPublicHost}:3478?transport=tcp"
        ];
    }
    if (configuration["STUN_URIS"] is { Length: > 0 } stunUris)
    {
        options.StunUris = stunUris.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    else if (!string.IsNullOrWhiteSpace(turnPublicHost))
    {
        // Prefer our own coturn for STUN over the Google default when a relay
        // host is configured.
        options.StunUris = [$"stun:{turnPublicHost}:3478"];
    }
    if (configuration.GetValue<int?>("TURN_CREDENTIAL_TTL_SECONDS") is { } ttl && ttl > 0)
    {
        options.CredentialTtlSeconds = ttl;
    }
});
builder.Services.AddScoped<AccountDeletionService>();

builder.Services.Configure<DeviceIdentityOptions>(builder.Configuration.GetSection("DeviceIdentity"));
builder.Services.AddSingleton<DeviceCredentialService>();
builder.Services.AddSingleton<PairingChallengeService>();
builder.Services.AddScoped<IAuthorizationHandler, DeviceScopeAuthorizationHandler>();

builder.Services.AddAuthentication().AddJwtBearer("DeviceBearer", jwtOptions =>
{
    // Keep claim types as issued (e.g. "sub", not ClaimTypes.NameIdentifier) so
    // downstream code reading JwtRegisteredClaimNames.Sub/"cv"/"scope" matches
    // what DeviceCredentialService.IssueAccessToken actually put in the token.
    jwtOptions.MapInboundClaims = false;
    var deviceOptions = builder.Configuration.GetSection("DeviceIdentity").Get<DeviceIdentityOptions>()
        ?? new DeviceIdentityOptions();
    jwtOptions.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = deviceOptions.Issuer,
        ValidAudience = deviceOptions.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(deviceOptions.TokenSigningKey ?? string.Empty)),
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});

// Notify n8n (or any webhook) when an account is deleted so an operator gets an email.
// Falls back to a no-op when no webhook is configured (tests, local dev).
var deletionWebhookUrl = builder.Configuration["Notifications:AccountDeletionWebhookUrl"];
if (!string.IsNullOrWhiteSpace(deletionWebhookUrl))
{
    builder.Services.AddHttpClient("account-deletion-webhook");
    builder.Services.AddSingleton<IAccountDeletionNotifier>(sp => new WebhookAccountDeletionNotifier(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("account-deletion-webhook"),
        deletionWebhookUrl,
        sp.GetRequiredService<ILogger<WebhookAccountDeletionNotifier>>()));
}
else
{
    builder.Services.AddSingleton<IAccountDeletionNotifier, NullAccountDeletionNotifier>();
}

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
    options.AddPolicy("create-session", context => IpLimit(context, "RateLimits:CreateSession", 10));
    options.AddPolicy("join-session", context => IpLimit(context, "RateLimits:JoinSession", 10));
    options.AddPolicy("rotate-code", context => IpLimit(context, "RateLimits:RotateCode", 5));
    options.AddPolicy("device-bootstrap", context => IpLimit(context, "RateLimits:DeviceBootstrap", 10));
    options.AddPolicy("device-token", context => IpLimit(context, "RateLimits:DeviceToken", 10));
    options.AddPolicy("pairing-create", context => IpLimit(context, "RateLimits:PairingCreate", 10));
    options.AddPolicy("pairing-complete", context => IpLimit(context, "RateLimits:PairingComplete", 10));
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
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));

    options.AddPolicy("DeviceAuthenticated", policy =>
    {
        policy.AddAuthenticationSchemes("DeviceBearer");
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new DeviceScopeRequirement());
    });

    foreach (var scope in new[]
        { "session:create", "session:join", "session:end", "signaling:connect", "turn:credentials" })
    {
        options.AddPolicy(scope, policy =>
        {
            policy.AddAuthenticationSchemes("DeviceBearer");
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new DeviceScopeRequirement(scope));
        });
    }

    if (deviceIdentityEnabled)
    {
        foreach (var scope in new[] { "device:read", "device:manage", "pairing:create", "pairing:complete", "pairing:revoke" })
        {
            options.AddPolicy(scope, policy =>
            {
                policy.AddAuthenticationSchemes("DeviceBearer");
                policy.RequireAuthenticatedUser();
                policy.Requirements.Add(new DeviceScopeRequirement(scope));
            });
        }
    }
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
// Prometheus scrape endpoint (issue #21). Anonymous so Prometheus can scrape it;
// exposes only aggregate SonicRelay metrics, no session ids or PII.
app.MapMetrics();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapAdminEndpoints();
app.MapDeviceEndpoints();
if (deviceIdentityEnabled)
{
    app.MapDeviceIdentityEndpoints();
    app.MapPairingEndpoints();
}
app.MapSessionEndpoints();
app.MapWebRtcEndpoints();
app.MapSignalingWebSocketEndpoint();

await IdentitySeeder.SeedAsync(app.Services);

app.Run();

RateLimitPartition<string> IpLimit(HttpContext context, string section, int defaultPermitLimit) =>
    CreateLimit(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", section, defaultPermitLimit);

RateLimitPartition<string> CreateLimit(string key, string section, int defaultPermitLimit) =>
    RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = builder.Configuration.GetValue($"{section}:PermitLimit", defaultPermitLimit),
        Window = TimeSpan.FromSeconds(builder.Configuration.GetValue($"{section}:WindowSeconds", 60)),
        QueueLimit = 0,
        AutoReplenishment = true
    });

public partial class Program;
