# Architecture

## System boundary

SonicRelay is a control plane. It authenticates users, persists session state, issues temporary join codes and routes WebRTC signaling messages. It does not capture, encode, buffer, transcode or relay audio. Media flows directly between clients when possible and through coturn when NAT traversal requires it.

```mermaid
flowchart TD
    subgraph Windows["Windows PC - Publisher"]
        WAPP["C#/.NET Desktop App"]
        CAP["WASAPI Loopback Capture"]
        OPUS["Opus Encoder"]
        WRTC_P["WebRTC Publisher"]
        WAPP --> CAP --> OPUS --> WRTC_P
    end

    subgraph Mobile["Mobile - Viewer"]
        FAPP["Flutter App"]
        WRTC_V["WebRTC Receiver"]
        PLAYER["Audio Player"]
        FAPP --> WRTC_V --> PLAYER
    end

    subgraph Edge["Reverse proxy / DNS"]
        HTTPS["HTTPS/WSS API"]
        DNS["DNS-only TURN/STUN"]
    end

    subgraph VPS["VPS"]
        API["SonicRelay API"]
        DB[("PostgreSQL")]
        REDIS[("Redis")]
        TURN["coturn"]
        API --> DB
        API --> REDIS
    end

    WAPP <-->|"auth and signaling"| HTTPS
    FAPP <-->|"auth and signaling"| HTTPS
    HTTPS --> API
    WRTC_P <-->|"WebRTC media"| WRTC_V
    WRTC_P -.->|"relay fallback"| DNS
    WRTC_V -.->|"relay fallback"| DNS
    DNS --> TURN
```

## Components

- `services/SonicRelay.Api`: Minimal API composition, rate limits, health checks, endpoint handlers, WebSocket signaling and session cleanup.
- `src/SonicRelay.Domain`: user, device, session, participant and signaling-event models, and (Phase 1 of issue #26) a parallel device-identity credential and pairing model — see `docs/device-identity.md`.
- `src/SonicRelay.Application`: abstractions for session-code storage and live connection routing.
- `src/SonicRelay.Infrastructure`: EF Core/PostgreSQL persistence, Identity stores, Redis session-code storage and the in-memory connection registry.
- `infra`: development and full-stack production Compose definitions, nginx and coturn configuration.
- `deploy`: API-only production Compose file and SSH deployment script used by GitHub Actions.

The signaling registry is process-local. Multiple API replicas do not share live WebSocket registrations, so horizontal scaling requires sticky routing or a distributed signaling backplane.

## Primary flow

Device endpoints persist owner-scoped Windows Publisher and Flutter Viewer records. Session creation and join validate those devices before admitting participants.

```mermaid
sequenceDiagram
    autonumber
    participant W as Windows Publisher
    participant API as SonicRelay API
    participant R as Redis
    participant DB as PostgreSQL
    participant F as Flutter Viewer
    participant TURN as coturn

    W->>API: POST /auth/login
    API-->>W: access token + refresh token
    W->>API: register Windows Publisher device
    W->>API: POST /api/sessions
    API->>DB: create session + publisher participant
    API->>R: store HMAC-derived code lookup with TTL
    API-->>W: session + temporary code
    W->>API: GET /ws/signaling?sessionId=...&deviceId=...

    F->>API: POST /auth/login
    API-->>F: access token + refresh token
    F->>API: register Flutter Viewer device
    F->>API: POST /api/sessions/join
    API->>R: resolve code
    API->>DB: create viewer participant
    API-->>F: session
    F->>API: GET /ws/signaling?sessionId=...&deviceId=...

    W->>API: webrtc.offer targeted to viewer
    API-->>F: webrtc.offer
    F->>API: webrtc.answer targeted to publisher
    API-->>W: webrtc.answer
    W-.->F: direct WebRTC audio when possible
    W-.->TURN: TURN relay fallback
    F-.->TURN: TURN relay fallback
```

## Persistence model

```mermaid
erDiagram
    APPLICATION_USER ||--o{ DEVICE : owns
    APPLICATION_USER ||--o{ STREAM_SESSION : creates
    APPLICATION_USER ||--o{ SESSION_PARTICIPANT : joins
    DEVICE ||--o{ STREAM_SESSION : publishes
    DEVICE ||--o{ SESSION_PARTICIPANT : connects_as
    STREAM_SESSION ||--o{ SESSION_PARTICIPANT : has
    STREAM_SESSION ||--o{ SIGNALING_EVENT : may_log

    APPLICATION_USER {
        uuid id PK
        string email
        string displayName
        bool isDisabled
        datetime createdAt
        datetime lastLoginAt
    }
    DEVICE {
        uuid id PK
        uuid ownerUserId
        string name
        string type
        string platform
        bool trusted
        bool revoked
    }
    STREAM_SESSION {
        uuid id PK
        uuid ownerUserId
        uuid sourceDeviceId
        string status
        int maxViewers
        datetime codeExpiresAt
    }
    SESSION_PARTICIPANT {
        uuid id PK
        uuid sessionId
        uuid userId
        uuid deviceId
        string role
        string status
        string connectionId
    }
    SIGNALING_EVENT {
        uuid id PK
        uuid sessionId
        string eventType
        uuid fromParticipantId
        uuid toParticipantId
    }
```

EF Core maps these tables but does not declare relational foreign-key navigation constraints in `AppDbContext`; ownership and membership checks are enforced by handlers.

## Session and peer topology

Users only see sessions they own or participate in. A publisher is expected to create one peer connection per viewer.

```mermaid
flowchart LR
    subgraph A["User A"]
        A_PC["Publisher device"] --> A_SESSION["Session"]
        A_PHONE["Viewer device"] --> A_SESSION
    end
    subgraph B["User B"]
        B_PC["Publisher device"] --> B_SESSION["Session"]
        B_PHONE["Viewer device"] --> B_SESSION
    end
    A_SESSION -. "isolated authorization scope" .- B_SESSION
```

```mermaid
flowchart TD
    PC["Windows Publisher"]
    V1["Viewer 1"]
    V2["Viewer 2"]
    V3["Viewer 3"]
    PC <-->|"PeerConnection 1"| V1
    PC <-->|"PeerConnection 2"| V2
    PC <-->|"PeerConnection 3"| V3
```

## Decision records

- [ADR 0001: Keep media outside the backend](adr/0001-control-plane-only.md)
- [ADR 0002: Use Identity opaque bearer tokens](adr/0002-identity-bearer-tokens.md)
- [ADR 0003: Split durable and ephemeral storage](adr/0003-postgresql-and-redis-storage.md)
- [ADR 0004: Use authenticated WebSocket signaling](adr/0004-authenticated-websocket-signaling.md)
- [ADR 0005: Symmetric device credentials with a parallel DeviceBearer scheme](adr/0005-device-identity-credentials.md)
