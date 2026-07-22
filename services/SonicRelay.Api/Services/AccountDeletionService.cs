using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.Users;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Services;

/// <summary>
/// Outcome of an account deletion request.
/// </summary>
public enum AccountDeletionOutcome
{
    Deleted,
    NotFound,
    SelfDeletionForbidden
}

/// <summary>
/// Reason a deletion was requested, used for auditing and notifications.
/// </summary>
public enum AccountDeletionReason
{
    /// <summary>An administrator removed another user.</summary>
    AdminAction,
    /// <summary>The user requested removal of their own account.</summary>
    SelfService
}

/// <summary>
/// Soft-deletes SonicRelay accounts: disables login/refresh, revokes the user's owned
/// devices (the old owner-scoped Device entity), and records an audit trail. Hard deletion
/// is intentionally avoided so the audit trail and foreign-key integrity survive. Does not
/// touch StreamSessions/SessionParticipants: those are owned by DeviceIdentity, which has
/// no relationship to ApplicationUser, so account deletion has nothing session-related left
/// to do since the device-identity migration (issue #26 Phase 2).
/// </summary>
public sealed class AccountDeletionService(
    UserManager<ApplicationUser> userManager,
    AppDbContext db,
    IAccountDeletionNotifier notifier,
    ILogger<AccountDeletionService> logger)
{
    public async Task<AccountDeletionOutcome> DeleteAsync(
        Guid targetUserId,
        Guid requestedByUserId,
        AccountDeletionReason reason,
        string? requestOrigin,
        CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return AccountDeletionOutcome.NotFound;
        }

        // An admin removing themselves via the admin endpoint is refused so an
        // operator cannot accidentally lock the platform's admin out. Self-service
        // deletion goes through a different path and is always allowed.
        if (reason == AccountDeletionReason.AdminAction && targetUserId == requestedByUserId)
        {
            return AccountDeletionOutcome.SelfDeletionForbidden;
        }

        if (!user.IsDisabled)
        {
            user.IsDisabled = true;
        }

        // Block future interactive logins using Identity's native lockout so the
        // stock login/refresh endpoints reject the account without custom middleware.
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        await userManager.UpdateAsync(user);

        // Rotating the security stamp invalidates every issued refresh token.
        await userManager.UpdateSecurityStampAsync(user);

        var devices = await db.Devices
            .Where(x => x.OwnerUserId == targetUserId && !x.Revoked)
            .ToListAsync(ct);
        foreach (var device in devices)
        {
            device.Revoked = true;
        }

        var now = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var revokedDevices = devices.Count;

        // Structured audit record. Deliberately avoids logging email/PII in the
        // clear beyond the identifiers required to trace an administrative action.
        logger.LogInformation(
            "Account deletion: target={TargetUserId} requestedBy={RequestedByUserId} reason={Reason} origin={RequestOrigin} revokedDevices={RevokedDevices}",
            targetUserId, requestedByUserId, reason, requestOrigin ?? "unknown", revokedDevices);

        await notifier.NotifyAsync(new AccountDeletionNotification(
            targetUserId,
            user.Email,
            reason.ToString(),
            requestOrigin,
            now), ct);

        return AccountDeletionOutcome.Deleted;
    }
}
