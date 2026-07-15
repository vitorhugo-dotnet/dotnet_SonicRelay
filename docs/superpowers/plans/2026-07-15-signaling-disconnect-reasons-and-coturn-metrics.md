# Signaling Disconnect Reasons and Coturn Prometheus Metrics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the server side of a connection loss a concrete "why" — classify and record the reason a signaling WebSocket closed abnormally — and enable coturn's own Prometheus metrics, which are off in both places coturn is defined in this repo today.

**Architecture:** `SignalingWebSocketEndpoint.HandleAsync`'s `try/finally` around `ReceiveLoopAsync` gains a `catch` that classifies the exception into a fixed reason and records it through a new `SonicRelayMetrics.RecordDisconnectReason(string reason)` counter, following the exact pattern the existing `RecordMessage`/`RecordError` counters already use. Separately, coturn's `--prometheus`/`prometheus` option is enabled in both of this repo's coturn definitions, scraped by a new Prometheus job, and surfaced in the existing Grafana dashboard.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, `prometheus-net`, xUnit (integration tests via `WebApplicationFactory`), coturn, Prometheus, Grafana.

## Global Constraints

- Disconnect reasons are a fixed, bounded set of strings — never raw exception text — matching the existing "no PII/unbounded cardinality in metric labels" rule already documented on `SonicRelayMetrics`.
- No behavior change to reconnect/grace-period logic (already implemented) — this only adds observability around the existing exit paths.
- Verified empirically in this sandbox (see Task 2) that an abrupt client-side WebSocket drop surfaces server-side as a plain `System.IO.IOException` ("The remote end closed the connection"), not `WebSocketException` — the classification must handle this correctly rather than assuming all abnormal closes are `WebSocketException`.
- Coturn's exact Prometheus metric names could not be verified in this sandbox (no Docker daemon, no network access to pull the image) — Task 3 includes an explicit verification sub-step against a real running coturn instance before the Grafana panel's queries are finalized; do not hardcode unverified metric names as fact.

---

### Task 1: `SonicRelayMetrics.RecordDisconnectReason`

**Files:**
- Modify: `services/SonicRelay.Api/Observability/SonicRelayMetrics.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/WebRtcObservabilityTests.cs`

**Interfaces:**
- Produces: `SonicRelayMetrics.RecordDisconnectReason(string reason): void`, exposing the Prometheus counter `sonicrelay_signaling_disconnect_reason_total{reason}`. Consumed by `SignalingWebSocketEndpoint` in Task 2.

- [ ] **Step 1: Write the failing test**

Append to `tests/SonicRelay.Api.IntegrationTests/WebRtcObservabilityTests.cs` (inside the class, e.g. after `Metrics_endpoint_is_anonymous_and_exposes_sonicrelay_series`):

```csharp
    [Fact]
    public async Task Metrics_endpoint_exposes_the_disconnect_reason_series_after_recording_one()
    {
        using var scope = _factory.Services.CreateScope();
        var metrics = scope.ServiceProvider.GetRequiredService<SonicRelay.Api.Observability.SonicRelayMetrics>();

        metrics.RecordDisconnectReason("transport_error");

        var client = _factory.CreateClient();
        var body = await client.GetStringAsync("/metrics");
        Assert.Contains("sonicrelay_signaling_disconnect_reason_total{reason=\"transport_error\"}", body);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter Metrics_endpoint_exposes_the_disconnect_reason_series_after_recording_one`
Expected: FAIL to compile — `SonicRelayMetrics` has no `RecordDisconnectReason`.

- [ ] **Step 3: Add the counter and method**

In `services/SonicRelay.Api/Observability/SonicRelayMetrics.cs`, add the counter next to `_signalingErrors`:

```csharp
    private readonly Counter _disconnectReasons = Metrics.CreateCounter(
        "sonicrelay_signaling_disconnect_reason_total",
        "Signaling WebSocket disconnects, by classified reason.",
        new CounterConfiguration { LabelNames = ["reason"] });
```

Add the method next to `RecordError`:

```csharp
    public void RecordDisconnectReason(string reason) => _disconnectReasons.WithLabels(Bounded(reason)).Inc();
```

(`Bounded` already exists in this class and guards against unexpected free-text values, matching `RecordMessage`/`RecordError`; the caller in Task 2 only ever passes one of five fixed strings, so this is a defense-in-depth reuse, not new behavior.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter Metrics_endpoint_exposes_the_disconnect_reason_series_after_recording_one`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add services/SonicRelay.Api/Observability/SonicRelayMetrics.cs tests/SonicRelay.Api.IntegrationTests/WebRtcObservabilityTests.cs
git commit -m "Add sonicrelay_signaling_disconnect_reason_total metric"
```

---

### Task 2: Classify and log abnormal signaling disconnects

**Files:**
- Modify: `services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs`
- Test: `tests/SonicRelay.Api.IntegrationTests/SignalingWebSocketTests.cs`

**Interfaces:**
- Consumes: `SonicRelayMetrics.RecordDisconnectReason` (Task 1).
- Produces: no new public interface — this task adds a `catch` around the existing `try` in `HandleAsync` and a `logger.LogWarning` call.

- [ ] **Step 1: Write the failing test**

Append to `tests/SonicRelay.Api.IntegrationTests/SignalingWebSocketTests.cs` (inside the class, e.g. after `Signaling_reconnecting_within_the_grace_period_reports_reconnection_not_departure`):

```csharp
    [Fact]
    public async Task Signaling_records_a_transport_error_reason_when_a_participant_drops_abruptly()
    {
        var publisher = await CreateParticipantAsync("disconnect-reason-publisher");
        using var publisherSocket = await ConnectAsync(publisher);
        await ReceiveAsync(publisherSocket);

        // An abrupt client-side Dispose (no close handshake) is the same trigger the
        // existing grace-period tests use to simulate a dropped connection; verified in
        // this sandbox that it surfaces server-side as a plain IOException, not
        // WebSocketException — see this plan's Global Constraints.
        publisherSocket.Dispose();

        var client = _factory.CreateClient();
        string metricsBody;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            metricsBody = await client.GetStringAsync("/metrics");
            if (metricsBody.Contains("sonicrelay_signaling_disconnect_reason_total{reason=\"transport_error\"}"))
                break;
            await Task.Delay(50, timeout.Token);
        } while (!timeout.IsCancellationRequested);

        Assert.Contains("sonicrelay_signaling_disconnect_reason_total{reason=\"transport_error\"}", metricsBody);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests --filter Signaling_records_a_transport_error_reason_when_a_participant_drops_abruptly`
Expected: FAIL — the metric is never recorded today (no classification exists yet).

- [ ] **Step 3: Add the classification catch**

In `services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs`, add a private helper near `IsTransient`-style helpers (or just above `HandleAsync`):

```csharp
    private enum SignalingDisconnectReason { NormalClosure, Cancelled, ProtocolError, TransportError, Unknown }

    private static SignalingDisconnectReason ClassifyDisconnect(Exception exception) => exception switch
    {
        OperationCanceledException => SignalingDisconnectReason.Cancelled,
        WebSocketException { WebSocketErrorCode: WebSocketError.InvalidMessageType or WebSocketError.HeaderError } =>
            SignalingDisconnectReason.ProtocolError,
        WebSocketException or IOException => SignalingDisconnectReason.TransportError,
        _ => SignalingDisconnectReason.Unknown,
    };

    private static string ToMetricReason(SignalingDisconnectReason reason) => reason switch
    {
        SignalingDisconnectReason.NormalClosure => "normal_closure",
        SignalingDisconnectReason.Cancelled => "cancelled",
        SignalingDisconnectReason.ProtocolError => "protocol_error",
        SignalingDisconnectReason.TransportError => "transport_error",
        _ => "unknown",
    };
```

Wrap the existing `try`/`finally` in `HandleAsync` with a `catch` that records the reason before rethrowing (rethrowing preserves the existing behavior — the `finally` block's cleanup and the ASP.NET Core pipeline's own handling of an unhandled exception from a Minimal API delegate are unchanged):

```csharp
        try
        {
            await SendEnvelopeAsync(SendFrameAsync, "session.joined", sessionId, null, participant.Id,
                new { participantId = participant.Id, role = participant.Role }, context.RequestAborted);
            var peerAnnouncementType = isGracePeriodReconnect ? "participant.reconnected" : "session.joined";
            await BroadcastAsync(registry, sessionId, participant.Id, peerAnnouncementType, participant.Id,
                new { participantId = participant.Id, role = participant.Role }, context.RequestAborted);
            await ReceiveLoopAsync(socket, SendFrameAsync, sessionId, participant.Id, db, registry, logger, metrics,
                context.RequestAborted);
            metrics.RecordDisconnectReason(ToMetricReason(SignalingDisconnectReason.NormalClosure));
        }
        catch (Exception exception)
        {
            var reason = ClassifyDisconnect(exception);
            logger.LogWarning(
                "Signaling connection closed abnormally for participant {ParticipantId} in session {SessionId}: {Reason}",
                participant.Id, sessionId, reason);
            metrics.RecordDisconnectReason(ToMetricReason(reason));
            throw;
        }
        finally
        {
```

Note: `ReceiveLoopAsync` already returns normally (no exception) both when the client sends a
proper Close frame and when `session.ended` is dispatched — both are legitimate non-abnormal
exits, which is why `NormalClosure` is recorded on the success path right after the `await
ReceiveLoopAsync(...)` line rather than being one of the `catch` classifications.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Api.IntegrationTests`
Expected: PASS — the new test plus all 16 pre-existing tests in `SignalingWebSocketTests` and all in `WebRtcObservabilityTests` (verified as a clean baseline before this change: `16` passed in `SignalingWebSocketTests` alone). Pay particular attention to any test that expects a *specific* exception to propagate out of `HandleAsync` uncaught (none currently do — the `catch` here rethrows unconditionally, so behavior for callers/tests is unchanged).

- [ ] **Step 5: Commit**

```bash
git add services/SonicRelay.Api/Endpoints/SignalingWebSocketEndpoint.cs tests/SonicRelay.Api.IntegrationTests/SignalingWebSocketTests.cs
git commit -m "Classify and record the reason signaling WebSockets disconnect abnormally"
```

---

### Task 3: Coturn Prometheus metrics — both compose paths, scrape job, dashboard panel

**Files:**
- Modify: `deploy/docker-compose.prodcoturn.yml`
- Modify: `infra/coturn/turnserver.conf`
- Modify: `infra/compose.dev.yml`
- Modify: `infra/compose.prod.yml`
- Modify: `observability/prometheus/sonicrelay-scrape.yml`
- Modify: `observability/grafana/sonicrelay-webrtc-turn-dashboard.json`
- Modify: `docs/observability.md`

**Interfaces:** none (infrastructure/config only — no application code).

- [ ] **Step 1: Enable `--prometheus` in the production coturn compose**

In `deploy/docker-compose.prodcoturn.yml`, add `--prometheus` to the `command:` list (after `--simple-log`, the last existing entry):

```yaml
      - --log-file=stdout
      - --simple-log
      - --prometheus
```

Add a line to the file's existing top-of-file comment block (after the `TLS:` paragraph) documenting the new port:

```
# PROMETHEUS: --prometheus exposes coturn's native metrics on port 9641 (no
# ports: mapping needed — network_mode: host already makes it reachable).
# Keep 9641 closed on the host firewall to everything except the Prometheus
# scrape source; it is not meant to be public.
```

- [ ] **Step 2: Enable `prometheus` in the file-based dev/prod-profile coturn config**

In `infra/coturn/turnserver.conf`, add a `prometheus` line after `no-cli` (this file takes no
value for it, so it is unaffected by this file's existing `${VAR}` substitution limitation
noted in its own header comment):

```
no-cli
prometheus
```

In `infra/compose.dev.yml`, add the port to the existing coturn `ports:` list:

```yaml
  coturn:
    profiles: ["dev"]
    ports:
      - "3478:3478/udp"
      - "3478:3478/tcp"
      - "5349:5349/tcp"
      - "49160-49200:49160-49200/udp"
      - "9641:9641"
```

In `infra/compose.prod.yml`, add the same line to its coturn `ports:` list:

```yaml
  coturn:
    profiles: ["prod"]
    ports:
      - "3478:3478/udp"
      - "3478:3478/tcp"
      - "5349:5349/tcp"
      - "49160-49200:49160-49200/udp"
      - "9641:9641"
```

- [ ] **Step 3: Verify against a real coturn instance and add the scrape job**

This sandbox has no Docker daemon and no network access to pull `coturn/coturn`, so the
exact metric names coturn's `--prometheus` exposes could not be verified here. Before
writing the Grafana panel in Step 4, run this verification where Docker is available:

```bash
docker compose --env-file infra/.env -f infra/compose.yml -f infra/compose.dev.yml up -d coturn
curl -s http://localhost:9641/metrics | head -50
```

Confirm the output is Prometheus text format with a `turn_`-prefixed metric family (coturn's
convention). Use the exact family names from that output — not names guessed here — when
writing Step 4's panel queries.

Add a `sonicrelay-coturn` job to `observability/prometheus/sonicrelay-scrape.yml`:

```yaml
# Prometheus scrape snippet for coturn's native metrics (connection-loss-logging work).
# Add this job alongside the sonicrelay-api job above.
- job_name: sonicrelay-coturn
  metrics_path: /metrics
  static_configs:
    - targets:
        # Compose service name for local/dev scraping; use the production host:9641 in prod.
        - coturn:9641
      labels:
        service: sonicrelay-coturn
```

- [ ] **Step 4: Add the Grafana panel using the verified metric names**

Add a new row to `observability/grafana/sonicrelay-webrtc-turn-dashboard.json`'s `panels`
array, after the existing "Recent logs (api + coturn)" panel (which ends at `y: 36`, height
`10`, so the new row starts at `y: 46`). Replace `<verified_metric_name>` placeholders below
with the exact family names found in Step 3 — do not leave them as literal placeholder text
in the committed file:

```json
    {
      "type": "timeseries",
      "title": "Coturn: active allocations",
      "gridPos": { "h": 8, "w": 8, "x": 0, "y": 46 },
      "datasource": { "type": "prometheus", "uid": "${prometheus}" },
      "targets": [{ "expr": "<verified_metric_name_for_allocations>", "legendFormat": "allocations", "refId": "A" }]
    },
    {
      "type": "timeseries",
      "title": "Coturn: relayed traffic (bytes/s)",
      "gridPos": { "h": 8, "w": 8, "x": 8, "y": 46 },
      "datasource": { "type": "prometheus", "uid": "${prometheus}" },
      "targets": [
        { "expr": "rate(<verified_metric_name_for_received_bytes>[5m])", "legendFormat": "rx", "refId": "A" },
        { "expr": "rate(<verified_metric_name_for_sent_bytes>[5m])", "legendFormat": "tx", "refId": "B" }
      ]
    },
    {
      "type": "stat",
      "title": "Coturn: total sessions",
      "gridPos": { "h": 8, "w": 8, "x": 16, "y": 46 },
      "datasource": { "type": "prometheus", "uid": "${prometheus}" },
      "targets": [{ "expr": "<verified_metric_name_for_total_sessions>", "refId": "A" }]
    },
    {
      "type": "timeseries",
      "title": "Signaling disconnect reason/s",
      "gridPos": { "h": 8, "w": 24, "x": 0, "y": 54 },
      "datasource": { "type": "prometheus", "uid": "${prometheus}" },
      "targets": [{ "expr": "sum by (reason) (rate(sonicrelay_signaling_disconnect_reason_total[5m]))", "legendFormat": "{{reason}}", "refId": "A" }]
    }
```

The last panel (signaling disconnect reasons, from Tasks 1–2) needs no verification — its
metric name is already fixed by this repo's own code — and can be added regardless of
Step 3's outcome.

- [ ] **Step 5: Update the observability doc**

In `docs/observability.md`, add a new subsection after the existing "Structured signaling
logs" section:

```markdown
### Coturn metrics

Coturn exposes its own Prometheus metrics on port 9641 when started with `--prometheus`
(production: `deploy/docker-compose.prodcoturn.yml`) or `prometheus` in
`infra/coturn/turnserver.conf` (dev/prod-profile compose stacks). Scrape it with
[`sonicrelay-coturn`](../observability/prometheus/sonicrelay-scrape.yml) and see the
"Coturn: ..." panels on the Grafana dashboard for active allocations, relayed traffic, and
total sessions — correlate these with the "Signaling disconnect reason/s" panel (driven by
the new `sonicrelay_signaling_disconnect_reason_total` metric) to tell apart a relay-side
capacity problem from a client/network-side one.
```

- [ ] **Step 6: Manual verification**

Run: `docker compose --env-file infra/.env -f infra/compose.yml -f infra/compose.dev.yml up -d`
then `curl http://localhost:9641/metrics` (confirm coturn metrics), import the updated
dashboard JSON into a local Grafana pointed at this stack's Prometheus, and confirm the new
panels render without "no data" (allocate a TURN relay with a real or test client to see the
allocation/traffic panels move).

- [ ] **Step 7: Commit**

```bash
git add deploy/docker-compose.prodcoturn.yml infra/coturn/turnserver.conf infra/compose.dev.yml infra/compose.prod.yml observability/prometheus/sonicrelay-scrape.yml observability/grafana/sonicrelay-webrtc-turn-dashboard.json docs/observability.md
git commit -m "Enable coturn Prometheus metrics, scrape job, and dashboard panels"
```
