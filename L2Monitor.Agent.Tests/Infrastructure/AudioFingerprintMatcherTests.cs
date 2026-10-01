using L2Monitor.Infrastructure.Windows;
using Xunit;

namespace L2Monitor.Agent.Tests.Infrastructure;

public sealed class AudioFingerprintMatcherTests
{
    private const int SampleRate = 11_025;

    [Fact]
    public void Match_DetectsReferenceEmbeddedInsideLongerWindow()
    {
        var reference = CreateReferencePattern();
        var fingerprint = AudioFingerprintMatcher.BuildReference(reference, SampleRate);

        var window = new float[SampleRate * 4];
        var rng = new Random(1234);
        for (var i = 0; i < window.Length; i++)
        {
            window[i] = (float)((rng.NextDouble() - 0.5d) * 0.02d);
        }

        var insertAt = SampleRate;
        for (var i = 0; i < reference.Length; i++)
        {
            window[insertAt + i] += reference[i];
        }

        var match = AudioFingerprintMatcher.Match(fingerprint, window, SampleRate);

        Assert.True(match.MatchingHashes >= 8);
        Assert.True(match.Confidence >= 0.08d);
        Assert.True(match.AlignedFrameCoverage >= 0.25d);
        Assert.True(match.EnvelopeSimilarity >= 0.70d);
        Assert.True(match.PeakSequenceSimilarity >= 0.78d);
    }

    [Fact]
    public void Match_RejectsDifferentPattern()
    {
        var fingerprint = AudioFingerprintMatcher.BuildReference(CreateReferencePattern(), SampleRate);
        var different = CreateDifferentPattern();

        var match = AudioFingerprintMatcher.Match(fingerprint, different, SampleRate);

        Assert.True(match.MatchingHashes < 8 || match.Confidence < 0.08d);
        Assert.True(match.EnvelopeSimilarity < 0.70d || match.AlignedFrameCoverage < 0.25d);
        Assert.True(match.PeakSequenceSimilarity < 0.78d);
    }

    [Fact]
    public void Match_RejectsPatternWithSimilarTonesButDifferentEnvelope()
    {
        var reference = CreateReferencePattern();
        var fingerprint = AudioFingerprintMatcher.BuildReference(reference, SampleRate);
        var differentEnvelope = CreateDifferentEnvelopePattern();

        var match = AudioFingerprintMatcher.Match(fingerprint, differentEnvelope, SampleRate);

        Assert.True(
            match.Confidence < 0.20d
            || match.AlignedFrameCoverage < 0.28d
            || match.EnvelopeSimilarity < 0.72d
            || match.PeakSequenceSimilarity < 0.78d);
    }

    [Fact]
    public void Match_RejectsNotificationLikePatternWithMatchingEnvelopeButMismatchedDominantTones()
    {
        var reference = CreateReferencePattern();
        var fingerprint = AudioFingerprintMatcher.BuildReference(reference, SampleRate);
        var notificationLike = CreateLayeredNotificationPattern();

        var match = AudioFingerprintMatcher.Match(fingerprint, notificationLike, SampleRate);

        Assert.True(match.MatchingHashes >= 10, $"Matching hashes were {match.MatchingHashes}.");
        Assert.True(match.Confidence >= 0.20d, $"Confidence was {match.Confidence:F3}.");
        Assert.True(match.AlignedFrameCoverage >= 0.28d, $"Aligned frame coverage was {match.AlignedFrameCoverage:F3}.");
        Assert.True(match.EnvelopeSimilarity >= 0.72d, $"Envelope similarity was {match.EnvelopeSimilarity:F3}.");
        Assert.True(match.PeakSequenceSimilarity < 0.78d, $"Peak sequence similarity was {match.PeakSequenceSimilarity:F3}.");
    }

    [Fact]
    public void Match_DoesNotInflateConfidenceForRepeatedHashLikePattern()
    {
        var reference = CreateReferencePattern();
        var fingerprint = AudioFingerprintMatcher.BuildReference(reference, SampleRate);
        var repeated = Concatenate(
            CreateTone(620, 0.14, 0.85f),
            CreateSilence(0.04),
            CreateTone(620, 0.10, 0.85f),
            CreateSilence(0.03),
            CreateTone(620, 0.16, 0.85f),
            CreateSilence(0.10),
            CreateTone(620, 0.14, 0.85f),
            CreateSilence(0.04),
            CreateTone(620, 0.10, 0.85f),
            CreateSilence(0.03),
            CreateTone(620, 0.16, 0.85f));

        var match = AudioFingerprintMatcher.Match(fingerprint, repeated, SampleRate);

        Assert.True(match.Confidence < 0.20d || match.PeakSequenceSimilarity < 0.78d,
            $"Confidence={match.Confidence:F3}, PeakSequence={match.PeakSequenceSimilarity:F3}, MatchingHashes={match.MatchingHashes}.");
        Assert.True(match.AlignedFrameCoverage < 0.50d || match.PeakSequenceSimilarity < 0.78d,
            $"FrameCoverage={match.AlignedFrameCoverage:F3}, PeakSequence={match.PeakSequenceSimilarity:F3}.");
    }

    [Fact]
    public void TrimSilence_RemovesQuietPaddingAroundReference()
    {
        var signal = CreateReferencePattern();
        var padded = new float[(SampleRate / 2) + signal.Length + (SampleRate / 3)];
        Array.Copy(signal, 0, padded, SampleRate / 2, signal.Length);

        var trimmed = DeathAudioDetector.TrimSilence(padded);

        Assert.True(trimmed.Length < padded.Length);
        Assert.True(trimmed.Length >= signal.Length);
    }

    private static float[] CreateReferencePattern()
    {
        var toneA = CreateTone(620, 0.14, 0.9f);
        var toneB = CreateTone(930, 0.10, 0.8f);
        var toneC = CreateTone(510, 0.16, 0.7f);
        return Concatenate(toneA, CreateSilence(0.04), toneB, CreateSilence(0.03), toneC);
    }

    private static float[] CreateDifferentPattern()
    {
        var toneA = CreateTone(300, 0.12, 0.7f);
        var toneB = CreateTone(410, 0.12, 0.7f);
        var toneC = CreateTone(1_520, 0.09, 0.7f);
        return Concatenate(toneA, CreateSilence(0.06), toneB, CreateSilence(0.05), toneC);
    }

    private static float[] CreateDifferentEnvelopePattern()
    {
        var toneA = CreateTone(620, 0.08, 0.9f);
        var toneB = CreateTone(930, 0.22, 0.8f);
        var toneC = CreateTone(510, 0.07, 0.7f);
        return Concatenate(toneB, CreateSilence(0.01), toneA, CreateSilence(0.09), toneC, CreateSilence(0.03), toneB);
    }

    private static float[] CreateLayeredNotificationPattern()
    {
        var reference = CreateReferencePattern();
        var overlay = Concatenate(
            CreateTone(710, 0.14, 1.0f),
            CreateSilence(0.04),
            CreateTone(1_090, 0.10, 0.95f),
            CreateSilence(0.03),
            CreateTone(660, 0.16, 0.9f));

        var result = new float[reference.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = reference[i] * 0.85f + (overlay[i] * 0.55f);
        }

        return result;
    }

    private static float[] CreateTone(double frequencyHz, double durationSec, float amplitude)
    {
        var sampleCount = (int)Math.Round(durationSec * SampleRate);
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var envelope = Math.Sin(Math.PI * i / Math.Max(1, sampleCount - 1));
            samples[i] = (float)(Math.Sin((2d * Math.PI * frequencyHz * i) / SampleRate) * envelope * amplitude);
        }

        return samples;
    }

    private static float[] CreateSilence(double durationSec)
        => new float[(int)Math.Round(durationSec * SampleRate)];

    private static float[] Concatenate(params float[][] parts)
    {
        var totalLength = parts.Sum(static part => part.Length);
        var result = new float[totalLength];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }
}
