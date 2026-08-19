using Microsoft.Extensions.Logging;

namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>One decoded chunk of interleaved S16 PCM samples, plus the sample rate and
/// channel count they were decoded at so downstream resampling/mixing can be correct.</summary>
public readonly record struct Mp3Frame(short[] Samples, int SampleRate, int Channels);

/// <summary>Decodes one audio file into a sequence of interleaved S16 PCM frames.</summary>
public interface IMp3Decoder
{
    IEnumerable<Mp3Frame> DecodeFrames(string filePath);
}

/// <summary>
/// Plays every <c>*.mp3</c> file in a directory in alphabetical order, forever. A
/// file that fails to decode is logged and skipped for that pass; the loop moves
/// on to the next file rather than stopping the radio. An empty or missing
/// directory, or a pass where every file failed to decode, produces no frames —
/// the source logs a warning and waits <see cref="IdleRetryDelay"/> before
/// re-scanning, rather than terminating or hot-spinning the CPU. Iteration only
/// ends when the supplied <see cref="CancellationToken"/> is cancelled.
/// </summary>
public sealed class Mp3TrackSource(string directoryPath, IMp3Decoder decoder, ILogger<Mp3TrackSource> logger)
{
    private static readonly TimeSpan IdleRetryDelay = TimeSpan.FromSeconds(5);

    public IEnumerable<Mp3Frame> ReadForever(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var files = ListTracksSorted();
            var yieldedAny = false;

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                IEnumerator<Mp3Frame>? frames;
                try
                {
                    frames = decoder.DecodeFrames(file).GetEnumerator();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Skipping unreadable track {TrackPath}", file);
                    continue;
                }

                using (frames)
                {
                    while (true)
                    {
                        Mp3Frame frame;
                        try
                        {
                            if (!frames.MoveNext()) break;
                            frame = frames.Current;
                        }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception, "Skipping unreadable track {TrackPath}", file);
                            break;
                        }
                        yieldedAny = true;
                        yield return frame;
                    }
                }
            }

            if (!yieldedAny)
            {
                // Nothing playable this pass (empty/missing directory, or every file failed to
                // decode) — back off instead of hot-spinning the CPU and flooding logs, and keep
                // checking: a valid file dropped in later must be picked up without a restart.
                logger.LogWarning("No playable *.mp3 files found in {DirectoryPath}; idling", directoryPath);
                cancellationToken.WaitHandle.WaitOne(IdleRetryDelay);
            }
        }
    }

    private List<string> ListTracksSorted()
    {
        if (!Directory.Exists(directoryPath)) return [];
        return Directory.EnumerateFiles(directoryPath, "*.mp3")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToList();
    }
}
