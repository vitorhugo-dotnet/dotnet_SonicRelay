using Microsoft.Extensions.Caching.Distributed;
using SonicRelay.Application.Abstractions;

namespace SonicRelay.Infrastructure.Redis;

public sealed class RedisSessionCodeStore(IDistributedCache cache) : ISessionCodeStore
{
    public async Task StoreAsync(string codeHash, Guid sessionId, TimeSpan ttl, CancellationToken ct)
    {
        await cache.SetStringAsync($"sr:session-code:{codeHash}", sessionId.ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        }, ct);
    }

    public async Task<Guid?> RedeemAsync(string codeHash, CancellationToken ct)
    {
        var value = await cache.GetStringAsync($"sr:session-code:{codeHash}", ct);
        return Guid.TryParse(value, out var sessionId) ? sessionId : null;
    }
}
