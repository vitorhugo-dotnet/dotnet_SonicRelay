namespace SonicRelay.Application.Abstractions;

/// <summary>
/// Tracks per-participant reconnect grace periods so a transient signaling drop does not
/// immediately finalize a participant as "left". <see cref="BeginGracePeriod"/> schedules
/// <paramref name="onExpiredAsync"/> to run once <paramref name="graceDuration"/> elapses,
/// unless <see cref="TryCancelGracePeriod"/> is called first because the participant
/// reconnected.
/// </summary>
public interface IParticipantReconnectTracker
{
    void BeginGracePeriod(Guid participantId, TimeSpan graceDuration, Func<Task> onExpiredAsync);

    /// <summary>Cancels a pending grace period. Returns true if one was pending and cancelled.</summary>
    bool TryCancelGracePeriod(Guid participantId);
}
