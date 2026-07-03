using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Application.Abstractions;
using SonicRelay.Infrastructure.Persistence;
using SonicRelay.Infrastructure.Redis;
using SonicRelay.Infrastructure.Signaling;

namespace SonicRelay.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddSonicRelayInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres")));
        services.AddStackExchangeRedisCache(options =>
            options.Configuration = configuration["Redis:ConnectionString"]);
        services.AddSingleton<ISessionCodeStore, RedisSessionCodeStore>();
        services.AddSingleton<IConnectionRegistry, InMemoryConnectionRegistry>();
        return services;
    }
}
