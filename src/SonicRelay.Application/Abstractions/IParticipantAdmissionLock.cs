namespace SonicRelay.Application.Abstractions;

/// <summary>
/// Serializes participant admission per <c>(session, device)</c> pair so two joins racing each
/// other cannot both observe "no participant yet" and both insert one.
/// </summary>
/// <remarks>
/// This is the recovery path's guard, not a general concurrency utility. A device coming back
/// from a network loss routinely fires more than one join at once — the automatic recovery and
/// a manual retry, or two attempts either side of an interface handover — and each of them
/// takes the read-then-insert path in <c>SessionEndpoints.AdmitViewerAsync</c>. The unique
/// index on <c>(SessionId, DeviceId, Role)</c> is the authoritative backstop across API
/// instances; this lock is what keeps the common single-instance case from having to recover
/// from a constraint violation on every rejoin.
/// </remarks>
public interface IParticipantAdmissionLock
{
    /// <summary>
    /// Waits for exclusive admission rights to <paramref name="sessionId"/>/<paramref name="deviceId"/>.
    /// Dispose the returned handle to release them.
    /// </summary>
    Task<IDisposable> AcquireAsync(Guid sessionId, Guid deviceId, CancellationToken cancellationToken);
}
