namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// A controllable UTC clock. Retention deadlines are measured in months, so the tests move this
/// forward instead of touching <c>DateTime.UtcNow</c> — the production code reads the clock only
/// through <see cref="TimeProvider"/> for exactly this reason.
/// </summary>
public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    /// <summary>Anchored to the real clock so JWT lifetimes issued under it stay valid.</summary>
    public TestTimeProvider() : this(DateTimeOffset.UtcNow)
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
