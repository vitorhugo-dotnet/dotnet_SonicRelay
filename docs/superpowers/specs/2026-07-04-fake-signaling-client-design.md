# Fake Signaling Client Design

## Goal

Provide a disposable .NET console tool that proves SonicRelay's real HTTP authentication, device, session, and WebSocket signaling flow works without implementing media capture or WebRTC.

## Chosen approach

Add `tools/SonicRelay.SignalingClient` as a dependency-free `net10.0` console application. A standalone client is preferred over embedding this flow in integration-test helpers because developers need to run it against an independently started API and its real PostgreSQL/Redis dependencies. A script was rejected because typed JSON and WebSocket close handling are clearer and testable in C#.

## Flow

The tool accepts the API base URL, creates unique publisher and viewer test accounts, logs both in, registers a `windows_publisher/windows` device and a `flutter_viewer/android` device, creates a session, and joins it by code. It opens one authenticated WebSocket per participant and derives participant IDs only from each server-issued `session.joined` envelope.

It then sends and validates these routed messages in order:

1. `publisher.ready` from publisher to viewer.
2. `viewer.ready` from viewer to publisher.
3. `webrtc.offer` from publisher to viewer with fake SDP test data.
4. `webrtc.answer` from viewer to publisher with fake SDP test data.
5. `webrtc.ice_candidate` from publisher to viewer with fake ICE test data.

Every received envelope must retain the sent message ID, contain the expected server-derived session/sender/recipient IDs, and preserve the fake payload. Failure returns a non-zero process exit code and a concise error. Success prints each completed stage. Both sockets use a normal close handshake in `finally` cleanup.

## Boundaries

The client uses only public API contracts. It does not reference backend projects, access storage directly, send audio, create peer connections, capture WASAPI, or contain Flutter code. Generated accounts and persisted records are intentionally test data; the tool does not add cleanup endpoints the API does not expose.

## Test strategy

Unit tests cover command-line URL parsing and signaling envelope creation/validation, including rejection of wrong routing metadata. Existing backend integration tests remain the source of truth for server routing. A focused build and client test project run verify the new tool without running the full repository test suite.

## Documentation

`tools/SonicRelay.SignalingClient/README.md` documents dependency startup, API startup, execution, expected output, and the explicit no-streaming limitation. The root README links to the tool.
