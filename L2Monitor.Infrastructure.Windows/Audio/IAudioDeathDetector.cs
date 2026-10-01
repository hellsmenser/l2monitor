namespace L2Monitor.Infrastructure.Windows;

public interface IAudioDeathDetector
{
    void UpdateTrackedProcessIds(IReadOnlyList<int> processIds);
    IReadOnlyList<AudioDeathDetection> DrainDetections(DateTimeOffset observedAtUtc);
}

public sealed record AudioDeathDetection(
    int ProcessId,
    DateTimeOffset DetectedAtUtc,
    double Confidence,
    string ReferenceName);
