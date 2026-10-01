using NAudio.Dsp;

namespace L2Monitor.Infrastructure.Windows;

internal static class AudioFingerprintMatcher
{
    private const int FftSize = 2048;
    private const int HopSize = 512;
    private const int PeaksPerFrame = 5;
    private const int PeakNeighborhood = 4;
    private const int MinFrequencyHz = 250;
    private const int MaxFrequencyHz = 4_200;
    private const int TargetZoneFrames = 10;
    private const int TargetFanOut = 3;
    private const double PeakSimilarityBinTolerance = 18d;

    public static AudioFingerprintReference BuildReference(float[] samples, int sampleRate)
    {
        var peaks = ExtractPeaks(samples, sampleRate);
        var hashes = BuildHashes(peaks);
        var landmarkCount = hashes.Values.Sum(static landmarks => landmarks.Count);
        return new AudioFingerprintReference(sampleRate, samples.ToArray(), peaks.Count, peaks, landmarkCount, hashes);
    }

    public static AudioFingerprintMatch Match(AudioFingerprintReference reference, float[] samples, int sampleRate)
    {
        var peaks = ExtractPeaks(samples, sampleRate);
        if (peaks.Count == 0 || reference.Hashes.Count == 0)
        {
            return AudioFingerprintMatch.NoMatch;
        }

        var queryHashes = BuildHashes(peaks);
        var queryLandmarkCount = queryHashes.Values.Sum(static landmarks => landmarks.Count);
        if (queryHashes.Count == 0 || queryLandmarkCount == 0)
        {
            return AudioFingerprintMatch.NoMatch;
        }

        var offsetHistogram = new Dictionary<int, OffsetMatchStats>();
        foreach (var (hash, queryLandmarks) in queryHashes)
        {
            if (!reference.Hashes.TryGetValue(hash, out var referenceLandmarks))
            {
                continue;
            }

            foreach (var queryLandmark in queryLandmarks)
            {
                foreach (var referenceLandmark in referenceLandmarks)
                {
                    var offset = queryLandmark.AnchorFrame - referenceLandmark.AnchorFrame;
                    if (!offsetHistogram.TryGetValue(offset, out var stats))
                    {
                        stats = new OffsetMatchStats();
                        offsetHistogram[offset] = stats;
                    }

                    stats.ReferenceLandmarks.Add(referenceLandmark.Id);
                    stats.QueryLandmarks.Add(queryLandmark.Id);
                    stats.QueryFrames.Add(queryLandmark.AnchorFrame);
                    stats.QueryFrames.Add(queryLandmark.TargetFrame);
                    stats.ReferenceFrames.Add(referenceLandmark.AnchorFrame);
                    stats.ReferenceFrames.Add(referenceLandmark.TargetFrame);
                }
            }
        }

        if (offsetHistogram.Count == 0)
        {
            return AudioFingerprintMatch.NoMatch;
        }

        var best = offsetHistogram
            .Select(pair => new CandidateOffsetMatch(
                pair.Key,
                pair.Value,
                ComputeMatchScore(pair.Value, reference, queryLandmarkCount)))
            .MaxBy(static candidate => candidate.Score);
        if (best is null)
        {
            return AudioFingerprintMatch.NoMatch;
        }

        var alignedFrameCoverage = best.Value.ReferenceFrames.Count / (double)Math.Max(1, reference.PeakFrameCount);
        var referenceLandmarkCoverage = best.Value.ReferenceLandmarks.Count / (double)Math.Max(1, reference.LandmarkCount);
        var envelopeSimilarity = ComputeEnvelopeSimilarity(reference.Samples, samples, best.OffsetFrames);
        var peakSequenceSimilarity = ComputePeakSequenceSimilarity(reference.Peaks, peaks, best.OffsetFrames);
        return new AudioFingerprintMatch(
            best.Value.ReferenceLandmarks.Count,
            referenceLandmarkCoverage,
            best.OffsetFrames,
            alignedFrameCoverage,
            envelopeSimilarity,
            peakSequenceSimilarity);
    }

    private static double ComputeMatchScore(
        OffsetMatchStats stats,
        AudioFingerprintReference reference,
        int queryLandmarkCount)
    {
        var referenceLandmarkCoverage = stats.ReferenceLandmarks.Count / (double)Math.Max(1, reference.LandmarkCount);
        var alignedFrameCoverage = stats.ReferenceFrames.Count / (double)Math.Max(1, reference.PeakFrameCount);
        var queryLandmarkPrecision = stats.QueryLandmarks.Count / (double)Math.Max(1, queryLandmarkCount);
        return (referenceLandmarkCoverage * 0.7d)
            + (alignedFrameCoverage * 0.2d)
            + (queryLandmarkPrecision * 0.1d);
    }

    private static double ComputeEnvelopeSimilarity(float[] referenceSamples, float[] querySamples, int offsetFrames)
    {
        if (referenceSamples.Length == 0 || querySamples.Length == 0)
        {
            return 0d;
        }

        var queryStart = offsetFrames * HopSize;
        var referenceStart = 0;
        if (queryStart < 0)
        {
            referenceStart = -queryStart;
            queryStart = 0;
        }

        if (queryStart >= querySamples.Length || referenceStart >= referenceSamples.Length)
        {
            return 0d;
        }

        var overlapLength = Math.Min(referenceSamples.Length - referenceStart, querySamples.Length - queryStart);
        if (overlapLength < referenceSamples.Length * 0.80d)
        {
            return 0d;
        }

        const int envelopeWindow = 256;
        var referenceEnvelope = BuildEnvelope(referenceSamples, referenceStart, overlapLength, envelopeWindow);
        var queryEnvelope = BuildEnvelope(querySamples, queryStart, overlapLength, envelopeWindow);
        return ComputeCosineSimilarity(referenceEnvelope, queryEnvelope);
    }

    private static double[] BuildEnvelope(float[] samples, int offset, int length, int windowSize)
    {
        var bucketCount = (int)Math.Ceiling(length / (double)windowSize);
        var envelope = new double[bucketCount];
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = offset + (bucket * windowSize);
            var end = Math.Min(offset + length, start + windowSize);
            double sum = 0;
            for (var i = start; i < end; i++)
            {
                sum += Math.Abs(samples[i]);
            }

            envelope[bucket] = sum / Math.Max(1, end - start);
        }

        return envelope;
    }

    private static double ComputeCosineSimilarity(double[] left, double[] right)
    {
        if (left.Length == 0 || right.Length == 0 || left.Length != right.Length)
        {
            return 0d;
        }

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        if (leftNorm <= double.Epsilon || rightNorm <= double.Epsilon)
        {
            return 0d;
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private static double ComputePeakSequenceSimilarity(
        IReadOnlyList<FramePeaks> referencePeaks,
        IReadOnlyList<FramePeaks> queryPeaks,
        int offsetFrames)
    {
        if (referencePeaks.Count == 0 || queryPeaks.Count == 0)
        {
            return 0d;
        }

        var queryByFrame = queryPeaks.ToDictionary(static frame => frame.FrameIndex);
        double similarityTotal = 0d;
        var comparedFrames = 0;

        foreach (var referenceFrame in referencePeaks)
        {
            if (!queryByFrame.TryGetValue(referenceFrame.FrameIndex + offsetFrames, out var queryFrame))
            {
                continue;
            }

            similarityTotal += ComputeFramePeakSimilarity(referenceFrame.Bins, queryFrame.Bins);
            comparedFrames++;
        }

        if (comparedFrames == 0)
        {
            return 0d;
        }

        return similarityTotal / comparedFrames;
    }

    private static double ComputeFramePeakSimilarity(IReadOnlyList<int> referenceBins, IReadOnlyList<int> queryBins)
    {
        if (referenceBins.Count == 0 || queryBins.Count == 0)
        {
            return 0d;
        }

        return (ComputeDirectionalFrameSimilarity(referenceBins, queryBins)
            + ComputeDirectionalFrameSimilarity(queryBins, referenceBins)) / 2d;
    }

    private static double ComputeDirectionalFrameSimilarity(IReadOnlyList<int> sourceBins, IReadOnlyList<int> candidateBins)
    {
        double similarityTotal = 0d;
        foreach (var sourceBin in sourceBins)
        {
            var bestDifference = candidateBins.Min(candidateBin => Math.Abs(sourceBin - candidateBin));
            var normalizedDifference = Math.Min(1d, bestDifference / PeakSimilarityBinTolerance);
            similarityTotal += 1d - normalizedDifference;
        }

        return similarityTotal / sourceBins.Count;
    }

    private static Dictionary<int, List<HashLandmark>> BuildHashes(IReadOnlyList<FramePeaks> peaks)
    {
        var hashes = new Dictionary<int, List<HashLandmark>>();
        var nextLandmarkId = 0;

        for (var anchorIndex = 0; anchorIndex < peaks.Count; anchorIndex++)
        {
            var anchor = peaks[anchorIndex];
            var remainingFanOut = TargetFanOut;

            for (var targetIndex = anchorIndex + 1;
                 targetIndex < peaks.Count && targetIndex <= anchorIndex + TargetZoneFrames && remainingFanOut > 0;
                 targetIndex++)
            {
                var target = peaks[targetIndex];
                var deltaFrames = target.FrameIndex - anchor.FrameIndex;
                if (deltaFrames <= 0)
                {
                    continue;
                }

                foreach (var anchorPeak in anchor.Bins)
                {
                    foreach (var targetPeak in target.Bins)
                    {
                        var hash = Hash(anchorPeak / 2, targetPeak / 2, deltaFrames);
                        if (!hashes.TryGetValue(hash, out var times))
                        {
                            times = [];
                            hashes[hash] = times;
                        }

                        times.Add(new HashLandmark(nextLandmarkId++, anchor.FrameIndex, target.FrameIndex));
                    }
                }

                remainingFanOut--;
            }
        }

        return hashes;
    }

    private static List<FramePeaks> ExtractPeaks(float[] samples, int sampleRate)
    {
        if (samples.Length < FftSize || sampleRate <= 0)
        {
            return [];
        }

        var minBin = Math.Clamp((int)Math.Round(MinFrequencyHz * FftSize / (double)sampleRate), 1, (FftSize / 2) - 1);
        var maxBin = Math.Clamp((int)Math.Round(MaxFrequencyHz * FftSize / (double)sampleRate), minBin + 1, (FftSize / 2) - 1);
        var peaks = new List<FramePeaks>();
        var window = CreateHannWindow(FftSize);

        for (var offset = 0; offset + FftSize <= samples.Length; offset += HopSize)
        {
            var spectrum = new Complex[FftSize];
            for (var i = 0; i < FftSize; i++)
            {
                spectrum[i].X = samples[offset + i] * window[i];
                spectrum[i].Y = 0;
            }

            FastFourierTransform.FFT(forward: true, (int)Math.Log2(FftSize), spectrum);

            var magnitudes = new double[maxBin + 1];
            var frameMax = 0d;
            for (var bin = minBin; bin <= maxBin; bin++)
            {
                var magnitude = Math.Sqrt((spectrum[bin].X * spectrum[bin].X) + (spectrum[bin].Y * spectrum[bin].Y));
                magnitudes[bin] = magnitude;
                if (magnitude > frameMax)
                {
                    frameMax = magnitude;
                }
            }

            if (frameMax <= 0)
            {
                continue;
            }

            var threshold = frameMax * 0.45d;
            var peakBins = new List<int>(PeaksPerFrame);
            for (var bin = minBin + PeakNeighborhood; bin <= maxBin - PeakNeighborhood; bin++)
            {
                var magnitude = magnitudes[bin];
                if (magnitude < threshold)
                {
                    continue;
                }

                var isLocalMax = true;
                for (var neighbor = bin - PeakNeighborhood; neighbor <= bin + PeakNeighborhood; neighbor++)
                {
                    if (neighbor == bin)
                    {
                        continue;
                    }

                    if (magnitudes[neighbor] > magnitude)
                    {
                        isLocalMax = false;
                        break;
                    }
                }

                if (isLocalMax)
                {
                    peakBins.Add(bin);
                }
            }

            if (peakBins.Count == 0)
            {
                continue;
            }

            var selectedBins = peakBins
                .OrderByDescending(bin => magnitudes[bin])
                .Take(PeaksPerFrame)
                .OrderBy(static bin => bin)
                .ToArray();

            peaks.Add(new FramePeaks(offset / HopSize, selectedBins));
        }

        return peaks;
    }

    private static float[] CreateHannWindow(int size)
    {
        var window = new float[size];
        for (var i = 0; i < size; i++)
        {
            window[i] = (float)(0.5d * (1d - Math.Cos((2d * Math.PI * i) / (size - 1))));
        }

        return window;
    }

    private static int Hash(int firstBin, int secondBin, int deltaFrames)
        => (firstBin << 18) | (secondBin << 6) | Math.Clamp(deltaFrames, 0, 63);

    internal sealed record AudioFingerprintReference(
        int SampleRate,
        float[] Samples,
        int PeakFrameCount,
        IReadOnlyList<FramePeaks> Peaks,
        int LandmarkCount,
        IReadOnlyDictionary<int, List<HashLandmark>> Hashes);

    internal sealed record AudioFingerprintMatch(
        int MatchingHashes,
        double Confidence,
        int OffsetFrames,
        double AlignedFrameCoverage,
        double EnvelopeSimilarity,
        double PeakSequenceSimilarity)
    {
        public static AudioFingerprintMatch NoMatch { get; } = new(0, 0, 0, 0, 0, 0);
    }

    private sealed class OffsetMatchStats
    {
        public HashSet<int> ReferenceLandmarks { get; } = [];
        public HashSet<int> QueryLandmarks { get; } = [];
        public HashSet<int> QueryFrames { get; } = [];
        public HashSet<int> ReferenceFrames { get; } = [];
    }

    private sealed record CandidateOffsetMatch(int OffsetFrames, OffsetMatchStats Value, double Score);
    internal sealed record HashLandmark(int Id, int AnchorFrame, int TargetFrame);
    internal sealed record FramePeaks(int FrameIndex, IReadOnlyList<int> Bins);
}
