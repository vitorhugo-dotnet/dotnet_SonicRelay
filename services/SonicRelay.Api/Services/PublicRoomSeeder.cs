using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.Devices;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

/// <summary>
/// Idempotently ensures the public radio's virtual publisher device, its
/// well-known public session, and the publisher's own SessionParticipant row all
/// exist. Safe to call on every startup and does not throw when the rows are
/// already there (re-activates a revoked device rather than erroring).
/// </summary>
public sealed class PublicRoomSeeder
{
    public static readonly Guid VirtualPublisherDeviceId = new("0b1e5a7a-0000-4000-8000-000000000001");
    public static readonly Guid PublicSessionId = new("0b1e5a7a-0000-4000-8000-000000000002");

    public async Task<StreamSession> EnsureSeededAsync(AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        var device = await db.DeviceIdentities.SingleOrDefaultAsync(x => x.Id == VirtualPublisherDeviceId, ct);
        if (device is null)
        {
            device = new DeviceIdentity
            {
                Id = VirtualPublisherDeviceId,
                Name = "Public Radio (virtual publisher)",
                DeviceType = DeviceTypes.WindowsPublisher,
                Platform = DevicePlatforms.Windows,
                // Never used to obtain a token via /api/devices/token: PublicRoomPublisherService
                // mints the JWT directly via DeviceCredentialService.IssueAccessToken. The hash
                // still has to be non-empty to satisfy the column, so it is random and unused.
                CredentialSecretHash = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                CredentialVersion = 1,
                Status = DeviceIdentityStatuses.Active,
                CreatedAt = now
            };
            db.DeviceIdentities.Add(device);
        }
        else if (device.Status != DeviceIdentityStatuses.Active)
        {
            device.Status = DeviceIdentityStatuses.Active;
            device.RevokedAt = null;
        }

        var session = await db.StreamSessions.SingleOrDefaultAsync(x => x.Id == PublicSessionId, ct);
        if (session is null)
        {
            session = new StreamSession
            {
                Id = PublicSessionId,
                SourceDeviceId = VirtualPublisherDeviceId,
                Status = SessionStatuses.Active,
                MaxViewers = 20, // caller (PublicRoomPublisherService) overwrites from PublicRoomOptions
                CodeExpiresAt = DateTimeOffset.MaxValue, // never used: joins go through JoinByIdAsync, not the code path
                StartedAt = now,
                CreatedAt = now
            };
            db.StreamSessions.Add(session);
        }

        var publisherParticipant = await db.SessionParticipants.SingleOrDefaultAsync(x =>
            x.SessionId == PublicSessionId && x.DeviceId == VirtualPublisherDeviceId
            && x.Role == ParticipantRoles.Publisher, ct);
        if (publisherParticipant is null)
        {
            db.SessionParticipants.Add(new SessionParticipant
            {
                Id = Guid.NewGuid(),
                SessionId = PublicSessionId,
                DeviceId = VirtualPublisherDeviceId,
                Role = ParticipantRoles.Publisher,
                Status = ParticipantStatuses.Connected,
                JoinedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<(string AccessToken, DateTimeOffset ExpiresAt)> IssuePublisherTokenAsync(
        AppDbContext db, DeviceCredentialService credentials, TimeProvider time, CancellationToken ct)
    {
        var device = await db.DeviceIdentities.SingleAsync(x => x.Id == VirtualPublisherDeviceId, ct);
        return credentials.IssueAccessToken(device);
    }
}
