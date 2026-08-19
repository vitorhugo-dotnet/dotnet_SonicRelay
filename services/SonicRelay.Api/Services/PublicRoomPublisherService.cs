using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using SonicRelay.Infrastructure.Persistence;
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using SonicRelay.Infrastructure.VirtualPublisher.WebRtc;
using SIPSorcery.Net;

namespace SonicRelay.Api.Services;

/// <summary>
/// Public radio room virtual publisher (docs/superpowers/specs/2026-08-19-public-radio-room-design.md):
/// seeds the well-known publisher device/session, connects to this API's own
/// /ws/signaling as a headless client, and streams the looping MP3 playlist to
/// every viewer who joins. Disabled by default; a false PublicRoom:Enabled means
/// this does nothing at all (same pattern as DataRetentionService).
/// </summary>
public sealed class PublicRoomPublisherService(
    IServiceScopeFactory scopeFactory,
    IOptions<PublicRoomOptions> options,
    PublicRoomSeeder seeder,
    DeviceCredentialService credentials,
    IServer server,
    TimeProvider time,
    ILogger<PublicRoomPublisherService> logger) : BackgroundService
{
    private readonly Dictionary<Guid, VirtualPublisherPeerConnection> peersByParticipantId = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Public radio room is disabled; PublicRoomPublisherService is a no-op");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await seeder.EnsureSeededAsync(db, time, stoppingToken);
        // EnsureSeededAsync only sets MaxViewers on first creation (see its comment); every
        // subsequent startup must still push the currently configured cap onto the row, since
        // SessionEndpoints' join-time enforcement (session.MaxViewers, not PublicRoomOptions)
        // is what actually gates HTTP joins. Without this, changing PublicRoom:MaxViewers after
        // the row already exists would silently have no effect on real viewer admission.
        if (session.MaxViewers != options.Value.EffectiveMaxViewers)
        {
            session.MaxViewers = options.Value.EffectiveMaxViewers;
            await db.SaveChangesAsync(stoppingToken);
        }
        var (token, _) = await seeder.IssuePublisherTokenAsync(db, credentials, time, stoppingToken);

        var baseUrl = ResolveLoopbackBaseUrl();
        await using var signaling = new PublicRoomSignalingClient(baseUrl, token);
        signaling.MessageReceived += (from, type, payload, ct) => HandleSignalingMessageAsync(signaling, from, type, payload, ct);

        try
        {
            await signaling.ConnectAsPublisherAsync(PublicRoomSeeder.PublicSessionId, stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Public radio room could not connect to its own signaling endpoint");
            return;
        }

        var decoder = new NLayerMp3Decoder();
        var trackLogger = scope.ServiceProvider.GetRequiredService<ILogger<Mp3TrackSource>>();
        var tracks = new Mp3TrackSource(options.Value.TracksPath, decoder, trackLogger);

        var receiveLoop = signaling.RunReceiveLoopAsync(stoppingToken);
        try
        {
            foreach (var frame in tracks.ReadForever(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;
                var bytes = new byte[frame.Samples.Length * 2];
                Buffer.BlockCopy(frame.Samples, 0, bytes, 0, bytes.Length);
                // frame.SampleRate/Channels come from the MP3's real native format
                // (NLayerMp3Decoder reports it), not a hardcoded 48kHz/stereo assumption —
                // OpusFrameAccumulator (inside VirtualPublisherPeerConnection) resamples
                // and up/down-mixes from whatever this actually is.
                var audioFrame = new WebRtcAudioFrame(bytes, frame.SampleRate, frame.Channels);
                foreach (var peer in peersByParticipantId.Values.ToList())
                {
                    try
                    {
                        await peer.SendAudioFrameAsync(audioFrame, stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception,
                            "Failed to send an audio frame to public room viewer {ViewerParticipantId}",
                            peer.ViewerParticipantId);
                    }
                }
            }
        }
        finally
        {
            foreach (var peer in peersByParticipantId.Values) await peer.DisposeAsync();
            peersByParticipantId.Clear();
            await receiveLoop;
        }
    }

    private async Task HandleSignalingMessageAsync(
        PublicRoomSignalingClient signaling, Guid fromParticipantId, string type,
        System.Text.Json.JsonElement payload, CancellationToken ct)
    {
        try
        {
            switch (type)
            {
                case "viewer.ready":
                    await AddViewerAsync(signaling, fromParticipantId, ct);
                    break;
                case "webrtc.answer":
                    if (peersByParticipantId.TryGetValue(fromParticipantId, out var answerPeer))
                    {
                        var sdp = payload.GetProperty("sdp").GetString()!;
                        await answerPeer.ApplyAnswerAsync(new WebRtcSessionDescription("answer", sdp), ct);
                    }
                    break;
                case "webrtc.ice_candidate":
                    if (peersByParticipantId.TryGetValue(fromParticipantId, out var icePeer))
                    {
                        var candidate = payload.GetProperty("candidate").GetString()!;
                        var sdpMid = payload.TryGetProperty("sdpMid", out var m) ? m.GetString() : null;
                        var sdpMLineIndex = payload.TryGetProperty("sdpMLineIndex", out var idx) ? idx.GetInt32() : (int?)null;
                        await icePeer.AddRemoteIceCandidateAsync(
                            new WebRtcIceCandidate(candidate, sdpMid, sdpMLineIndex), ct);
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Error handling {MessageType} from viewer {ViewerParticipantId}",
                type, fromParticipantId);
        }
    }

    private async Task AddViewerAsync(PublicRoomSignalingClient signaling, Guid viewerParticipantId, CancellationToken ct)
    {
        if (peersByParticipantId.ContainsKey(viewerParticipantId)) return;
        if (peersByParticipantId.Count >= options.Value.EffectiveMaxViewers) return; // SessionEndpoints already enforced this at join time

        var connection = new RTCPeerConnection();
        var peer = new VirtualPublisherPeerConnection(viewerParticipantId.ToString(), connection);
        peer.LocalIceCandidateReady += async (candidate, candidateCt) =>
            await signaling.SendAsync(viewerParticipantId, "webrtc.ice_candidate",
                new { candidate = candidate.Candidate, sdpMid = candidate.SdpMid, sdpMLineIndex = candidate.SdpMLineIndex },
                candidateCt);

        peersByParticipantId[viewerParticipantId] = peer;
        var offer = await peer.CreateOfferAsync(ct);
        await signaling.SendAsync(viewerParticipantId, "webrtc.offer", new { sdp = offer.Sdp }, ct);
    }

    private Uri ResolveLoopbackBaseUrl()
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        return address is not null ? new Uri(address) : new Uri("http://localhost:8080");
    }
}
