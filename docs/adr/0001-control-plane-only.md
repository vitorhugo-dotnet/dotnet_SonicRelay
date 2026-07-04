# ADR 0001: Keep media outside the backend

- Status: Accepted
- Date: 2026-07-04

## Context

SonicRelay needs authenticated session coordination but targets low-latency audio between a Windows publisher and mobile viewers. Sending media through the ASP.NET API would add latency, bandwidth cost and media-processing responsibilities.

## Decision

The backend is a control plane only. Clients negotiate WebRTC through the API, then send Opus media directly when possible or through coturn as a relay fallback.

## Consequences

The API remains focused on identity, authorization, state and signaling. Publisher/viewer clients must implement capture, encoding, peer connections, playback and media telemetry. coturn must be operated separately, and larger fan-out may eventually require an SFU.
