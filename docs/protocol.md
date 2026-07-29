# HTTP and WebSocket protocol

This document describes routes mapped by the current API. Desktop/mobile client
flows for sessions, signaling and TURN use `Authorization: Bearer
<DeviceBearer-token>`. ASP.NET Core Identity tokens remain only for retained
legacy/account endpoints pending Phase 4 cleanup.

## Health

| Method | Route | Auth | Behavior |
| --- | --- | --- | --- |
| `GET` | `/health/live` | Public | Process liveness only; excludes registered dependency checks. |
| `GET` | `/health/ready` | Public | Checks PostgreSQL and Redis. |

Swagger is enabled by default only in Development, or when `Swagger:Enabled=true`.

## Authentication

### Device identity (current desktop/mobile flow)

Bootstrap a publisher or viewer with `POST /api/devices/bootstrap`, then
exchange its device ID and one-time credential secret at `POST
/api/devices/token` for a short-lived `DeviceBearer` JWT. Use that token for
sessions, WebSocket signaling and TURN credentials. Before a viewer joins a
publisher's session, the two devices establish a durable pairing through
`POST /api/pairings/challenges` and `POST /api/pairings/complete`.

### Identity (retained legacy/account endpoints)

`MapIdentityApi<ApplicationUser>` maps the standard ASP.NET Core Identity API under `/auth`: `/register`, `/login`, `/refresh`, `/confirmEmail`, `/resendConfirmationEmail`, `/forgotPassword`, `/resetPassword`, `/manage/2fa` and `/manage/info`. The framework owns their request/response contracts. This project additionally maps:

| Method | Route | Auth | Behavior |
| --- | --- | --- | --- |
| `POST` | `/auth/logout` | Required | Returns `204`; it does not revoke already issued self-contained bearer tokens. |
| `GET` | `/auth/me` | Required | Returns `id`, `email`, `displayName`, `emailConfirmed`, `createdAt` and `lastLoginAt`. |

These Identity routes are retained for legacy/account management pending Phase 4;
desktop/mobile clients must not use them for sessions, signaling, TURN, or
device pairing. The legacy login response contains an opaque bearer access
token, expiry and refresh token; it is not a JWT.

```json
{
  "tokenType": "Bearer",
  "accessToken": "<opaque-access-token>",
  "expiresIn": 900,
  "refreshToken": "<opaque-refresh-token>"
}
```

Login and refresh use fixed-window, IP-keyed rate limits.

## Devices

All device routes require authentication and operate only on devices owned by the caller.

| Method | Route | Current behavior |
| --- | --- | --- |
| `POST` | `/api/devices/` | Validates type/platform, persists a device and returns `201 Created`. |
| `GET` | `/api/devices/` | Lists the caller's devices. |
| `GET` | `/api/devices/{deviceId}` | Returns an owned device or `404`. |
| `PATCH` | `/api/devices/{deviceId}` | Updates an owned device's name and/or public key. |
| `DELETE` | `/api/devices/{deviceId}` | Deletes an owned device and returns `204`. |
| `POST` | `/api/devices/{deviceId}/revoke` | Idempotently revokes an owned device. |

Valid pairs are `windows_publisher`/`windows` and `flutter_viewer`/`android|ios`. Revoked devices cannot create, join or connect to sessions.

## Sessions

All session routes require an active `DeviceBearer` token; capability-changing routes also require their matching device scope.

| Method | Route | Behavior |
| --- | --- | --- |
| `POST` | `/api/sessions/` | Creates a waiting session and publisher participant for the authenticated publisher device; returns `201` and a six-character code. |
| `GET` | `/api/sessions/active` | Lists waiting/active sessions where the caller's authenticated device is the source or a participant, including connected viewer count. |
| `GET` | `/api/sessions/{sessionId}` | Returns a session to its source device or a participant; inaccessible sessions return `404`. |
| `POST` | `/api/sessions/{sessionId}/end` | Source-device-only; marks the session ended, disconnects participants and removes the Redis code. Idempotently returns the ended session. |
| `POST` | `/api/sessions/{sessionId}/rotate-code` | Source-device-only; rejects ended/expired sessions with `409`, invalidates the previous code and returns a new code. |
| `POST` | `/api/sessions/join` | Resolves a valid code for the authenticated viewer device, enforces the viewer limit, creates/reconnects a participant and activates a waiting session. A new participant also requires an active pairing to the session source device. |

Create request:

```json
{ "maxViewers": 3 }
```

`maxViewers` defaults to `Sessions:MaxViewersPerSession` and must be at least one. There is currently no upper bound.

Join request:

```json
{ "code": "ABC123" }
```

Codes are trimmed, uppercased and must contain exactly six ASCII letters/digits. A new viewer participant needs both an active DevicePairing to the session's source device and the current session join code. Wrong, malformed, expired and terminal-session codes, as well as a missing/revoked pairing for a new participant, all return the same `404` invalid/expired-code response. Existing participants may reconnect after pairing revocation until the session ends. Despite the store method name `RedeemAsync`, a successful lookup does not consume a code; it remains reusable until rotation, session end or expiry.

Session responses contain `id`, `sourceDeviceId`, `status`, `maxViewers`, `codeExpiresAt`, `startedAt`, `endedAt`, `createdAt`, and `code` when a new code is issued.

## WebSocket signaling

### Mapa mental: signaling não é mídia

- **WebSocket** é o canal persistente de signaling entre cada client e o backend.
- **WebRTC** cria a conexão de mídia entre Publisher e Viewer, direta ou via relay.
- **SDP offer/answer** negocia capacidades e parâmetros da conexão.
- **ICE candidate** descreve um caminho de rede que um peer pode tentar.
- **STUN** ajuda um peer a descobrir seu endereço público.
- **TURN/coturn** retransmite os pacotes WebRTC quando a conexão direta falha.
- **Opus** é o codec de áudio usado pelos clients; não roda no backend.

O backend é o **control-plane**: autentica, autoriza, mantém sessões e encaminha signaling. O áudio pertence ao **media-plane** e flui entre os clients ou através do coturn. A API não captura, codifica, decodifica, armazena nem retransmite áudio.

### Fluxo do Publisher

1. Autentique pela API e registre um device `windows_publisher`/`windows`.
2. Crie uma sessão com `POST /api/sessions/` e exiba o código temporário ao usuário.
3. Abra o WebSocket autenticado usando apenas `sessionId`; a identidade do Publisher vem do token `DeviceBearer`.
4. Guarde seu `participantId` recebido em `session.joined`. Quando outro `session.joined` anunciar um Viewer, use o `participantId` do payload como destino de `publisher.ready`.
5. Para cada Viewer, crie uma `RTCPeerConnection`, adicione a faixa de áudio Opus e envie uma `webrtc.offer` direcionada ao `participantId` dele.
6. Ao receber `webrtc.answer`, aplique o SDP como remote description na conexão daquele Viewer.
7. Troque `webrtc.ice_candidate` nos dois sentidos enquanto o ICE gathering estiver ativo. Candidate vazio/nulo para fim de gathering deve ser representado no payload conforme a biblioteca do client, pois o backend não interpreta o campo.
8. Mantenha uma peer connection por Viewer. Capture áudio e gerencie reconnect/cleanup no app Windows, não nesta API.

### Fluxo do Viewer

1. Autentique pela API, registre um device `flutter_viewer` para `android` ou `ios` e entre com `POST /api/sessions/join`.
2. Abra o WebSocket autenticado usando apenas `sessionId`; a identidade do Viewer vem do token `DeviceBearer`.
3. Guarde seu `participantId` recebido em `session.joined`. Ao receber `publisher.ready`, aprenda o ID do Publisher pelo campo autenticado `from` e responda com `viewer.ready` para esse destino.
4. Ao receber `webrtc.offer`, crie/configure a `RTCPeerConnection` e aplique o SDP como remote description.
5. Gere a answer, aplique-a localmente e envie `webrtc.answer` ao Publisher.
6. Troque `webrtc.ice_candidate` nos dois sentidos e conecte a faixa de áudio remota ao playback Flutter.
7. Encerre a peer connection ao receber `session.ended`, ao sair da sessão ou ao perder a autorização do device.

### Admissão e envelope

Connect with an authenticated WebSocket upgrade:

```text
GET /ws/signaling?sessionId={uuid}
Authorization: Bearer <DeviceBearer-token>
```

Before upgrade, the API verifies:

- the `sessionId` query parameter is a UUID;
- the session exists and is not ended, expired or past `codeExpiresAt`;
- the `DeviceBearer` token is valid and has the required signaling scope;
- the authenticated device is active and a participant matches that device and session.

Validation failures return HTTP `400`, `401`, `403`, `404` or `410` before the upgrade. Every server frame uses this envelope:

```json
{
  "type": "session.joined",
  "messageId": "<uuid>",
  "sessionId": "<uuid>",
  "from": null,
  "to": "<participant-uuid>",
  "timestamp": "2026-07-04T14:00:00Z",
  "payload": {
    "participantId": "<participant-uuid>",
    "role": "publisher"
  }
}
```

Ao admitir um socket, o servidor envia ao novo socket um `session.joined` sobre ele próprio (`from: null`) e anuncia o novo participante aos peers já conectados (`from: <new-participant-uuid>`). O payload sempre contém `participantId` e `role`. Assim, o Publisher descobre cada Viewer sem compartilhar IDs fora do protocolo; o Viewer descobre o Publisher quando recebe `publisher.ready`.

### Client messages

Clients send the same envelope shape. `type` is required. `messageId` may be supplied as a UUID and is preserved; otherwise the server generates it. Client `sessionId`, `from`, and `timestamp` values are never trusted. The server derives the session from the connection, overwrites `from` with the authenticated participant, and assigns its own timestamp.

Um client precisa enviar apenas `type`, `to`, `payload` e, opcionalmente, `messageId`:

```json
{
  "type": "viewer.ready",
  "messageId": "0f057269-0f91-4a30-a7be-f5755b01f82a",
  "to": "<publisher-participant-uuid>",
  "payload": {}
}
```

`ping` requires no recipient and produces an enveloped `pong`. These routed types require a UUID `to` participant in the same live session and may include any JSON `payload`:

- `publisher.ready`
- `viewer.ready`
- `webrtc.offer`
- `webrtc.answer`
- `webrtc.ice_candidate`
- `pong`

The server emits a normalized routed frame:

```json
{
  "type": "webrtc.offer",
  "messageId": "<uuid>",
  "sessionId": "<uuid>",
  "from": "<sender-participant-uuid>",
  "to": "<recipient-participant-uuid>",
  "timestamp": "2026-07-04T14:00:00Z",
  "payload": {}
}
```

`session.joined`, `session.left`, `session.ended`, `participant.disconnected`, `participant.reconnected`, and `error` are server-generated types and are rejected when sent by a client. Routing is constrained to the current session. Errors use the canonical envelope with `type: "error"` and a payload such as `{ "code": "participant_not_found" }`. Other error codes are `invalid_message`, `unsupported_message_type` and `invalid_recipient`.

### Reconnect grace period

A dropped signaling socket does not immediately tear down the participant. When the underlying
session is still live, the server holds the participant in a `reconnecting` state for
`Sessions:ParticipantDisconnectGraceSeconds` (default `15`, configurable via
`Sessions__ParticipantDisconnectGraceSeconds`) before finalizing it as left:

1. On disconnect, other participants receive `participant.disconnected` with `{ "participantId": "<uuid>" }`. Treat this as "peer is transiently unreachable" — keep the peer connection and wait, do not tear it down yet.
2. If the same participant (same session, user and device) reconnects its WebSocket within the grace period, the reused participant row is reported to peers as `participant.reconnected` (same payload shape as `session.joined`: `{ "participantId": "<uuid>", "role": "publisher" | "viewer" }`) instead of a fresh `session.joined`. The reconnecting client itself always gets `session.joined` about itself, as on a first connect, so it can confirm its `participantId`. Clients should resume the existing peer connection on `participant.reconnected`, restarting ICE or renegotiating rather than starting over from scratch.
3. If the grace period elapses without a reconnect, the participant is finalized as disconnected and peers receive the usual `session.left`.

A participant that rejoins via `POST /api/sessions/join` before reopening its WebSocket (a full manual reconnect) also cancels any pending grace period once its new WebSocket connects, so both a lightweight socket-only retry and a full re-authenticate-and-rejoin flow converge on the same `participant.reconnected` signal. Ending a session (`POST /api/sessions/{sessionId}/end`) always wins immediately over a pending grace period.

A viewer mid-grace-period still holds its viewer slot: `POST /api/sessions/join`'s capacity check counts `reconnecting` viewers alongside `connected` ones, so a dropped viewer cannot be displaced by a new one joining during the grace window.

The server never automatically reconnects to a session that has already ended or expired; clients must treat `session.ended`, socket closure without any of the above server messages, and HTTP `410`/`404` as terminal and stop retrying that session.

SDP and ICE payloads are opaque JSON to the API. SDP describes the peer media/session parameters, and ICE candidates describe network paths discovered by the peers. The server forwards those payloads unchanged and never writes their content to logs; routing logs contain only message type, session ID, sender ID, recipient ID, and message ID.

### Offer/answer flow

O Publisher inicia a negociação para cada Viewer. Use o SDP produzido pela biblioteca WebRTC sem analisá-lo ou remontá-lo manualmente:

```json
{
  "type": "webrtc.offer",
  "to": "<viewer-participant-uuid>",
  "payload": { "type": "offer", "sdp": "<sdp-gerado-pelo-webrtc>" }
}
```

O Viewer responde ao participante Publisher indicado em `from`:

```json
{
  "type": "webrtc.answer",
  "to": "<publisher-participant-uuid>",
  "payload": { "type": "answer", "sdp": "<sdp-gerado-pelo-webrtc>" }
}
```

O backend preserva `payload`, mas normaliza os metadados do envelope. O recebimento de signaling não significa que a mídia conectou: cada client deve observar os estados ICE/peer connection e tratar timeout ou reconexão.

### ICE candidate flow

Publisher e Viewer enviam candidates conforme a biblioteca WebRTC os descobre (trickle ICE), sempre direcionados ao outro participante:

```json
{
  "type": "webrtc.ice_candidate",
  "to": "<other-participant-uuid>",
  "payload": {
    "candidate": "candidate:<dados-omitidos>",
    "sdpMid": "0",
    "sdpMLineIndex": 0
  }
}
```

Configure STUN e TURN/coturn nos clients ao criar a peer connection. Essas credenciais não passam pelo payload de signaling e nunca devem ser incorporadas a exemplos, logs ou repositórios públicos.

### O que o backend valida

- token `DeviceBearer` e upgrade WebSocket;
- formato UUID de `sessionId` (o único parâmetro de consulta do signaling);
- existência e estado/validade temporal da sessão;
- status e versão de credencial do device autenticado pelo token;
- participação do device autenticado na sessão;
- JSON válido, `type` permitido, limite de 64 KiB e frame textual;
- presença/formato de `to` e pertencimento do destinatário à mesma sessão;
- identidade do remetente, derivada do socket autenticado.

### O que o backend não inspeciona

- conteúdo ou validade semântica do SDP;
- conteúdo, alcançabilidade ou prioridade de ICE candidates;
- codec, bitrate, samples ou qualquer pacote de áudio;
- estado interno da peer connection;
- credenciais/configuração STUN/TURN dos clients.

Esse limite é deliberado: o backend coordena peers e trata `payload` como JSON opaco. Validação WebRTC pertence às bibliotecas dos clients.

### Notas de segurança para clients

- Use apenas HTTPS/WSS em produção e valide o certificado do servidor.
- Armazene o segredo de credencial persistente do device e os tokens
  `DeviceBearer` de curta duração no armazenamento seguro da plataforma e
  nunca em logs. Tokens Identity de acesso/refresh são apenas do fluxo
  legado/account.
- Não registre SDP, ICE candidates, tokens, códigos de sessão nem credenciais TURN; SDP/ICE podem revelar dados de rede e mídia.
- Aceite mensagens somente pelo socket autenticado e para a sessão/participante esperado, mesmo com a normalização do servidor.
- Trate `error`, `session.left`, `session.ended`, fechamento do socket e expiração como estados normais e limpe recursos.
- Não confunda coturn com a API: coturn pode retransmitir pacotes WebRTC cifrados; a API só retransmite signaling JSON.

### Confusões comuns de iniciantes

- WebSocket conectado não significa áudio conectado; ele apenas permite negociar WebRTC.
- SDP não contém o áudio e ICE candidate não é um pacote de áudio.
- STUN não retransmite mídia; TURN/coturn é o fallback que pode retransmiti-la.
- Opus roda nos clients através do stack WebRTC, não no ASP.NET Core.
- `to` recebe um **participant ID**, não user ID, device ID ou session ID.
- Uma sessão com vários Viewers exige uma peer connection Publisher↔Viewer para cada Viewer no MVP; não existe SFU.

Text messages may be fragmented but may not exceed 64 KiB. Binary frames are rejected. Disconnects broadcast `session.left` to other live participants. When the session becomes terminal, the server sends `session.ended` and closes routing for that connection. There is no persisted signaling history; `SignalingEvent` is mapped in EF Core but the endpoint does not write it.

## Próximos passos dos clients

Este repositório termina no contrato de signaling. O Windows Publisher deve implementar captura WASAPI, criação das peer connections e publicação Opus em seu próprio repositório. O Flutter Viewer deve implementar recepção WebRTC e playback no repositório mobile. Nenhuma dessas responsabilidades deve ser movida para o backend, e o MVP não requer SFU ou outro media server.

Para uma introdução aos conceitos, leia o [guia para leigos](beginner-guide.md). Para os limites arquiteturais e controles existentes, consulte [Architecture](architecture.md) e [Security](security.md).
