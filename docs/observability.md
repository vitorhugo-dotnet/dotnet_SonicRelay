# WebRTC/TURN observability

Implements issue #21: minimum telemetry to investigate eventual packet loss in
WebRTC/TURN sessions and diagnose it in Grafana. The API does **not** carry real-time
media — it only handles auth, sessions and signaling — so the telemetry here covers
signaling, session transport mode and client-reported WebRTC stats, correlated with the
existing coturn/container metrics.

## What is exposed

### `/metrics` (Prometheus)

The API exposes Prometheus metrics **anonymously** at `/metrics` (so Prometheus can scrape
it). No session ids, IPs, SDP or ICE candidates are ever used as label values — only
bounded enums.

| Metric | Type | Labels | Meaning |
| --- | --- | --- | --- |
| `sonicrelay_sessions_active` | gauge | — | Sessions with ≥1 connected participant. |
| `sonicrelay_signaling_connections_active` | gauge | — | Active signaling WebSocket connections. |
| `sonicrelay_signaling_messages_total` | counter | `type` | Signaling messages handled, by type. |
| `sonicrelay_signaling_errors_total` | counter | `reason` | Signaling errors returned to clients. |
| `sonicrelay_session_transport_mode_total` | counter | `mode` | Selected transport: `direct`, `stun`, `turn_udp`, `turn_tcp`, `turn_tls`. |
| `sonicrelay_session_ice_restarts_total` | counter | — | ICE restarts reported by clients. |
| `sonicrelay_session_packet_loss_ratio` | histogram | `role` | Inbound audio packet-loss ratio (0..1). |
| `sonicrelay_session_jitter_ms` | histogram | `role` | Inbound audio jitter (ms). |
| `sonicrelay_session_rtt_ms` | histogram | `role` | WebRTC round-trip time (ms). |

### Data-retention metrics

The retention sweep (issue #44) publishes its own series here, for a different reason
than the rest of this page: a cleanup that silently stops running has no user-visible
symptom, and SonicRelay's Google Play Data Safety declaration stops being true while
everything else looks healthy. These are the only signal that it is still working.

| Metric | Type | Labels | Meaning |
| --- | --- | --- | --- |
| `sonicrelay_data_retention_runs_total` | counter | — | Cleanup passes that completed. |
| `sonicrelay_data_retention_deleted_records_total` | counter | `entity` | Records permanently deleted, by table. |
| `sonicrelay_data_retention_failures_total` | counter | — | Cleanup passes that failed. |
| `sonicrelay_data_retention_last_success_timestamp` | gauge | — | Unix seconds of the last fully successful pass. |
| `sonicrelay_device_identity_rotations_total` | counter | — | Device identities replaced by a new identifier. |

`entity` is a fixed set of table names, so a scrape can never reveal *which* device or
session was erased. `/health/ready` also carries a `data-retention` check that turns
unhealthy once the last success is older than `DataRetention:StaleAfterHours` (48 by
default). See [data retention](data-retention.md) for the policy these enforce.

### Client WebRTC stats ingestion

Clients POST periodic `getStats()` snapshots to an authenticated endpoint; only a
participant of the session may report for it (`403` otherwise). The report never carries
SDP or full ICE candidates.

```http
POST /api/webrtc/stats
Authorization: Bearer <access_token>
Content-Type: application/json

{
  "sessionId": "…",
  "role": "viewer",
  "iceConnectionState": "connected",
  "iceRestart": false,
  "selectedCandidatePair": {
    "localCandidateType": "host|srflx|relay",
    "remoteCandidateType": "host|srflx|relay",
    "protocol": "udp|tcp",
    "relayProtocol": "udp|tcp|tls"
  },
  "inboundAudio": { "packetsReceived": 12345, "packetsLost": 12, "jitter": 0.012 },
  "candidatePair": { "currentRoundTripTime": 0.08 }
}
```

The API derives the transport mode from the selected candidate pair, computes the
packet-loss ratio (`packetsLost / (packetsReceived + packetsLost)`), converts jitter/RTT to
milliseconds, and records them into the metrics above (returns `202 Accepted`). It also
writes a structured log line with a **hashed** session id so logs can be correlated to a
session without exposing the real id.

### Structured signaling logs

Signaling connect/disconnect and message-routing events are logged structurally (session
id, participant id, connection id, message type) without SDP/ICE bodies, so Loki can be
filtered by `container="sonicrelay-api"`.

## Wiring it into your Grafana stack

The existing stack already has `prometheus`, `loki`, `tempo` and `jaeger` datasources.

1. **Scrape the API.** Add [`observability/prometheus/sonicrelay-scrape.yml`](../observability/prometheus/sonicrelay-scrape.yml)
   as a job under Prometheus `scrape_configs:` (adjust the target host/port to your deploy).
2. **Load the alerts.** Add [`observability/prometheus/sonicrelay-alerts.yml`](../observability/prometheus/sonicrelay-alerts.yml)
   to Prometheus `rule_files:` (or import as Grafana-managed alert rules). It covers:
   packet loss > 2% (p95, 5m), RTT p95 > 300ms (5m), jitter p95 > 30ms (5m), elevated
   signaling error rate, and heavy TURN TCP/TLS use (UDP likely blocked).
3. **Import the dashboard.** Import
   [`observability/grafana/sonicrelay-webrtc-turn-dashboard.json`](../observability/grafana/sonicrelay-webrtc-turn-dashboard.json)
   into Grafana ("SonicRelay - WebRTC/TURN") and select the `prometheus` and `loki`
   datasources when prompted. Panels: active sessions/connections, signaling messages &
   errors, transport mode (direct vs relay), ICE restarts, packet loss / jitter / RTT
   percentiles, container network drops for api/coturn, and recent api/coturn logs.

## How to reproduce / test

- **External network:** connect a viewer over a real network and watch the *Transport mode*
  panel — `direct` or `stun` when NAT permits, `turn_udp` when relayed.
- **UDP degraded/blocked:** force the publisher to relay (Windows: *Settings → Force relay*)
  or block UDP 3478 outbound on the client; the panel should show `turn_tcp`/`turn_tls`, and
  the *TURN TCP/TLS heavily used* alert should fire after sustained use.
- **Correlate a loss event:** with a session live, watch packet-loss/jitter/RTT percentiles
  together with the transport mode and the api/coturn logs panel to see whether loss
  coincides with a relay switch, ICE restart or signaling error.
- **Data retention:** confirm `sonicrelay_data_retention_last_success_timestamp` advances
  at least daily. `time() - sonicrelay_data_retention_last_success_timestamp` is the age of
  the last successful cleanup and is what the `SonicRelayDataRetentionStalled` alert
  watches.

## Client recovery journal

The desktop publisher and the Flutter viewer each write a recovery line per step
of a reconnect into their own on-device diagnostic log (category `Recovery`),
using one shared vocabulary. Both are redacted through the existing
`DiagnosticRedactor` before they hit disk, so a journal line never carries a
token, a full SDP body or a full ICE candidate — a recovery `reason` is usually a
raw error string, which is exactly where those leak in.

The vocabulary is fixed and identical on both clients. That is the point:
recovery spans two client codebases and this backend, so correlating a viewer's
timeline against a publisher's has to be a matter of sorting by timestamp, not of
translating between two ad-hoc phrasings.

| Event | Meaning |
| --- | --- |
| `network_lost` | The device reports no usable transport; recovery is parked and spends no attempt budget. |
| `network_restored` | A transport came back; a stabilization window runs before the next attempt. |
| `recovery_started` | A new recovery generation began. |
| `recovery_cancelled` | The lifecycle was cancelled (a deliberate close) while recovering. |
| `stale_attempt_ignored` | A result arrived from a superseded generation and was discarded. |
| `signaling_reconnect_started` / `_succeeded` | One signaling socket attempt. |
| `session_rejoin_started` / `_succeeded` | Rejoining the session over the restored socket. |
| `ice_restart_started` / `_succeeded` | ICE restart on an existing peer connection. |
| `peer_rebuild_started` / `_succeeded` | The peer connection was rebuilt after a restart failed. |
| `media_resumed` | Inbound RTP advanced again — the only thing that makes a viewer read as live. |
| `recovery_failed` | Recovery gave up; `reason` says why. |

Every line carries `generation` and `attempt`. The generation is what makes an
out-of-order log readable: an attempt that finishes after a newer one has taken
over still logs, and without the stamp its lines are indistinguishable from the
live attempt's — which is what makes "it said connected but no audio came back"
so hard to reconstruct after the fact. Steps also carry `stage`, `state`,
`reason` and a masked `sessionCorrelationId` where they apply.

These journals are local to the device and are surfaced through each client's
existing diagnostic export; they are not scraped by Prometheus.

## Not covered (deliberate follow-ups)

- Per-peer `availableIncoming/OutgoingBitrate` and `concealedSamples` are accepted by the
  endpoint but not yet turned into dedicated metrics/panels.
- Tempo/Jaeger tracing of signaling is out of scope for this first telemetry pass.
