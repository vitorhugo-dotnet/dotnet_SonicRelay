namespace SonicRelay.Application.Abstractions;

public interface ISessionCodeStore
{
    Task StoreAsync(string codeHash, Guid sessionId, TimeSpan ttl, CancellationToken ct);
    Task<Guid?> RedeemAsync(string codeHash, CancellationToken ct);
}
