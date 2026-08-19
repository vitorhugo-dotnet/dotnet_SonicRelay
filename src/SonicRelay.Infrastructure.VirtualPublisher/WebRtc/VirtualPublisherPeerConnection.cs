using Concentus.Structs;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SonicRelay.Infrastructure.VirtualPublisher.Audio;

namespace SonicRelay.Infrastructure.VirtualPublisher.WebRtc;

/// <summary>
/// One send-only Opus audio peer connection to a single public-room viewer.
/// Deliberately smaller than the desktop app's publisher connection: no RTCP
/// diagnostics and no ICE restart, since the spec scopes those out for the
/// public radio (see docs/superpowers/specs/2026-08-19-public-radio-room-design.md).
/// </summary>
public sealed class VirtualPublisherPeerConnection : IAsyncDisposable
{
    private const int SampleRate = 48000;
    private static readonly TimeSpan PacingLatencyBudget = TimeSpan.FromMilliseconds(200);

    private readonly RTCPeerConnection connection;
    private readonly OpusEncoder opusEncoder;
    private readonly OpusFrameAccumulator accumulator;
    private readonly RtpPacketPacer pacer;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly byte[] encodeBuffer = new byte[4000];
    private readonly int samplesPerChannel;
    private volatile bool formatNegotiated;
    private volatile bool connected;
    private bool disposed;

    public VirtualPublisherPeerConnection(
        string viewerParticipantId, RTCPeerConnection connection, AudioQualityProfile? profile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerParticipantId);
        ViewerParticipantId = viewerParticipantId;
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));

        var quality = profile ?? AudioQualityProfile.Default;
        quality.Validate();
        var channels = quality.Channels;
        var bitrate = quality.OpusBitrateKbps * 1000;
        var stereo = channels == 2 ? 1 : 0;
        samplesPerChannel = SampleRate * quality.FrameDurationMs / 1000;
        accumulator = new OpusFrameAccumulator(SampleRate, channels, quality.FrameDurationMs);

        var opusFormat = new AudioFormat(
            AudioCodecsEnum.OPUS, 111, SampleRate, channels,
            $"useinbandfec=1;stereo={stereo};sprop-stereo={stereo};maxaveragebitrate={bitrate};maxplaybackrate=48000");
        this.connection.addTrack(new MediaStreamTrack(opusFormat, MediaStreamStatusEnum.SendOnly));

        opusEncoder = OpusEncoderFactory.Create(quality);
        pacer = new RtpPacketPacer(
            TimeSpan.FromMilliseconds(quality.FrameDurationMs), PacingLatencyBudget,
            packet => this.connection.SendAudio((uint)samplesPerChannel, packet));

        this.connection.OnAudioFormatsNegotiated += OnAudioFormatsNegotiated;
        this.connection.onicecandidate += OnIceCandidate;
        this.connection.onconnectionstatechange += OnConnectionStateChanged;
    }

    public string ViewerParticipantId { get; }

    public event Func<WebRtcIceCandidate, CancellationToken, Task>? LocalIceCandidateReady;

    public async Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var offer = connection.createOffer(null)
            ?? throw new InvalidOperationException("SIPSorcery could not create an SDP offer.");
        await connection.setLocalDescription(offer).ConfigureAwait(false);
        return new WebRtcSessionDescription("offer", offer.sdp);
    }

    public Task ApplyAnswerAsync(WebRtcSessionDescription answer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ThrowIfDisposed();
        var result = connection.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answer.Sdp
        });
        return result == SetDescriptionResultEnum.OK
            ? Task.CompletedTask
            : throw new InvalidOperationException($"The WebRTC answer was rejected: {result}.");
    }

    public Task AddRemoteIceCandidateAsync(WebRtcIceCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ThrowIfDisposed();
        connection.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate.Candidate,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = (ushort)(candidate.SdpMLineIndex ?? 0)
        });
        return Task.CompletedTask;
    }

    public async Task SendAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (disposed) return;
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed) return;
            if (!connected || !formatNegotiated)
            {
                accumulator.Clear();
                pacer.Clear();
                return;
            }

            var samples = PcmAudioConverter.ToS16(frame.Data.Span, WebRtcSourceSampleFormat.Pcm16);
            accumulator.Append(samples, frame.SampleRate, frame.ChannelCount);
            while (accumulator.TryTakeFrame(out var pcm))
            {
                var length = opusEncoder.Encode(pcm, samplesPerChannel, encodeBuffer, encodeBuffer.Length);
                if (length <= 0) continue;
                pacer.Enqueue(encodeBuffer[..length]);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    private void OnAudioFormatsNegotiated(List<AudioFormat> formats)
    {
        if (formats.Any(format => string.Equals(format.FormatName, "OPUS", StringComparison.OrdinalIgnoreCase)))
        {
            formatNegotiated = true;
        }
    }

    private void OnIceCandidate(RTCIceCandidate? candidate)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.candidate)) return;
        var handlers = LocalIceCandidateReady;
        if (handlers is null) return;
        var value = candidate.candidate.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase)
            ? candidate.candidate
            : $"candidate:{candidate.candidate}";
        var sdpMid = string.IsNullOrEmpty(candidate.sdpMid) ? null : candidate.sdpMid;
        var payload = new WebRtcIceCandidate(value, sdpMid, candidate.sdpMLineIndex);
        _ = Task.Run(() => handlers.Invoke(payload, CancellationToken.None));
    }

    private void OnConnectionStateChanged(RTCPeerConnectionState next) =>
        connected = next == RTCPeerConnectionState.connected;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
        }
        finally
        {
            sendLock.Release();
        }

        connection.OnAudioFormatsNegotiated -= OnAudioFormatsNegotiated;
        connection.onicecandidate -= OnIceCandidate;
        connection.onconnectionstatechange -= OnConnectionStateChanged;
        await pacer.DisposeAsync().ConfigureAwait(false);
        try
        {
            connection.close();
        }
        catch
        {
            // Closing an already-failed transport must not throw out of dispose.
        }
        sendLock.Dispose();
    }
}
