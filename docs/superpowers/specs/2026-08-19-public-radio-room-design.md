# Sala Pública de Rádio (Public Radio Room) — Design

- **Data:** 2026-08-19
- **Status:** Aprovado para planejamento de implementação
- **Repositório principal:** `dotnet_SonicRelay` (Minimal API)
- **Repositórios impactados (consumo, sem mudanças de design aqui):** `flutter_mobile-web_SonicRelay` (único viewer suportado inicialmente)

## Contexto e objetivo

O objetivo é facilitar o teste do SonicRelay por pessoas que não vão instalar o
app desktop. Para isso, a API vai expor uma **sala pública de "rádio"**: uma
`StreamSession` sempre disponível (quando habilitada), com um publisher
sintético (não um device real) que toca faixas MP3 montadas via volume,
transmitindo via WebRTC para até 20 viewers simultâneos, exatamente como um
publisher de verdade faz hoje.

Tudo deve ser configurável via variáveis de ambiente e volume no
docker-compose, sem exigir rebuild de imagem para trocar as faixas ou
ligar/desligar a feature.

## Decisões de arquitetura

### 1. Fica no mesmo repositório/solution, como módulo novo

Não é um serviço/repositório separado. É um projeto novo na solution do
`dotnet_SonicRelay` (`SonicRelay.Infrastructure.VirtualPublisher`), referenciado
pelo `services/SonicRelay.Api`, seguindo a mesma estrutura de Clean
Architecture já usada no projeto (Domain / Application / Infrastructure / Api).

**Por quê:** o servidor já expõe toda a infraestrutura necessária (sessions,
sinalização WebSocket, ICE servers via coturn) — criar um serviço separado
duplicaria autenticação, deploy e configuração sem necessidade. A feature é
claramente delimitada (não reaproveita `StreamSession`/`SignalingWebSocketEndpoint`
como estão, mas os usa como infraestrutura de transporte).

### 2. Modelo de transmissão: rádio ao vivo via WebRTC (não HTTP streaming)

O servidor é a fonte única de verdade da posição de reprodução. A transmissão
usa o mesmo transporte que já existe para publishers reais — WebRTC via
coturn/STUN/TURN e sinalização por WebSocket (`/ws/signaling`) — e **não** um
endpoint HTTP de streaming de áudio (ex. range requests / icecast-like).

**Por quê:** o usuário confirmou que o único viewer hoje (app Flutter) já
implementa o fluxo de viewer via `flutter_webrtc` contra o `/ws/signaling`
existente, e que a transmissão deve funcionar "igual o aplicativo desktop
.NET Avalonia", que também usa WebRTC (SIPSorcery + Concentus) sobre esse
mesmo protocolo de sinalização.

Quem entra "sintoniza" a partir do ponto atual da faixa em reprodução — não
há rebobinar nem sincronização de início para o novo viewer.

### 3. Publisher sintético reaproveita o pipeline de áudio do app desktop

O app desktop (`desktop_dotnet_SonicRelay`) já implementa, em bibliotecas
.NET puras sem dependência de UI (`SonicRelay.Windows.WebRtc`, `.Signaling`,
`.Audio`, `.ApiClient`, `.Core`):

- **WebRTC:** SIPSorcery (WebRTC gerenciado, sem lib nativa).
- **Codec:** Concentus (encoder Opus em C# puro).
- **Pipeline:** PCM → `OpusFrameAccumulator` → `OpusEncoderFactory` → `RtpPacketPacer` → `RTCPeerConnection.SendAudio`.
- **Sinalização:** `SignalingClient` sobre `ClientWebSocket`, mesmos tipos de
  mensagem do backend (`publisher.ready`, `viewer.ready`, `webrtc.offer`,
  `webrtc.answer`, `webrtc.ice_candidate`, etc.).
- **ICE servers:** consumidos de `GET /api/webrtc/ice-servers` (credenciais
  HMAC de curta duração, já implementado no backend).

Como desktop e backend são repositórios distintos, **não há reaproveitamento
direto de pacote** (nem submódulo, nem NuGet privado — decisão explícita para
não sobre-engenhar uma feature de teste). O pipeline é **portado/reescrito**
dentro do novo projeto `SonicRelay.Infrastructure.VirtualPublisher`,
referenciando os mesmos pacotes NuGet (SIPSorcery, Concentus) diretamente.

Trade-off aceito: duplicação de lógica entre desktop e backend. Se o pipeline
de encode do desktop evoluir, o da rádio pública não acompanha
automaticamente — aceitável dado o objetivo de destravar testes rapidamente.
Uma extração futura para pacote NuGet compartilhado fica fora de escopo
deste design.

### 4. Publisher virtual conecta como um device real, via loopback

`PublicRoomPublisherService` (um `BackgroundService`, mesmo padrão de
`DataRetentionService`/`SessionCleanupService` já existentes em
`services/SonicRelay.Api/Services/`) conecta em `ws://localhost/ws/signaling`
como um `ClientWebSocket` comum, autenticado com uma **device identity de
sistema** dedicada (seedada/registrada automaticamente quando a feature está
habilitada).

**Por quê:** isso evita qualquer mudança no `SignalingWebSocketEndpoint` ou no
`IConnectionRegistry` existentes — do ponto de vista do resto do sistema, o
publisher virtual é indistinguível de um publisher real. Minimiza risco sobre
código já testado em produção.

### 5. Fonte de áudio: MP3 do volume montado

- Formato: **MP3** apenas (decodificação via biblioteca gerenciada, ex.
  NLayer — sem dependência nativa, consistente com o resto do stack).
- Ordem: **alfabética** pelo nome do arquivo.
- Repetição: **loop infinito** — ao terminar a última faixa, recomeça da
  primeira. A rádio nunca fica muda enquanto estiver habilitada e houver pelo
  menos um arquivo válido.
- Local dos arquivos: diretório **no host (VPS)**, montado como bind mount
  **somente leitura** dentro do container. A aplicação só enxerga o caminho
  interno do container (`/app/tracks`), nunca o caminho do host — trocar as
  faixas é uma operação de arquivo na VPS, sem rebuild/redeploy da imagem.

### 6. Limite de participantes e comportamento ao lotar

- `MaxViewers = 20` (configurável via env var, default 20).
- Reaproveita o **mesmo mecanismo já existente** em `SessionEndpoints.cs`
  (checagem de `MaxViewers` por sessão) — o 21º viewer recebe **409 Conflict
  "sala cheia"**, idêntico ao comportamento atual de uma sessão normal cheia.
  Sem fila de espera.

### 7. Autenticação: reaproveita device token existente

Entrar na sala pública exige o mesmo `Authorization: Bearer <deviceAccessToken>`
que qualquer join de viewer hoje. Não há fluxo de autenticação "leve"/anônimo
novo — o app Flutter já passa por esse fluxo antes de poder ouvir.

### 8. Feature flag simples (sem Microsoft.FeatureManagement)

Liga/desliga via configuração lida uma vez no startup (`IConfiguration`),
seguindo o mesmo padrão de flag de config dos `BackgroundService` existentes
— não usa `Microsoft.FeatureManagement`, que é voltado para toggling dinâmico
em runtime (rollout percentual, targeting, App Configuration), desnecessário
aqui já que a mudança de configuração é feita via variável de ambiente do
docker-compose e requer restart do container.

Quando desabilitado (`PublicRoom:Enabled=false`), `PublicRoomPublisherService`
retorna imediatamente no início do `ExecuteAsync` — nenhuma sessão é criada,
nenhuma peer connection é aberta, nenhum arquivo é lido. Custo de CPU/RAM
desprezível (early-return, sem loop rodando).

## Componentes novos

| Componente | Local | Responsabilidade |
|---|---|---|
| `SonicRelay.Infrastructure.VirtualPublisher` (novo projeto) | solution `dotnet_SonicRelay` | Pipeline de áudio: `Mp3TrackSource`, wrapper de peer connection SIPSorcery, encoder Opus (Concentus), `RtpPacketPacer` |
| `PublicRoomPublisherService : BackgroundService` | `services/SonicRelay.Api/Services/` | Orquestra: garante sessão pública, conecta como publisher via loopback, roda o loop de reprodução |
| Device identity de sistema | seed automático no startup | Identidade usada pelo publisher virtual para autenticar como qualquer device publisher |
| `GET /api/public-room` (novo endpoint) | `Endpoints/PublicRoomEndpoints.cs` | Descoberta **+ auto-pareamento**: retorna `{ enabled, sessionId, maxViewers }` e, como efeito colateral idempotente, garante um `DevicePairing` `Active` entre o device chamador e o device do publisher virtual (get-or-create). Requer o mesmo auth de device já usado pelos demais endpoints de sessão. |

> **Correção pós-design (achada ao mapear o código real):** `JoinByIdAsync`
> (`SessionEndpoints.cs`) dispensa o **código**, mas continua exigindo um
> `DevicePairing` com `Status = Active` entre o device do viewer e o device
> do publisher (`HasActivePairingAsync`) — é essa pairing que autoriza o
> join, não a ausência de código. Sem isso, todo join na rádio pública
> retornaria `403 not_paired`. A correção é o auto-pareamento acima: reusa o
> mesmo modelo `DevicePairing` que o pareamento manual via QR code já usa
> (`PairingEndpoints.CompleteAsync`), então nada em `SessionEndpoints.cs` ou
> `PairingEndpoints.cs` precisa mudar — o publisher virtual só passa a ter
> pairings criadas automaticamente em vez de via challenge/QR.

## Fluxo de dados

1. **Startup** com `PublicRoom:Enabled=true`: `PublicRoomPublisherService`
   garante que a `StreamSession` pública existe (id/slug fixo, ex.
   `public-radio`, `MaxViewers` configurado, sem expiração).
2. Serviço conecta em `/ws/signaling` como publisher (device de sistema),
   enviando `publisher.ready`.
3. **Loop de reprodução:** próximo MP3 (ordem alfabética, reinicia no fim) →
   decodifica em PCM → converte/encoda em Opus → envia via RTP para cada
   peer connection ativa.
4. **Viewer entra:** Flutter chama `GET /api/public-room` para descobrir o
   `sessionId` ativo (o endpoint também garante o `DevicePairing` ativo com
   o publisher virtual, get-or-create) → chama `joinById` nessa sessão (sem
   código) → conecta
   em `/ws/signaling` → `viewer.ready` → publisher virtual detecta
   o novo participante, abre uma peer connection SIPSorcery dedicada, envia
   `webrtc.offer` → handshake padrão (`webrtc.answer`, ICE candidates) →
   viewer passa a receber áudio a partir do ponto atual.
5. **Sala cheia:** mesmo check de `MaxViewers` existente → 409.
6. **Viewer desconecta:** fluxo existente de `participant.disconnected` +
   `SessionCleanupService` fecha a peer connection correspondente.

## Configuração via docker-compose

```yaml
# deploy/docker-compose.prod.yml
services:
  api:
    environment:
      PUBLICROOM__ENABLED: "${PUBLICROOM_ENABLED:-false}"
      PUBLICROOM__TRACKSPATH: "/app/tracks"
      PUBLICROOM__MAXVIEWERS: "${PUBLICROOM_MAXVIEWERS:-20}"
    volumes:
      - ${PUBLICROOM_TRACKS_HOST_PATH}:/app/tracks:ro
```

```bash
# .env (na VPS)
PUBLICROOM_ENABLED=true
PUBLICROOM_MAXVIEWERS=20
PUBLICROOM_TRACKS_HOST_PATH=/srv/sonicrelay/tracks
```

O diretório `/srv/sonicrelay/tracks` (exemplo) vive na VPS; o container só
lê (`:ro`), nunca escreve nem duplica os arquivos para dentro da imagem.
Trocar/adicionar faixas é uma operação de arquivo no host, sem rebuild.

## Tratamento de erros

- **MP3 corrompido/ilegível:** log + pula para o próximo arquivo, não
  derruba o loop de reprodução.
- **Diretório de tracks vazio ou inexistente:** log de warning, serviço fica
  "idle" (sem tocar nada) até haver ao menos um arquivo válido; a API sobe
  normalmente.
- **Falha ao conectar no signaling (loopback indisponível, etc.):** retry
  com backoff, mesmo padrão defensivo (try/catch por tick, não derruba o
  host) já usado em `DataRetentionService`/`SessionCleanupService`.
- **Viewer desconecta:** reaproveita o fluxo existente de
  `participant.disconnected`/cleanup.

## Testes

- Unit tests de `Mp3TrackSource`: ordenação alfabética, loop infinito, skip
  de arquivo inválido.
- Unit tests do pipeline de encode (PCM → Opus → RTP), espelhando os padrões
  de teste já usados no app desktop, se existirem.
- Teste de integração/manual: subir o compose local com uma pasta de tracks
  de teste, abrir o app Flutter, confirmar áudio contínuo e o `409` ao
  estourar 20 viewers simultâneos.

## Fora de escopo

- Suporte a outros formatos além de MP3.
- Fila de espera para viewers além do limite de 20.
- Autenticação "leve"/anônima para a sala pública (mantém device token
  existente).
- Extração do pipeline WebRTC/Opus para pacote NuGet compartilhado entre
  desktop e backend.
- Suporte a viewers no app desktop para essa sala pública (hoje só o Flutter
  é viewer).
- Toggle dinâmico em runtime (`Microsoft.FeatureManagement`) — troca de
  configuração exige restart do container, o que é aceitável dado que tudo
  é configurado via docker-compose/env vars.
