using System.Collections.Concurrent;
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
///
/// The outer loop follows the same house pattern as DataRetentionService /
/// SessionCleanupService: nothing that can go wrong here (a DB blip at boot, the
/// signaling connection dropping, a decode failure) is allowed to escape
/// ExecuteAsync, because an unhandled exception there is host-fatal
/// (BackgroundServiceExceptionBehavior.StopHost, the ASP.NET Core default, and
/// nothing in this codebase overrides it). Any non-cancellation failure is logged
/// and the whole connect+stream sequence is retried after a short delay, so the
/// radio recovers on its own instead of staying dead until the process restarts.
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
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    // ConcurrentDictionary, not Dictionary: AddViewerAsync/RemoveViewerAsync run on the
    // un-awaited receive-loop task while the frame-send loop concurrently enumerates
    // .Values — a plain Dictionary is not safe for that and can throw mid-iteration.
    private readonly ConcurrentDictionary<Guid, VirtualPublisherPeerConnection> peersByParticipantId = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Public radio room is disabled; PublicRoomPublisherService is a no-op");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Public radio room publisher failed; retrying in {RetryDelaySeconds}s",
                    ReconnectDelay.TotalSeconds);
            }

            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One attempt at seeding, minting a token, connecting to signaling and streaming until
    /// either cancellation, or something goes wrong (connect failure, the signaling receive
    /// loop dying because the server dropped the socket, or an exception from the streaming
    /// loop). Any of the latter returns/throws back to <see cref="ExecuteAsync"/>'s retry loop,
    /// which reconnects from scratch — re-seeding is idempotent and re-minting a token is cheap.
    /// </summary>
    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
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

        await signaling.ConnectAsPublisherAsync(PublicRoomSeeder.PublicSessionId, stoppingToken);

        var decoder = new NLayerMp3Decoder();
        var trackLogger = scope.ServiceProvider.GetRequiredService<ILogger<Mp3TrackSource>>();
        var tracks = new Mp3TrackSource(options.Value.TracksPath, decoder, trackLogger);

        // A linked token lets either side promptly stop the other: if the receive loop dies
        // (server dropped the socket) the streaming loop must not keep running forever in the
        // background while ExecuteAsync's retry loop reconnects from scratch, and vice versa.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var receiveLoop = signaling.RunReceiveLoopAsync(linkedCts.Token);
        var streamTask = StreamTracksAsync(tracks, linkedCts.Token);

        try
        {
            var completed = await Task.WhenAny(streamTask, receiveLoop);
            if (!linkedCts.IsCancellationRequested) await linkedCts.CancelAsync();

            await AwaitQuietlyAsync(streamTask);
            await AwaitQuietlyAsync(receiveLoop);

            // RunReceiveLoopAsync only returns (rather than staying open until cancellation) when
            // the socket was closed by the server/network — that's a lost connection, not a clean
            // shutdown, unless we were cancelled at the same moment.
            if (completed == receiveLoop && !stoppingToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "Public radio room signaling connection was lost while streaming.");
            }
        }
        finally
        {
            // Best-effort per peer: one peer's DisposeAsync throwing must not stop the rest from
            // being cleaned up, nor prevent the dictionary clear below.
            foreach (var peer in peersByParticipantId.Values)
            {
                try
                {
                    await peer.DisposeAsync();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Failed to dispose public room viewer peer connection for {ViewerParticipantId} during cleanup",
                        peer.ViewerParticipantId);
                }
            }
            peersByParticipantId.Clear();
        }
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected: this task was stopped by the linked cancellation above, not a real fault.
        }
    }

    /// <summary>
    /// Decodes and sends frames at roughly real-time speed. Without this, decoding runs at full
    /// CPU speed and floods RtpPacketPacer's ~200ms buffer far faster than audio can play,
    /// causing it to drop the vast majority of frames before they ever reach a viewer. This is a
    /// simple software audio clock: it tracks how much audio has been handed off so far and
    /// sleeps off the difference against a wall-clock start, no drift-correction needed for an
    /// MP3 playlist loop.
    /// </summary>
    private async Task StreamTracksAsync(Mp3TrackSource tracks, CancellationToken stoppingToken)
    {
        var clockStart = time.GetTimestamp();
        var playedDuration = TimeSpan.Zero;

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
            foreach (var peer in peersByParticipantId.Values)
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

            if (frame.Channels > 0 && frame.SampleRate > 0)
            {
                var samplesPerChannel = frame.Samples.Length / frame.Channels;
                playedDuration += TimeSpan.FromSeconds(samplesPerChannel / (double)frame.SampleRate);
            }

            var delay = playedDuration - time.GetElapsedTime(clockStart);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }
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
                // SignalingWebSocketEndpoint.HandleDisconnectAsync broadcasts one of these to every
                // other participant (including this publisher) whenever a viewer's signaling
                // socket drops: "participant.disconnected" when a reconnect grace period starts,
                // "session.left" once the participant is finalized as gone (either immediately, or
                // after the grace period elapses without a reconnect). Both carry
                // `{ participantId }` as payload with `from` set to the departed participant — see
                // SignalingWebSocketEndpoint.cs. Neither was previously handled, which permanently
                // leaked that viewer's peer connection (RTP pacer task, Opus encoder, ICE sockets)
                // and left a stale entry in peersByParticipantId that blocked both new viewers
                // (once EffectiveMaxViewers had ever been reached cumulatively) and that same
                // viewer's own reconnect (AddViewerAsync's ContainsKey short-circuits on the stale
                // entry). Disposing and removing the peer here for both message types lets a
                // grace-period reconnect (which typically re-negotiates WebRTC from the browser
                // anyway) and a final departure both free the slot and clear the way for a fresh
                // offer on the next "viewer.ready".
                case "session.left":
                case "participant.disconnected":
                    await RemoveViewerAsync(fromParticipantId);
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

    private async Task RemoveViewerAsync(Guid viewerParticipantId)
    {
        if (!peersByParticipantId.TryRemove(viewerParticipantId, out var peer)) return;
        try
        {
            await peer.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Failed to dispose public room viewer peer connection for {ViewerParticipantId} after disconnect",
                viewerParticipantId);
        }
    }

    private Uri ResolveLoopbackBaseUrl()
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        return address is not null ? new Uri(address) : new Uri("http://localhost:8080");
    }
}
