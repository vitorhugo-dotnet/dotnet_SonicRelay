using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Application.Abstractions;
using SonicRelay.Infrastructure.Persistence;
using SonicRelay.Infrastructure.Redis;
using SonicRelay.Infrastructure.Signaling;
using SonicRelay.Domain.Users;

namespace SonicRelay.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddSonicRelayInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres")));
        services.AddIdentityApiEndpoints<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedAccount = false;
                options.SignIn.RequireConfirmedEmail = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();
        services.AddStackExchangeRedisCache(options =>
            options.Configuration = configuration["Redis:ConnectionString"]);
        services.AddSingleton<ISessionCodeStore, RedisSessionCodeStore>();
        services.AddSingleton<IConnectionRegistry, InMemoryConnectionRegistry>();
        services.AddSingleton<IParticipantReconnectTracker, InMemoryParticipantReconnectTracker>();
        return services;
    }
}
