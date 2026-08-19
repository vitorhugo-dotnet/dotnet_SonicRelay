using Microsoft.Extensions.Logging;

namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>Decodes one audio file into a sequence of interleaved S16 PCM frames.</summary>
public interface IMp3Decoder
{
    IEnumerable<short[]> DecodeFrames(string filePath);
}

/// <summary>
/// Plays every <c>*.mp3</c> file in a directory in alphabetical order, forever. A
/// file that fails to decode is logged and skipped for that pass; the loop moves
/// on to the next file rather than stopping the radio. An empty or missing
/// directory yields no frames at all (the caller treats that as "idle").
/// </summary>
public sealed class Mp3TrackSource(string directoryPath, IMp3Decoder decoder, ILogger<Mp3TrackSource> logger)
{
    public IEnumerable<short[]> ReadForever(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var files = ListTracksSorted();
            if (files.Count == 0) yield break;

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                IEnumerator<short[]>? frames;
                try
                {
                    frames = decoder.DecodeFrames(file).GetEnumerator();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Skipping unreadable track {TrackPath}", file);
                    continue;
                }

                while (true)
                {
                    short[] frame;
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
                    yield return frame;
                }
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
