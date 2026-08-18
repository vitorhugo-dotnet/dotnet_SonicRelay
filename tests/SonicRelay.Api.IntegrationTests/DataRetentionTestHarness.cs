using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Observability;
using SonicRelay.Api.Services;
using SonicRelay.Application.Abstractions;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>Session-code store double: records removals and can be made to fail on demand.</summary>
internal sealed class FakeSessionCodeStore : ISessionCodeStore
{
    private readonly ConcurrentDictionary<string, Guid> _codes = new();

    public List<Guid> Removed { get; } = [];

    /// <summary>Simulates a Redis outage during the session sweep.</summary>
    public bool FailOnRemove { get; set; }

    public Task StoreAsync(string codeHash, Guid sessionId, TimeSpan ttl, CancellationToken ct)
    {
        _codes[codeHash] = sessionId;
        return Task.CompletedTask;
    }

    public Task<Guid?> RedeemAsync(string codeHash, CancellationToken ct) =>
        Task.FromResult(_codes.TryGetValue(codeHash, out var id) ? id : (Guid?)null);

    public Task RemoveAsync(Guid sessionId, CancellationToken ct)
    {
        if (FailOnRemove) throw new InvalidOperationException("session code store unavailable");
        Removed.Add(sessionId);
        foreach (var entry in _codes.Where(x => x.Value == sessionId).ToList())
        {
            _codes.TryRemove(entry.Key, out _);
        }
        return Task.CompletedTask;
    }
}

/// <summary>Captures everything the retention code logs so tests can audit it for identifiers.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public ConcurrentBag<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(ConcurrentBag<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
    }
}

/// <summary>
/// Hosts <see cref="DataRetentionService"/> over an in-memory database with a controllable clock,
/// so retention boundaries can be asserted at day granularity without any real waiting.
/// </summary>
internal sealed class DataRetentionTestHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private DataRetentionTestHarness(ServiceProvider provider, DataRetentionOptions options)
    {
        _provider = provider;
        Options = options;
        Clock = provider.GetRequiredService<TestTimeProvider>();
        CodeStore = (FakeSessionCodeStore)provider.GetRequiredService<ISessionCodeStore>();
        Service = provider.GetRequiredService<DataRetentionService>();
        State = provider.GetRequiredService<DataRetentionState>();
        Logs = provider.GetRequiredService<RecordingLoggerProvider>();
    }

    public DataRetentionOptions Options { get; }
    public TestTimeProvider Clock { get; }
    public FakeSessionCodeStore CodeStore { get; }
    public DataRetentionService Service { get; }
    public DataRetentionState State { get; }
    public RecordingLoggerProvider Logs { get; }

    public DateTimeOffset Now => Clock.GetUtcNow();

    /// <summary>A collection timestamp exactly <paramref name="days"/> old.</summary>
    public DateTimeOffset DaysAgo(double days) => Now - TimeSpan.FromDays(days);

    /// <summary>
    /// A collection timestamp <paramref name="daysPastCutoff"/> days beyond the policy's deletion
    /// cutoff — negative for a row that is still inside the window. Expressing fixtures this way
    /// keeps the boundary tests meaningful if the configured ceiling ever moves.
    /// </summary>
    public DateTimeOffset CutoffOffset(double daysPastCutoff) =>
        Now - Options.EffectiveRetention - TimeSpan.FromDays(daysPastCutoff);

    public static DataRetentionTestHarness Create(Action<DataRetentionOptions>? configure = null)
    {
        var options = new DataRetentionOptions();
        configure?.Invoke(options);

        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        var databaseName = $"retention-tests-{Guid.NewGuid()}";
        services.AddDbContext<AppDbContext>(builder => builder.UseInMemoryDatabase(databaseName));
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });
        services.AddSingleton(logs);
        services.AddSingleton(new TestTimeProvider());
        services.AddSingleton<TimeProvider>(provider => provider.GetRequiredService<TestTimeProvider>());
        services.AddSingleton<ISessionCodeStore>(new FakeSessionCodeStore());
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton<DataRetentionMetrics>();
        services.AddSingleton<DataRetentionState>();
        services.AddSingleton<DataRetentionService>();

        return new DataRetentionTestHarness(services.BuildServiceProvider(), options);
    }

    public async Task SeedAsync(Action<AppDbContext> seed)
    {
        using var scope = _provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    public async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = _provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task<DataRetentionReport> RunAsync() => Service.CleanupOnceAsync(CancellationToken.None);

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
