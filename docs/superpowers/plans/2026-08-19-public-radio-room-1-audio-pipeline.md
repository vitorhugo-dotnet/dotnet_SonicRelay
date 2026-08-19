# Public Radio Room — Plan 1: Virtual Publisher Audio Pipeline

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a new `SonicRelay.Infrastructure.VirtualPublisher` project that can decode a directory of MP3 files, in alphabetical order, on infinite loop, and send them as a WebRTC Opus audio track to one or more SIPSorcery peer connections.

**Architecture:** Ports the self-contained pieces of the desktop app's proven WebRTC/Opus pipeline (`OpusFrameAccumulator`, `OpusEncoderFactory`, `RtpPacketPacer`, `PcmAudioConverter` from `desktop_dotnet_SonicRelay/src/SonicRelay.Windows.WebRtc`) into the backend solution, rewritten (not referenced across repos — see the design spec). Adds a new, deliberately smaller `VirtualPublisherPeerConnection` (create offer / apply answer / add remote ICE / send audio — no RTCP diagnostics, no ICE restart, out of scope per spec) and an `Mp3TrackSource` that decodes MP3 via NLayer. This plan produces a library with no host process yet — Plan 2 wires it into a `BackgroundService` and the API's endpoints.

**Tech Stack:** .NET 10, SIPSorcery 10.0.14, Concentus 2.2.2, NLayer (managed MP3 decoder), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-19-public-radio-room-design.md`

## Global Constraints

- Target framework: `net10.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` (match every other project in the solution).
- Test framework: xUnit 2.9.3 + `Microsoft.NET.Test.Sdk` 17.14.1 (match `tests/SonicRelay.SignalingClient.Tests`).
- No RTCP diagnostics, no ICE restart, no adaptive bitrate — the spec explicitly scopes those out. `VirtualPublisherPeerConnection` is a deliberately smaller class than the desktop's `SipSorceryPeerConnection`.
- Fixed audio profile: stereo, 128 kbps, 20 ms frames, 48 kHz (matches the desktop's `AudioQualityProfile.High` / `Default`) — no user-selectable quality.
- Every ported file changes namespace to `SonicRelay.Infrastructure.VirtualPublisher.*` but keeps the original public API shape so the porting is mechanical and low-risk.

---

## File Structure

- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/SonicRelay.Infrastructure.VirtualPublisher.csproj`
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/AudioQualityProfile.cs` — minimal ported profile record (just what the encoder needs)
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/PcmAudioConverter.cs` — ported verbatim (namespace only change)
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/OpusFrameAccumulator.cs` — ported verbatim
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/OpusEncoderFactory.cs` — ported, references the new `AudioQualityProfile`
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/RtpPacketPacer.cs` — ported verbatim
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/Mp3TrackSource.cs` — new: enumerates/orders/loops MP3 files; decoding is behind an injectable `IMp3Decoder` so ordering/looping is unit-testable without real MP3 bytes
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/NLayerMp3Decoder.cs` — new: the real `IMp3Decoder` implementation backed by NLayer
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/WebRtc/WebRtcContracts.cs` — new: trimmed-down contracts (`WebRtcSessionDescription`, `WebRtcIceCandidate`, `WebRtcAudioFrame`)
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/WebRtc/VirtualPublisherPeerConnection.cs` — new: minimal SIPSorcery wrapper
- Create: `tests/SonicRelay.VirtualPublisher.Tests/SonicRelay.VirtualPublisher.Tests.csproj`
- Create: `tests/SonicRelay.VirtualPublisher.Tests/OpusFrameAccumulatorTests.cs`
- Create: `tests/SonicRelay.VirtualPublisher.Tests/OpusEncoderFactoryTests.cs`
- Create: `tests/SonicRelay.VirtualPublisher.Tests/Mp3TrackSourceTests.cs`
- Modify: `SonicRelay.sln` — add both new projects

---

### Task 1: Create the `SonicRelay.Infrastructure.VirtualPublisher` project

**Files:**
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/SonicRelay.Infrastructure.VirtualPublisher.csproj`
- Modify: `SonicRelay.sln`

**Interfaces:**
- Produces: an empty, compiling class library project referenced by the solution, ready for the source files added in later tasks.

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Concentus" Version="2.2.2" />
    <PackageReference Include="SIPSorcery" Version="10.0.14" />
    <PackageReference Include="NLayer" Version="1.16.0" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add the project to the solution**

Run: `dotnet sln SonicRelay.sln add src/SonicRelay.Infrastructure.VirtualPublisher/SonicRelay.Infrastructure.VirtualPublisher.csproj`

- [ ] **Step 3: Verify it builds**

Run: `dotnet build src/SonicRelay.Infrastructure.VirtualPublisher/SonicRelay.Infrastructure.VirtualPublisher.csproj`
Expected: Build succeeded (project has no source files yet, that's fine).

- [ ] **Step 4: Commit**

```bash
git add SonicRelay.sln src/SonicRelay.Infrastructure.VirtualPublisher/SonicRelay.Infrastructure.VirtualPublisher.csproj
git commit -m "Add SonicRelay.Infrastructure.VirtualPublisher project"
```

---

### Task 2: Create the test project

**Files:**
- Create: `tests/SonicRelay.VirtualPublisher.Tests/SonicRelay.VirtualPublisher.Tests.csproj`
- Modify: `SonicRelay.sln`

**Interfaces:**
- Consumes: `SonicRelay.Infrastructure.VirtualPublisher.csproj` (Task 1)
- Produces: an empty, compiling xUnit test project referencing the new library.

- [ ] **Step 1: Create the test project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\SonicRelay.Infrastructure.VirtualPublisher\SonicRelay.Infrastructure.VirtualPublisher.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add to solution and verify it builds**

Run: `dotnet sln SonicRelay.sln add tests/SonicRelay.VirtualPublisher.Tests/SonicRelay.VirtualPublisher.Tests.csproj && dotnet build tests/SonicRelay.VirtualPublisher.Tests/SonicRelay.VirtualPublisher.Tests.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add SonicRelay.sln tests/SonicRelay.VirtualPublisher.Tests/SonicRelay.VirtualPublisher.Tests.csproj
git commit -m "Add SonicRelay.VirtualPublisher.Tests project"
```

---

### Task 3: Port `AudioQualityProfile` (minimal, fixed profile)

**Files:**
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/AudioQualityProfile.cs`
- Test: `tests/SonicRelay.VirtualPublisher.Tests/AudioQualityProfileTests.cs`

**Interfaces:**
- Produces: `SonicRelay.Infrastructure.VirtualPublisher.Audio.AudioQualityProfile` — record with `Id, DisplayName, Channels, OpusBitrateKbps, FrameDurationMs, SampleRateHz, ExpectedPacketLossPercent { get; init; }`, static `Default`, instance `Validate()` throwing `ArgumentException` on out-of-range values. Consumed by `OpusEncoderFactory` (Task 6) and `VirtualPublisherPeerConnection` (Task 9).

- [ ] **Step 1: Write the failing test**

```csharp
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class AudioQualityProfileTests
{
    [Fact]
    public void Default_is_stereo_128kbps_20ms_48khz()
    {
        var profile = AudioQualityProfile.Default;

        Assert.Equal(2, profile.Channels);
        Assert.Equal(128, profile.OpusBitrateKbps);
        Assert.Equal(20, profile.FrameDurationMs);
        Assert.Equal(48000, profile.SampleRateHz);
        profile.Validate(); // does not throw
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Validate_rejects_invalid_channel_count(int channels)
    {
        var profile = AudioQualityProfile.Default with { Channels = channels };
        Assert.Throws<ArgumentException>(profile.Validate);
    }

    [Fact]
    public void Validate_rejects_bitrate_out_of_range()
    {
        var profile = AudioQualityProfile.Default with { OpusBitrateKbps = 8 };
        Assert.Throws<ArgumentException>(profile.Validate);
    }

    [Fact]
    public void Validate_rejects_unsupported_frame_duration()
    {
        var profile = AudioQualityProfile.Default with { FrameDurationMs = 15 };
        Assert.Throws<ArgumentException>(profile.Validate);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter AudioQualityProfileTests`
Expected: FAIL to compile — `AudioQualityProfile` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>
/// Fixed Opus encode profile for the public radio's virtual publisher. Unlike the
/// desktop app there is no user-selectable quality — this is always
/// stereo/128kbps/20ms/48kHz, matching the desktop's "High" preset.
/// </summary>
public sealed record AudioQualityProfile(
    string Id,
    string DisplayName,
    int Channels,
    int OpusBitrateKbps,
    int FrameDurationMs,
    int SampleRateHz)
{
    public const int MinBitrateKbps = 16;
    public const int MaxBitrateKbps = 192;
    public const int FixedSampleRateHz = 48000;

    private static readonly int[] AllowedFrameDurationsMs = [10, 20, 40];

    public int ExpectedPacketLossPercent { get; init; } = 10;

    public static AudioQualityProfile Default { get; } =
        new("high", "High quality", 2, 128, 20, FixedSampleRateHz);

    public void Validate()
    {
        if (Channels is < 1 or > 2)
            throw new ArgumentException($"Channels must be 1 or 2, was {Channels}.", nameof(Channels));
        if (OpusBitrateKbps is < MinBitrateKbps or > MaxBitrateKbps)
            throw new ArgumentException(
                $"Opus bitrate must be between {MinBitrateKbps} and {MaxBitrateKbps} kbps, was {OpusBitrateKbps}.",
                nameof(OpusBitrateKbps));
        if (Array.IndexOf(AllowedFrameDurationsMs, FrameDurationMs) < 0)
            throw new ArgumentException(
                $"Frame duration must be 10, 20, or 40 ms, was {FrameDurationMs}.", nameof(FrameDurationMs));
        if (SampleRateHz != FixedSampleRateHz)
            throw new ArgumentException(
                $"Sample rate must be {FixedSampleRateHz} Hz, was {SampleRateHz}.", nameof(SampleRateHz));
        if (ExpectedPacketLossPercent is < 0 or > 100)
            throw new ArgumentException(
                $"Expected packet loss must be between 0 and 100 percent, was {ExpectedPacketLossPercent}.",
                nameof(ExpectedPacketLossPercent));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter AudioQualityProfileTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Infrastructure.VirtualPublisher/Audio/AudioQualityProfile.cs tests/SonicRelay.VirtualPublisher.Tests/AudioQualityProfileTests.cs
git commit -m "Port AudioQualityProfile (fixed stereo/128kbps profile)"
```

---

### Task 4: Port `PcmAudioConverter`

**Files:**
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/PcmAudioConverter.cs`
- Test: `tests/SonicRelay.VirtualPublisher.Tests/PcmAudioConverterTests.cs`

**Interfaces:**
- Produces: `SonicRelay.Infrastructure.VirtualPublisher.Audio.PcmAudioConverter.ToS16(ReadOnlySpan<byte>, WebRtcSourceSampleFormat)` returning `short[]`, and enum `WebRtcSourceSampleFormat { Pcm16, IeeeFloat32 }`. Consumed by `VirtualPublisherPeerConnection` (Task 9) to feed the frame accumulator.

- [ ] **Step 1: Write the failing test**

```csharp
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class PcmAudioConverterTests
{
    [Fact]
    public void ToS16_from_pcm16_returns_samples_unchanged()
    {
        short[] expected = [100, -200, 300];
        var bytes = new byte[expected.Length * 2];
        Buffer.BlockCopy(expected, 0, bytes, 0, bytes.Length);

        var result = PcmAudioConverter.ToS16(bytes, WebRtcSourceSampleFormat.Pcm16);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ToS16_from_float32_scales_and_clamps()
    {
        float[] input = [1.0f, -1.0f, 0.0f, 2.0f]; // 2.0f must clamp to 1.0f
        var bytes = new byte[input.Length * 4];
        Buffer.BlockCopy(input, 0, bytes, 0, bytes.Length);

        var result = PcmAudioConverter.ToS16(bytes, WebRtcSourceSampleFormat.IeeeFloat32);

        Assert.Equal([short.MaxValue, short.MinValue, (short)0, short.MaxValue], result);
    }

    [Fact]
    public void ToS16_from_empty_returns_empty()
    {
        Assert.Empty(PcmAudioConverter.ToS16(ReadOnlySpan<byte>.Empty, WebRtcSourceSampleFormat.Pcm16));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter PcmAudioConverterTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Runtime.InteropServices;

namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>Sample format of the raw bytes handed to <see cref="PcmAudioConverter.ToS16"/>.</summary>
public enum WebRtcSourceSampleFormat
{
    Pcm16,
    IeeeFloat32
}

/// <summary>Converts raw PCM buffers to the S16LE interleaved samples the Opus encoder consumes.</summary>
public static class PcmAudioConverter
{
    public static short[] ToS16(ReadOnlySpan<byte> data, WebRtcSourceSampleFormat format)
    {
        if (data.IsEmpty) return [];
        switch (format)
        {
            case WebRtcSourceSampleFormat.Pcm16:
                return MemoryMarshal.Cast<byte, short>(data[..(data.Length - data.Length % 2)]).ToArray();
            case WebRtcSourceSampleFormat.IeeeFloat32:
                var floats = MemoryMarshal.Cast<byte, float>(data[..(data.Length - data.Length % 4)]);
                var samples = new short[floats.Length];
                for (var i = 0; i < floats.Length; i++)
                {
                    var clamped = Math.Clamp(floats[i], -1f, 1f);
                    samples[i] = (short)Math.Round(clamped * short.MaxValue);
                }
                return samples;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter PcmAudioConverterTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Infrastructure.VirtualPublisher/Audio/PcmAudioConverter.cs tests/SonicRelay.VirtualPublisher.Tests/PcmAudioConverterTests.cs
git commit -m "Port PcmAudioConverter"
```

---

### Task 5: Port `OpusFrameAccumulator`

**Files:**
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/OpusFrameAccumulator.cs`
- Test: `tests/SonicRelay.VirtualPublisher.Tests/OpusFrameAccumulatorTests.cs`

**Interfaces:**
- Produces: `OpusFrameAccumulator(int targetSampleRate = 48000, int targetChannels = 2, int frameDurationMs = 20)` with `TargetFrameSize`, `Append(ReadOnlySpan<short>, int sampleRate, int channelCount)`, `TryTakeFrame(out short[] frame)`, `Clear()`. Consumed by `VirtualPublisherPeerConnection` (Task 9).

- [ ] **Step 1: Write the failing test**

```csharp
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class OpusFrameAccumulatorTests
{
    [Fact]
    public void TryTakeFrame_returns_false_when_not_enough_samples_buffered()
    {
        var accumulator = new OpusFrameAccumulator(targetSampleRate: 48000, targetChannels: 2, frameDurationMs: 20);
        accumulator.Append([1, 2, 3, 4], sampleRate: 48000, channelCount: 2); // far short of 960*2

        Assert.False(accumulator.TryTakeFrame(out _));
    }

    [Fact]
    public void TryTakeFrame_emits_exact_frame_size_at_matching_rate()
    {
        var accumulator = new OpusFrameAccumulator(targetSampleRate: 48000, targetChannels: 2, frameDurationMs: 20);
        var samplesPerChannel = 48000 * 20 / 1000; // 960
        var samples = new short[samplesPerChannel * 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)i;

        accumulator.Append(samples, sampleRate: 48000, channelCount: 2);

        Assert.True(accumulator.TryTakeFrame(out var frame));
        Assert.Equal(samplesPerChannel * 2, frame.Length);
        Assert.Equal(samples, frame);
        Assert.False(accumulator.TryTakeFrame(out _)); // buffer now empty
    }

    [Fact]
    public void Append_upmixes_mono_to_stereo_by_duplicating_each_sample()
    {
        var accumulator = new OpusFrameAccumulator(targetSampleRate: 48000, targetChannels: 2, frameDurationMs: 20);
        var samplesPerChannel = 48000 * 20 / 1000;
        var mono = new short[samplesPerChannel];
        for (var i = 0; i < mono.Length; i++) mono[i] = (short)(i + 1);

        accumulator.Append(mono, sampleRate: 48000, channelCount: 1);

        Assert.True(accumulator.TryTakeFrame(out var frame));
        Assert.Equal(samplesPerChannel * 2, frame.Length);
        Assert.Equal(mono[0], frame[0]);
        Assert.Equal(mono[0], frame[1]); // left == right for the first source sample
    }

    [Fact]
    public void Clear_discards_buffered_samples()
    {
        var accumulator = new OpusFrameAccumulator(targetSampleRate: 48000, targetChannels: 2, frameDurationMs: 20);
        accumulator.Append([1, 2, 3, 4], sampleRate: 48000, channelCount: 2);

        accumulator.Clear();

        Assert.False(accumulator.TryTakeFrame(out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter OpusFrameAccumulatorTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>
/// Buffers ragged decode chunks and emits exact frames of the configured duration
/// (default 20 ms -> 960 samples per channel at 48 kHz). Handles mono/stereo
/// up/down-mixing and linear resampling of arbitrary common source rates. Not
/// thread-safe; callers serialize access.
/// </summary>
public sealed class OpusFrameAccumulator
{
    private readonly int targetSampleRate;
    private readonly int targetChannels;
    private readonly int frameDurationMs;
    private readonly List<short> buffer = [];
    private int sourceSampleRate;
    private int sourceSamplesPerFramePerChannel;

    public OpusFrameAccumulator(int targetSampleRate = 48000, int targetChannels = 2, int frameDurationMs = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);
        if (targetChannels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(targetChannels));
        if (frameDurationMs is not (10 or 20 or 40))
            throw new ArgumentOutOfRangeException(nameof(frameDurationMs), "Frame duration must be 10, 20, or 40 ms.");
        this.targetSampleRate = targetSampleRate;
        this.targetChannels = targetChannels;
        this.frameDurationMs = frameDurationMs;
    }

    private int TargetSamplesPerFramePerChannel => targetSampleRate * frameDurationMs / 1000;

    public int TargetFrameSize => TargetSamplesPerFramePerChannel * targetChannels;

    public void Append(ReadOnlySpan<short> samples, int sampleRate, int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (sampleRate * frameDurationMs % 1000 != 0)
            throw new ArgumentException(
                $"Sample rate must yield a whole number of samples per {frameDurationMs} ms frame.", nameof(sampleRate));
        if (channelCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (sampleRate != sourceSampleRate)
        {
            buffer.Clear();
            sourceSampleRate = sampleRate;
            sourceSamplesPerFramePerChannel = sampleRate * frameDurationMs / 1000;
        }

        if (channelCount == targetChannels)
        {
            AppendRaw(samples);
        }
        else if (channelCount == 1)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                buffer.Add(samples[i]);
                buffer.Add(samples[i]);
            }
        }
        else
        {
            for (var i = 0; i + 1 < samples.Length; i += 2)
            {
                buffer.Add((short)((samples[i] + samples[i + 1]) / 2));
            }
        }
    }

    public bool TryTakeFrame(out short[] frame)
    {
        frame = [];
        if (sourceSampleRate == 0) return false;
        var neededSourceSamples = sourceSamplesPerFramePerChannel * targetChannels;
        if (buffer.Count < neededSourceSamples) return false;

        var source = buffer.GetRange(0, neededSourceSamples).ToArray();
        buffer.RemoveRange(0, neededSourceSamples);

        if (sourceSampleRate == targetSampleRate)
        {
            frame = source;
            return true;
        }

        frame = ResampleInterleaved(
            source, sourceSamplesPerFramePerChannel, TargetSamplesPerFramePerChannel, targetChannels);
        return true;
    }

    public void Clear()
    {
        buffer.Clear();
        sourceSampleRate = 0;
    }

    private void AppendRaw(ReadOnlySpan<short> samples)
    {
        buffer.EnsureCapacity(buffer.Count + samples.Length);
        foreach (var sample in samples) buffer.Add(sample);
    }

    private static short[] ResampleInterleaved(short[] source, int sourceFrames, int targetFrames, int channels)
    {
        var result = new short[targetFrames * channels];
        for (var frameIndex = 0; frameIndex < targetFrames; frameIndex++)
        {
            var position = (double)frameIndex * (sourceFrames - 1) / Math.Max(targetFrames - 1, 1);
            var lower = (int)position;
            var upper = Math.Min(lower + 1, sourceFrames - 1);
            var fraction = position - lower;
            for (var channel = 0; channel < channels; channel++)
            {
                var a = source[lower * channels + channel];
                var b = source[upper * channels + channel];
                result[frameIndex * channels + channel] = (short)Math.Round(a + (b - a) * fraction);
            }
        }
        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter OpusFrameAccumulatorTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Infrastructure.VirtualPublisher/Audio/OpusFrameAccumulator.cs tests/SonicRelay.VirtualPublisher.Tests/OpusFrameAccumulatorTests.cs
git commit -m "Port OpusFrameAccumulator"
```

---

### Task 6: Port `OpusEncoderFactory`

**Files:**
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/OpusEncoderFactory.cs`
- Test: `tests/SonicRelay.VirtualPublisher.Tests/OpusEncoderFactoryTests.cs`

**Interfaces:**
- Consumes: `AudioQualityProfile` (Task 3)
- Produces: `OpusEncoderFactory.Create(AudioQualityProfile profile)` returning a configured `Concentus.Structs.OpusEncoder`. Consumed by `VirtualPublisherPeerConnection` (Task 9).

- [ ] **Step 1: Write the failing test**

```csharp
using Concentus.Enums;
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class OpusEncoderFactoryTests
{
    [Fact]
    public void Create_configures_bitrate_and_resilience_from_the_profile()
    {
        var profile = AudioQualityProfile.Default; // stereo, 128 kbps

        var encoder = OpusEncoderFactory.Create(profile);

        Assert.Equal(128_000, encoder.Bitrate);
        Assert.True(encoder.UseVBR);
        Assert.True(encoder.UseConstrainedVBR);
        Assert.False(encoder.UseDTX);
        Assert.True(encoder.UseInbandFEC);
        Assert.Equal(profile.ExpectedPacketLossPercent, encoder.PacketLossPercent);
    }

    [Fact]
    public void Create_selects_music_signal_for_stereo_profiles()
    {
        var encoder = OpusEncoderFactory.Create(AudioQualityProfile.Default with { Id = "test" });

        Assert.Equal(OpusSignal.OPUS_SIGNAL_MUSIC, encoder.SignalType);
    }

    [Fact]
    public void Create_selects_voice_signal_for_mono_profiles()
    {
        var mono = AudioQualityProfile.Default with { Channels = 1, OpusBitrateKbps = 32 };

        var encoder = OpusEncoderFactory.Create(mono);

        Assert.Equal(OpusSignal.OPUS_SIGNAL_VOICE, encoder.SignalType);
    }

    [Fact]
    public void Create_throws_for_an_invalid_profile()
    {
        var invalid = AudioQualityProfile.Default with { OpusBitrateKbps = 1 };
        Assert.Throws<ArgumentException>(() => OpusEncoderFactory.Create(invalid));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter OpusEncoderFactoryTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
using Concentus.Enums;
using Concentus.Structs;

namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>
/// Builds the Concentus Opus encoder for the fixed <see cref="AudioQualityProfile"/>
/// with packet-loss resilience configured explicitly (see the desktop app's
/// equivalent factory for the full rationale on in-band FEC applicability).
/// </summary>
public static class OpusEncoderFactory
{
    public const int SampleRate = 48000;

    public static OpusEncoder Create(AudioQualityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        var channels = profile.Channels;
        var application = channels == 2
            ? OpusApplication.OPUS_APPLICATION_AUDIO
            : OpusApplication.OPUS_APPLICATION_VOIP;
        return new OpusEncoder(SampleRate, channels, application)
        {
            Bitrate = profile.OpusBitrateKbps * 1000,
            Complexity = 10,
            SignalType = channels == 2 ? OpusSignal.OPUS_SIGNAL_MUSIC : OpusSignal.OPUS_SIGNAL_VOICE,
            UseVBR = true,
            UseConstrainedVBR = true,
            UseDTX = false,
            UseInbandFEC = true,
            PacketLossPercent = profile.ExpectedPacketLossPercent,
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter OpusEncoderFactoryTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Infrastructure.VirtualPublisher/Audio/OpusEncoderFactory.cs tests/SonicRelay.VirtualPublisher.Tests/OpusEncoderFactoryTests.cs
git commit -m "Port OpusEncoderFactory"
```

---

### Task 7: Port `RtpPacketPacer`

**Files:**
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/RtpPacketPacer.cs`
- Test: `tests/SonicRelay.VirtualPublisher.Tests/RtpPacketPacerTests.cs`

**Interfaces:**
- Produces: `RtpPacketPacer(TimeSpan frameDuration, TimeSpan latencyBudget, Action<byte[]> send)` with `Enqueue(byte[])`, `Clear()`, `PacketsSent`, `PacketsDropped`, `Backlog`, and `IAsyncDisposable`. Consumed by `VirtualPublisherPeerConnection` (Task 9).

- [ ] **Step 1: Write the failing test**

```csharp
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class RtpPacketPacerTests
{
    [Fact]
    public async Task Enqueue_sends_a_packet_through_the_callback()
    {
        var sent = new TaskCompletionSource<byte[]>();
        await using var pacer = new RtpPacketPacer(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200),
            packet => sent.TrySetResult(packet));

        pacer.Enqueue([1, 2, 3]);

        var received = await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new byte[] { 1, 2, 3 }, received);
        Assert.Equal(1, pacer.PacketsSent);
    }

    [Fact]
    public void Enqueue_drops_oldest_packets_beyond_the_latency_budget()
    {
        // frameDuration=20ms, latencyBudget=40ms -> capacity for 2 queued frames.
        // The pump task is not given time to run (no await), so packets pile up
        // synchronously and the third Enqueue must drop the first.
        var pacer = new RtpPacketPacer(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40), _ => { });

        pacer.Enqueue([1]);
        pacer.Enqueue([2]);
        pacer.Enqueue([3]);

        Assert.True(pacer.PacketsDropped >= 1);
    }

    [Fact]
    public void Constructor_rejects_a_latency_budget_shorter_than_one_frame()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RtpPacketPacer(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10), _ => { }));
    }

    [Fact]
    public async Task Clear_discards_queued_packets_without_counting_them_as_dropped()
    {
        await using var pacer = new RtpPacketPacer(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), _ => { });
        pacer.Enqueue([1]);

        pacer.Clear();

        Assert.Equal(0, pacer.Backlog);
        Assert.Equal(0, pacer.PacketsDropped);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter RtpPacketPacerTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Diagnostics;

namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>
/// Paces encoded audio packets onto the wire: one packet per frame deadline on a
/// monotonic schedule, so a burst of decoded frames does not leave as an RTP burst.
/// The backlog is bounded by a latency budget; past it the oldest packets are
/// dropped instead of growing latency. <see cref="Enqueue"/> never blocks.
/// </summary>
public sealed class RtpPacketPacer : IAsyncDisposable
{
    private readonly TimeSpan frameDuration;
    private readonly long frameTimestampTicks;
    private readonly int capacity;
    private readonly Action<byte[]> send;
    private readonly Queue<byte[]> queue = new();
    private readonly object sync = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task pump;
    private long packetsSent;
    private long packetsDropped;
    private long sendFailures;
    private bool disposed;

    public RtpPacketPacer(TimeSpan frameDuration, TimeSpan latencyBudget, Action<byte[]> send)
    {
        if (frameDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(frameDuration));
        if (latencyBudget < frameDuration)
            throw new ArgumentOutOfRangeException(
                nameof(latencyBudget), "The latency budget must hold at least one frame.");
        this.frameDuration = frameDuration;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        frameTimestampTicks = (long)(frameDuration.TotalSeconds * Stopwatch.Frequency);
        capacity = (int)(latencyBudget.Ticks / frameDuration.Ticks);
        pump = Task.Run(PumpAsync);
    }

    public long PacketsSent => Interlocked.Read(ref packetsSent);
    public long PacketsDropped => Interlocked.Read(ref packetsDropped);
    public long SendFailures => Interlocked.Read(ref sendFailures);

    public int Backlog
    {
        get { lock (sync) return queue.Count; }
    }

    public TimeSpan BacklogDuration => frameDuration * Backlog;

    public void Enqueue(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Length == 0) return;
        lock (sync)
        {
            if (disposed) return;
            queue.Enqueue(packet);
            while (queue.Count > capacity)
            {
                queue.Dequeue();
                Interlocked.Increment(ref packetsDropped);
            }
            signal.Release();
        }
    }

    public void Clear()
    {
        lock (sync) queue.Clear();
    }

    private async Task PumpAsync()
    {
        var token = cancellation.Token;
        long nextDeadline = 0;
        var anchored = false;
        try
        {
            while (true)
            {
                await signal.WaitAsync(token).ConfigureAwait(false);
                byte[]? packet = null;
                lock (sync)
                {
                    if (queue.Count > 0) packet = queue.Dequeue();
                }
                if (packet is null) continue;

                var now = Stopwatch.GetTimestamp();
                if (!anchored || now - nextDeadline > frameTimestampTicks)
                {
                    nextDeadline = now;
                    anchored = true;
                }

                var wait = nextDeadline - now;
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait / (double)Stopwatch.Frequency), token)
                        .ConfigureAwait(false);
                }

                try
                {
                    send(packet);
                    Interlocked.Increment(ref packetsSent);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref sendFailures);
                }
                nextDeadline += frameTimestampTicks;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            queue.Clear();
        }
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        cancellation.Dispose();
        signal.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter RtpPacketPacerTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Infrastructure.VirtualPublisher/Audio/RtpPacketPacer.cs tests/SonicRelay.VirtualPublisher.Tests/RtpPacketPacerTests.cs
git commit -m "Port RtpPacketPacer"
```

---

### Task 8: `Mp3TrackSource` — alphabetical, looping, skip-invalid track enumeration

**Files:**
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/Mp3TrackSource.cs`
- Test: `tests/SonicRelay.VirtualPublisher.Tests/Mp3TrackSourceTests.cs`

**Interfaces:**
- Produces:
  - `interface IMp3Decoder { IEnumerable<short[]> DecodeFrames(string filePath); }` — abstraction so ordering/looping is testable without real MP3 bytes.
  - `sealed class Mp3TrackSource(string directoryPath, IMp3Decoder decoder, ILogger<Mp3TrackSource> logger)` with `IEnumerable<short[]> ReadForever(CancellationToken cancellationToken)` — infinite alphabetical loop over `*.mp3` files in `directoryPath`; a file whose `DecodeFrames` throws is logged and skipped, loop continues with the next file; an empty/missing directory yields nothing (caller treats "no frames ever produced" as idle, per Task 9 in Plan 2).
- Consumed by: `PublicRoomPublisherService` (Plan 2).

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using SonicRelay.Infrastructure.VirtualPublisher.Audio;
using Xunit;

namespace SonicRelay.VirtualPublisher.Tests;

public sealed class Mp3TrackSourceTests
{
    private sealed class FakeDecoder : IMp3Decoder
    {
        private readonly Dictionary<string, Func<IEnumerable<short[]>>> byPath;
        public FakeDecoder(Dictionary<string, Func<IEnumerable<short[]>>> byPath) => this.byPath = byPath;

        public IEnumerable<short[]> DecodeFrames(string filePath) => byPath[Path.GetFileName(filePath)]();
    }

    private static string CreateTrackDirectory(params string[] fileNames)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sonicrelay-track-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        foreach (var name in fileNames) File.WriteAllBytes(Path.Combine(dir, name), [0]);
        return dir;
    }

    [Fact]
    public void ReadForever_visits_mp3_files_in_alphabetical_order()
    {
        var dir = CreateTrackDirectory("b.mp3", "a.mp3", "c.mp3");
        var order = new List<string>();
        var decoder = new FakeDecoder(new()
        {
            ["a.mp3"] = () => { order.Add("a"); return [[1, 2]]; },
            ["b.mp3"] = () => { order.Add("b"); return [[3, 4]]; },
            ["c.mp3"] = () => { order.Add("c"); return [[5, 6]]; },
        });
        var source = new Mp3TrackSource(dir, decoder, NullLogger<Mp3TrackSource>.Instance);

        var frames = source.ReadForever(CancellationToken.None).Take(3).ToList();

        Assert.Equal(["a", "b", "c"], order);
        Assert.Equal(3, frames.Count);
    }

    [Fact]
    public void ReadForever_loops_back_to_the_first_track_after_the_last()
    {
        var dir = CreateTrackDirectory("a.mp3", "b.mp3");
        var visits = new List<string>();
        var decoder = new FakeDecoder(new()
        {
            ["a.mp3"] = () => { visits.Add("a"); return [[1]]; },
            ["b.mp3"] = () => { visits.Add("b"); return [[2]]; },
        });
        var source = new Mp3TrackSource(dir, decoder, NullLogger<Mp3TrackSource>.Instance);

        source.ReadForever(CancellationToken.None).Take(5).ToList();

        Assert.Equal(["a", "b", "a", "b", "a"], visits);
    }

    [Fact]
    public void ReadForever_skips_a_track_whose_decoder_throws_and_continues_with_the_next()
    {
        var dir = CreateTrackDirectory("a.mp3", "b.mp3");
        var decoder = new FakeDecoder(new()
        {
            ["a.mp3"] = () => throw new InvalidOperationException("corrupt"),
            ["b.mp3"] = () => [[9]],
        });
        var source = new Mp3TrackSource(dir, decoder, NullLogger<Mp3TrackSource>.Instance);

        var frames = source.ReadForever(CancellationToken.None).Take(2).ToList();

        Assert.Equal(2, frames.Count);
        Assert.Equal((short)9, frames[0][0]);
        Assert.Equal((short)9, frames[1][0]); // looped back to b.mp3 again, a.mp3 skipped both times
    }

    [Fact]
    public void ReadForever_yields_nothing_for_an_empty_directory()
    {
        var dir = CreateTrackDirectory();
        var source = new Mp3TrackSource(dir, new FakeDecoder(new()), NullLogger<Mp3TrackSource>.Instance);

        var frames = source.ReadForever(CancellationToken.None).Take(1).ToList();

        Assert.Empty(frames);
    }

    [Fact]
    public void ReadForever_yields_nothing_for_a_missing_directory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sonicrelay-does-not-exist-" + Guid.NewGuid());
        var source = new Mp3TrackSource(missing, new FakeDecoder(new()), NullLogger<Mp3TrackSource>.Instance);

        var frames = source.ReadForever(CancellationToken.None).Take(1).ToList();

        Assert.Empty(frames);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter Mp3TrackSourceTests`
Expected: FAIL to compile.

- [ ] **Step 3: Add the `Microsoft.Extensions.Logging.Abstractions` package to the test project**

Add to `tests/SonicRelay.VirtualPublisher.Tests/SonicRelay.VirtualPublisher.Tests.csproj`'s first `<ItemGroup>`:

```xml
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
```

- [ ] **Step 4: Write the implementation**

```csharp
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
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SonicRelay.VirtualPublisher.Tests --filter Mp3TrackSourceTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/SonicRelay.Infrastructure.VirtualPublisher/Audio/Mp3TrackSource.cs tests/SonicRelay.VirtualPublisher.Tests/Mp3TrackSourceTests.cs tests/SonicRelay.VirtualPublisher.Tests/SonicRelay.VirtualPublisher.Tests.csproj
git commit -m "Add Mp3TrackSource: alphabetical, looping, skip-invalid track enumeration"
```

---

### Task 9: `NLayerMp3Decoder` — the real `IMp3Decoder`

**Files:**
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/Audio/NLayerMp3Decoder.cs`
- Test: none (no NLayer-decodable fixture is checked into the repo; correctness is verified manually per the spec's Testing section — see `docs/superpowers/specs/2026-08-19-public-radio-room-design.md`)

**Interfaces:**
- Consumes: `IMp3Decoder` (Task 8), NLayer's `NLayer.MpegFile`.
- Produces: `NLayerMp3Decoder : IMp3Decoder` — real MP3-to-PCM decoding. Consumed by `PublicRoomPublisherService` (Plan 2), which wires `Mp3TrackSource` with this decoder in production and with a fake in tests.

- [ ] **Step 1: Write the implementation**

```csharp
using NLayer;

namespace SonicRelay.Infrastructure.VirtualPublisher.Audio;

/// <summary>
/// Decodes an MP3 file to interleaved S16 PCM frames using NLayer (a managed
/// decoder — no native dependency, matching the rest of this project's stack).
/// Reads in ~20 ms chunks so <see cref="Mp3TrackSource"/> can interleave decoding
/// across the whole playlist rotation instead of loading a full track at once.
/// </summary>
public sealed class NLayerMp3Decoder : IMp3Decoder
{
    private const int SamplesPerChunkPerChannel = 960; // 20 ms at 48 kHz

    public IEnumerable<short[]> DecodeFrames(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var mpegFile = new MpegFile(stream);
        var channels = mpegFile.Channels;
        var chunkFloats = new float[SamplesPerChunkPerChannel * channels];

        while (true)
        {
            var read = mpegFile.ReadSamples(chunkFloats, 0, chunkFloats.Length);
            if (read <= 0) yield break;

            var pcm = new short[read];
            for (var i = 0; i < read; i++)
            {
                pcm[i] = (short)Math.Round(Math.Clamp(chunkFloats[i], -1f, 1f) * short.MaxValue);
            }
            yield return pcm;
        }
    }
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build src/SonicRelay.Infrastructure.VirtualPublisher/SonicRelay.Infrastructure.VirtualPublisher.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SonicRelay.Infrastructure.VirtualPublisher/Audio/NLayerMp3Decoder.cs
git commit -m "Add NLayerMp3Decoder"
```

---

### Task 10: `WebRtcContracts` — trimmed session-description/candidate/frame types

**Files:**
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/WebRtc/WebRtcContracts.cs`

**Interfaces:**
- Produces: `WebRtcSessionDescription(string Type, string Sdp)`, `WebRtcIceCandidate(string Candidate, string? SdpMid, int? SdpMLineIndex)`, `WebRtcAudioFrame` (bytes + sampleRate + channelCount, validated non-empty). Consumed by `VirtualPublisherPeerConnection` (Task 11).

- [ ] **Step 1: Write the implementation**

```csharp
namespace SonicRelay.Infrastructure.VirtualPublisher.WebRtc;

public sealed record WebRtcSessionDescription(string Type, string Sdp);

public sealed record WebRtcIceCandidate(string Candidate, string? SdpMid = null, int? SdpMLineIndex = null);

public sealed class WebRtcAudioFrame
{
    private readonly byte[] data;

    public WebRtcAudioFrame(ReadOnlySpan<byte> data, int sampleRate, int channelCount)
    {
        if (data.IsEmpty) throw new ArgumentException("Audio frame data cannot be empty.", nameof(data));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channelCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channelCount));
        this.data = data.ToArray();
        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }

    public ReadOnlyMemory<byte> Data => data;
    public int SampleRate { get; }
    public int ChannelCount { get; }
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build src/SonicRelay.Infrastructure.VirtualPublisher/SonicRelay.Infrastructure.VirtualPublisher.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SonicRelay.Infrastructure.VirtualPublisher/WebRtc/WebRtcContracts.cs
git commit -m "Add trimmed WebRTC contracts for the virtual publisher"
```

---

### Task 11: `VirtualPublisherPeerConnection` — minimal SIPSorcery wrapper

**Files:**
- Create: `src/SonicRelay.Infrastructure.VirtualPublisher/WebRtc/VirtualPublisherPeerConnection.cs`
- Test: none (requires a real ICE/DTLS handshake against a peer; verified manually per the spec's Testing section — the ported units above (Tasks 4-9) cover everything that is unit-testable in isolation)

**Interfaces:**
- Consumes: `AudioQualityProfile` (Task 3), `OpusEncoderFactory` (Task 6), `OpusFrameAccumulator` (Task 5), `PcmAudioConverter`/`WebRtcSourceSampleFormat` (Task 4), `RtpPacketPacer` (Task 7), `WebRtcContracts` (Task 10).
- Produces: `VirtualPublisherPeerConnection(string viewerParticipantId, RTCPeerConnection connection, AudioQualityProfile? profile = null)` — deliberately smaller than the desktop's `SipSorceryPeerConnection`: no RTCP diagnostics, no ICE restart (out of scope per spec). Exposes:
  - `string ViewerParticipantId`
  - `event Func<WebRtcIceCandidate, CancellationToken, Task>? LocalIceCandidateReady`
  - `Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken ct = default)`
  - `Task ApplyAnswerAsync(WebRtcSessionDescription answer, CancellationToken ct = default)`
  - `Task AddRemoteIceCandidateAsync(WebRtcIceCandidate candidate, CancellationToken ct = default)`
  - `Task SendAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken ct = default)`
  - `IAsyncDisposable`
  Consumed by `PublicRoomPublisherService` (Plan 2), one instance per connected viewer.

- [ ] **Step 1: Write the implementation**

```csharp
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
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build src/SonicRelay.Infrastructure.VirtualPublisher/SonicRelay.Infrastructure.VirtualPublisher.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SonicRelay.Infrastructure.VirtualPublisher/WebRtc/VirtualPublisherPeerConnection.cs
git commit -m "Add VirtualPublisherPeerConnection (minimal SIPSorcery wrapper)"
```

---

## Plan 1 Self-Review Notes

- **Spec coverage:** MP3 decode (Task 9), alphabetical + infinite loop + skip-invalid (Task 8, directly tested), fixed Opus/WebRTC profile (Tasks 3/6), pacing to avoid bursts (Task 7), send-only peer connection per viewer (Task 11). RTCP diagnostics/ICE-restart are explicitly out of scope per the spec and intentionally omitted.
- **No placeholders:** every task has real, complete code; the two untested tasks (9 and 11) are called out with the reason (native codec I/O and live ICE/DTLS respectively, both need a real network peer — covered by the spec's manual test step) rather than left silently untested.
- **Type consistency:** `WebRtcAudioFrame`, `WebRtcSessionDescription`, `WebRtcIceCandidate` (Task 10) are the exact types `VirtualPublisherPeerConnection` (Task 11) consumes; `IMp3Decoder`/`Mp3TrackSource` (Task 8) is the exact type `NLayerMp3Decoder` (Task 9) implements.

## Execution Handoff

This is Plan 1 of 2. Plan 2 (`docs/superpowers/plans/2026-08-19-public-radio-room-2-orchestration.md`) wires this library into a `BackgroundService`, the discovery endpoint, and docker-compose, and depends on every task here being complete first.
