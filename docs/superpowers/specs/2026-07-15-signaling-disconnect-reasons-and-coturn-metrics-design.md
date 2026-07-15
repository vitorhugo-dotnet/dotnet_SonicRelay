# Signaling Disconnect Reasons and Coturn Prometheus Metrics

## Goal

Users report frequent connection loss with clients minimized, backgrounded, or even in
the foreground. The API already has solid observability (issue #21: `/metrics`,
client-reported WebRTC stats, structured signaling logs, a Grafana dashboard and alerts),
but two gaps make the *server* side of a connection loss hard to diagnose: the signaling
endpoint never records *why* a WebSocket closed abnormally, and coturn's own Prometheus
metrics (relay allocations, TURN traffic) aren't enabled — only container-level network
counters are scraped today.

## Scope

In scope: classifying and logging/metricing abnormal signaling WebSocket closures, and
enabling + scraping + dashboarding coturn's native Prometheus endpoint. Out of scope:
changing reconnect/grace-period behavior (already implemented) and any client-side change
(tracked separately in `flutter_SonicRelay` and `windows_SonicRelay`).

## Part A — Signaling disconnect reasons

`SignalingWebSocketEndpoint.HandleAsync` calls `ReceiveLoopAsync` inside a `try/finally`
with no `catch`: if the socket drops abnormally (client killed mid-read, network drop,
malformed frame), the exception propagates through the `finally` (which still does
connection-registry cleanup and grace-period bookkeeping) but is never logged with a
reason — only the generic "disconnected" log lines fire, indistinguishable from a normal
close.

Add a `catch` that classifies the exception into a fixed, low-cardinality reason (safe as
a metric label, matching the existing convention in `Observability.SonicRelayMetrics`):

| Exception | Reason |
| --- | --- |
| Loop returns normally (client sent Close frame) | `normal_closure` |
| `OperationCanceledException` (request aborted) | `cancelled` |
| `WebSocketException` (protocol/frame error, oversized message) | `protocol_error` |
| Any other `IOException`/`WebSocketException` from the transport | `transport_error` |
| Anything else unexpected | `unknown` |

This reason is logged (`logger.LogWarning("Signaling connection closed abnormally for
participant {ParticipantId} in session {SessionId}: {Reason}", ...)`, no exception
message/stack trace to keep log lines free of incidental data) and recorded in a new
counter, `sonicrelay_signaling_disconnect_reason_total{reason}`, added to
`Observability.SonicRelayMetrics` next to the existing `RecordMessage`/connection
counters.

## Part B — Coturn Prometheus metrics

`deploy/docker-compose.prodcoturn.yml` runs coturn purely through CLI args (no mounted
config) and does not pass `--prometheus`, so the only visibility into coturn today is
container-level network drop counters (cAdvisor) and raw logs (Loki) — no allocation
count, no relayed traffic volume, no session count from coturn itself.

Add `--prometheus` to the `command:` list. Coturn's Prometheus listener defaults to port
9641; since the container already runs with `network_mode: host`, no `ports:` mapping is
needed — the port is already reachable wherever Prometheus can reach the host. Document
in the compose file's existing comment block that 9641 should stay closed to anything
except the Prometheus scrape source (it is not meant to be public).

Add a `sonicrelay-coturn` job to `observability/prometheus/sonicrelay-scrape.yml`
targeting `<host>:9641`, and extend the existing Grafana dashboard
(`sonicrelay-webrtc-turn-dashboard.json`) with a new row using coturn's native metrics
(active allocations, relayed traffic bytes, total sessions) alongside the existing
"Container network drops (api + coturn)" panel, so a relay-side capacity or traffic
problem is visible next to the client-reported packet-loss/jitter/RTT panels already
there.

## Data and safety

The disconnect-reason enum is fixed and never derived from free text, so cardinality and
log content stay bounded exactly like the existing signaling metrics. Coturn's Prometheus
endpoint exposes only aggregate relay metrics (no per-peer IP/credential data), consistent
with the "no PII in metrics" rule already followed by `/metrics` on the API.

## Verification

API: a focused test drives `HandleMessageAsync`/`ReceiveLoopAsync` failure paths (or the
existing signaling integration test harness, extended) to confirm each exception type
maps to the expected reason and increments the right counter. Coturn: verified manually by
running the dev/prod compose stack and confirming `curl <host>:9641/metrics` returns
coturn's metric families, then confirming the new Grafana panel renders during a real TURN
relay session.

## Follow-up: repository scope note

This work originally targeted `dotnet.MeatData`, which is an unrelated portfolio project
with no signaling/coturn/WebRTC code. The actual SonicRelay backend is
`vitorhugo-dotnet/dotnet_SonicRelay`; this spec and its implementation live there instead.
