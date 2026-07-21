using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SonicRelayApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"sonicrelay-tests-{Guid.NewGuid()}";
    private readonly IReadOnlyDictionary<string, string?> _settings;

    public SonicRelayApiFactory() : this(new Dictionary<string, string?>())
    {
    }

    internal SonicRelayApiFactory(IReadOnlyDictionary<string, string?> settings) => _settings = settings;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=sonicrelay_tests;Username=test;Password=test");
        builder.UseSetting("Redis:ConnectionString", "localhost:6379");
        builder.UseSetting("Sessions:CodeTtlMinutes", "10");
        builder.UseSetting("Sessions:CodeHmacKey", "integration-test-session-code-key");
        builder.UseSetting("Sessions:CleanupEnabled", "false");
        builder.UseSetting("RateLimits:Login:PermitLimit", "100");
        builder.UseSetting("RateLimits:Refresh:PermitLimit", "100");
        builder.UseSetting("RateLimits:CreateSession:PermitLimit", "100");
        builder.UseSetting("RateLimits:JoinSession:PermitLimit", "100");
        builder.UseSetting("RateLimits:RotateCode:PermitLimit", "100");
        builder.UseSetting("DeviceIdentity:CredentialHmacKey", "integration-test-device-credential-key");
        builder.UseSetting("DeviceIdentity:PairingCodeHmacKey", "integration-test-pairing-code-key");
        builder.UseSetting("DeviceIdentity:TokenSigningKey", "integration-test-device-token-signing-key-32bytes-min");
        builder.UseSetting("RateLimits:DeviceBootstrap:PermitLimit", "100");
        builder.UseSetting("RateLimits:DeviceToken:PermitLimit", "100");
        foreach (var setting in _settings)
        {
            builder.UseSetting(setting.Key, setting.Value);
        }
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
            services.RemoveAll<IDistributedCache>();
            services.AddDistributedMemoryCache();
        });
    }
}
