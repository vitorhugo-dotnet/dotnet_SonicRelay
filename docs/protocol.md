# HTTP and WebSocket protocol

This document describes routes mapped by the current API. Unless marked public, HTTP requests require `Authorization: Bearer <opaque-access-token>`.

## Health

| Method | Route | Auth | Behavior |
| --- | --- | --- | --- |
| `GET` | `/health/live` | Public | Process liveness only; excludes registered dependency checks. |
| `GET` | `/health/ready` | Public | Checks PostgreSQL and Redis. |

Swagger is enabled by default only in Development, or when `Swagger:Enabled=true`.

## Authentication

`MapIdentityApi<ApplicationUser>` maps the standard ASP.NET Core Identity API under `/auth`: `/register`, `/login`, `/refresh`, `/confirmEmail`, `/resendConfirmationEmail`, `/forgotPassword`, `/resetPassword`, `/manage/2fa` and `/manage/info`. The framework owns their request/response contracts. This project additionally maps:

| Method | Route | Auth | Behavior |
| --- | --- | --- | --- |
| `POST` | `/auth/logout` | Required | Returns `204`; it does not revoke already issued self-contained bearer tokens. |
| `GET` | `/auth/me` | Required | Returns `id`, `email`, `displayName`, `emailConfirmed`, `createdAt` and `lastLoginAt`. |

For desktop/mobile login, omit cookies or use `POST /auth/login?useCookies=false`. The response contains an opaque bearer access token, expiry and refresh token; it is not a JWT.

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

All session routes require authentication.

| Method | Route | Behavior |
| --- | --- | --- |
| `POST` | `/api/sessions/` | Creates a waiting session and publisher participant for an owned, non-revoked source device; returns `201` and a six-character code. |
| `GET` | `/api/sessions/active` | Lists waiting/active sessions owned by or joined by the caller, including connected viewer count. |
| `GET` | `/api/sessions/{sessionId}` | Returns a session to its owner or participant; inaccessible sessions return `404`. |
| `POST` | `/api/sessions/{sessionId}/end` | Owner-only; marks the session ended, disconnects participants and removes the Redis code. Idempotently returns the ended session. |
| `POST` | `/api/sessions/{sessionId}/rotate-code` | Owner-only; rejects ended/expired sessions with `409`, invalidates the previous code and returns a new code. |
| `POST` | `/api/sessions/join` | Resolves a valid code and owned viewer device, enforces the viewer limit, creates/reconnects a participant and activates a waiting session. |

Create request:

```json
{ "sourceDeviceId": "<uuid>", "maxViewers": 3 }
```

`maxViewers` defaults to `Sessions:MaxViewersPerSession` and must be at least one. There is currently no upper bound.

Join request:

```json
{ "code": "ABC123", "deviceId": "<uuid>" }
```

Codes are trimmed, uppercased and must contain exactly six ASCII letters/digits. Wrong, malformed, expired and terminal-session codes all return `404` with the same error. Despite the store method name `RedeemAsync`, a successful lookup does not consume the code; it remains reusable until rotation, session end or expiry.

Session responses contain `id`, `ownerUserId`, `sourceDeviceId`, `status`, `maxViewers`, `codeExpiresAt`, `startedAt`, `endedAt`, `createdAt`, and `code` when a new code is issued.

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
3. Abra o WebSocket autenticado usando `sessionId` e o ID do device Publisher.
4. Guarde seu `participantId` recebido em `session.joined`. Quando outro `session.joined` anunciar um Viewer, use o `participantId` do payload como destino de `publisher.ready`.
5. Para cada Viewer, crie uma `RTCPeerConnection`, adicione a faixa de áudio Opus e envie uma `webrtc.offer` direcionada ao `participantId` dele.
6. Ao receber `webrtc.answer`, aplique o SDP como remote description na conexão daquele Viewer.
7. Troque `webrtc.ice_candidate` nos dois sentidos enquanto o ICE gathering estiver ativo. Candidate vazio/nulo para fim de gathering deve ser representado no payload conforme a biblioteca do client, pois o backend não interpreta o campo.
8. Mantenha uma peer connection por Viewer. Capture áudio e gerencie reconnect/cleanup no app Windows, não nesta API.

### Fluxo do Viewer

1. Autentique pela API, registre um device `flutter_viewer` para `android` ou `ios` e entre com `POST /api/sessions/join`.
2. Abra o WebSocket autenticado usando os `sessionId` e `deviceId` retornados/registrados.
3. Guarde seu `participantId` recebido em `session.joined`. Ao receber `publisher.ready`, aprenda o ID do Publisher pelo campo autenticado `from` e responda com `viewer.ready` para esse destino.
4. Ao receber `webrtc.offer`, crie/configure a `RTCPeerConnection` e aplique o SDP como remote description.
5. Gere a answer, aplique-a localmente e envie `webrtc.answer` ao Publisher.
6. Troque `webrtc.ice_candidate` nos dois sentidos e conecte a faixa de áudio remota ao playback Flutter.
7. Encerre a peer connection ao receber `session.ended`, ao sair da sessão ou ao perder a autorização do device.

### Admissão e envelope

Connect with an authenticated WebSocket upgrade:

```text
GET /ws/signaling?sessionId={uuid}&deviceId={uuid}
Authorization: Bearer <opaque-access-token>
```

Before upgrade, the API verifies:

- both query parameters are UUIDs;
- the session exists and is not ended, expired or past `codeExpiresAt`;
- the device belongs to the authenticated user and is not revoked;
- a participant matches the session, user and device.

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

`session.joined`, `session.left`, `session.ended`, and `error` are server-generated types and are rejected when sent by a client. Routing is constrained to the current session. Errors use the canonical envelope with `type: "error"` and a payload such as `{ "code": "participant_not_found" }`. Other error codes are `invalid_message`, `unsupported_message_type` and `invalid_recipient`.

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

- token de acesso e upgrade WebSocket;
- formato UUID de `sessionId` e `deviceId`;
- existência e estado/validade temporal da sessão;
- propriedade, tipo esperado e revogação do device;
- participação do usuário/device na sessão;
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
- Armazene access/refresh tokens no armazenamento seguro da plataforma e nunca em logs.
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
