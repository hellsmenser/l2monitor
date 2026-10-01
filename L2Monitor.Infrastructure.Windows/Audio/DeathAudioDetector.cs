using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace L2Monitor.Infrastructure.Windows;

internal sealed class DeathAudioDetector(
    TimeProvider timeProvider,
    ILogger<DeathAudioDetector> logger) : IAudioDeathDetector, IHostedService, IDisposable
{
    private const int TargetSampleRate = 11_025;
    private static readonly TimeSpan AnalysisInterval = TimeSpan.FromMilliseconds(900);
    private static readonly WaveFormat ProcessLoopbackFormat = new(44_100, 16, 2);
    private const long ProcessLoopbackBufferDurationHns = 200_000;
    private const int MinimumMatchingHashes = 10;
    private const double MinimumConfidence = 0.20d;
    private const double MinimumAlignedFrameCoverage = 0.28d;
    private const double MinimumEnvelopeSimilarity = 0.72d;
    private const double MinimumPeakSequenceSimilarity = 0.78d;

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<DeathAudioDetector> _logger = logger;
    private readonly ConcurrentQueue<AudioDeathDetection> _detections = new();
    private readonly Dictionary<int, ProcessLoopbackCapture> _capturesByPid = [];
    private AudioFingerprintMatcher.AudioFingerprintReference? _reference;
    private string? _referenceName;
    private bool _started;

    public void UpdateTrackedProcessIds(IReadOnlyList<int> processIds)
    {
        if (!_started || _reference is null)
        {
            return;
        }

        var desired = processIds
            .Where(static pid => pid > 0)
            .Distinct()
            .ToHashSet();

        var toStop = new List<ProcessLoopbackCapture>();
        var toStart = new List<ProcessLoopbackCapture>();

        lock (_sync)
        {
            foreach (var (pid, capture) in _capturesByPid.ToArray())
            {
                if (!desired.Contains(pid))
                {
                    _capturesByPid.Remove(pid);
                    toStop.Add(capture);
                }
            }

            foreach (var pid in desired)
            {
                if (_capturesByPid.ContainsKey(pid))
                {
                    continue;
                }

                var capture = new ProcessLoopbackCapture(
                    pid,
                    _reference,
                    _referenceName ?? "audio_trimmed.m4a",
                    _timeProvider,
                    _logger,
                    OnDetection);
                _capturesByPid[pid] = capture;
                toStart.Add(capture);
            }
        }

        foreach (var capture in toStop)
        {
            capture.Dispose();
        }

        foreach (var capture in toStart)
        {
            capture.Start();
        }
    }

    public IReadOnlyList<AudioDeathDetection> DrainDetections(DateTimeOffset observedAtUtc)
    {
        var drained = new List<AudioDeathDetection>();
        while (_detections.TryPeek(out var detection) && detection.DetectedAtUtc <= observedAtUtc)
        {
            if (_detections.TryDequeue(out detection))
            {
                drained.Add(detection);
            }
        }

        return drained;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        var referencePath = ResolveReferencePath();
        if (referencePath is null)
        {
            _logger.LogInformation("Death audio detector is disabled because audio_trimmed.m4a was not found.");
            return Task.CompletedTask;
        }

        try
        {
            var referenceSamples = LoadReferenceSamples(referencePath);
            _reference = AudioFingerprintMatcher.BuildReference(referenceSamples, TargetSampleRate);
            _referenceName = Path.GetFileName(referencePath);

            _logger.LogInformation(
                "Death audio detector enabled in process-loopback mode. Reference={Reference} SampleRate={SampleRate} Hashes={HashCount} " +
                "Thresholds: MatchingHashes>={MinimumMatchingHashes} Confidence>={MinimumConfidence:F2} FrameCoverage>={MinimumAlignedFrameCoverage:F2} Envelope>={MinimumEnvelopeSimilarity:F2} PeakSequence>={MinimumPeakSequenceSimilarity:F2}",
                referencePath,
                TargetSampleRate,
                _reference.Hashes.Count,
                MinimumMatchingHashes,
                MinimumConfidence,
                MinimumAlignedFrameCoverage,
                MinimumEnvelopeSimilarity,
                MinimumPeakSequenceSimilarity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to initialize death audio detector. Audio trigger will stay disabled.");
            _reference = null;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        List<ProcessLoopbackCapture> captures;
        lock (_sync)
        {
            captures = [.. _capturesByPid.Values];
            _capturesByPid.Clear();
        }

        foreach (var capture in captures)
        {
            capture.Dispose();
        }
    }

    private void OnDetection(AudioDeathDetection detection)
        => _detections.Enqueue(detection);

    private static string? ResolveReferencePath()
    {
        var envPath = Environment.GetEnvironmentVariable("L2MONITOR_DEATH_AUDIO_SAMPLE");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "audio_trimmed.m4a"),
            Path.Combine(Environment.CurrentDirectory, "audio_trimmed.m4a"),
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++)
        {
            candidates.Add(Path.Combine(directory.FullName, "audio_trimmed.m4a"));
            directory = directory.Parent;
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static float[] LoadReferenceSamples(string referencePath)
    {
        using var reader = new MediaFoundationReader(referencePath);
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels > 1)
        {
            provider = new StereoToMonoSampleProvider(provider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f,
            };
        }

        var sourceSamples = ReadAllSamples(provider);
        var monoSamples = provider.WaveFormat.SampleRate == TargetSampleRate
            ? sourceSamples
            : ResampleToTarget(sourceSamples, provider.WaveFormat.SampleRate, TargetSampleRate);

        return TrimSilence(monoSamples);
    }

    private static float[] ReadAllSamples(ISampleProvider provider)
    {
        var samples = new List<float>();
        var buffer = new float[provider.WaveFormat.SampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        return samples.ToArray();
    }

    private static float[] ConvertToMonoSamples(byte[] buffer, int bytesRecorded, WaveFormat waveFormat)
    {
        var channels = Math.Max(1, waveFormat.Channels);
        if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat && waveFormat.BitsPerSample == 32)
        {
            var sampleCount = bytesRecorded / sizeof(float);
            var result = new float[sampleCount / channels];
            for (var frame = 0; frame < result.Length; frame++)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += BitConverter.ToSingle(buffer, (frame * channels + channel) * sizeof(float));
                }

                result[frame] = sum / channels;
            }

            return result;
        }

        if (waveFormat.Encoding == WaveFormatEncoding.Pcm && waveFormat.BitsPerSample == 16)
        {
            var bytesPerSample = sizeof(short);
            var sampleCount = bytesRecorded / bytesPerSample;
            var result = new float[sampleCount / channels];
            for (var frame = 0; frame < result.Length; frame++)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var value = BitConverter.ToInt16(buffer, (frame * channels + channel) * bytesPerSample);
                    sum += value / (float)short.MaxValue;
                }

                result[frame] = sum / channels;
            }

            return result;
        }

        return [];
    }

    internal static float[] ResampleToTarget(float[] source, int sourceRate, int targetRate)
    {
        if (source.Length == 0 || sourceRate <= 0 || targetRate <= 0)
        {
            return [];
        }

        if (sourceRate == targetRate)
        {
            return source.ToArray();
        }

        var targetLength = (int)Math.Round(source.Length * (targetRate / (double)sourceRate));
        var result = new float[targetLength];

        for (var i = 0; i < targetLength; i++)
        {
            var position = i * (sourceRate / (double)targetRate);
            var left = Math.Clamp((int)Math.Floor(position), 0, source.Length - 1);
            var right = Math.Min(left + 1, source.Length - 1);
            var frac = position - left;
            result[i] = (float)((source[left] * (1d - frac)) + (source[right] * frac));
        }

        return result;
    }

    internal static float[] TrimSilence(float[] samples)
    {
        if (samples.Length == 0)
        {
            return samples;
        }

        var peak = samples.Max(static sample => Math.Abs(sample));
        var threshold = Math.Max(0.015f, peak * 0.12f);

        var start = 0;
        while (start < samples.Length && Math.Abs(samples[start]) < threshold)
        {
            start++;
        }

        var end = samples.Length - 1;
        while (end >= start && Math.Abs(samples[end]) < threshold)
        {
            end--;
        }

        if (end < start)
        {
            return samples;
        }

        var padding = TargetSampleRate / 20;
        start = Math.Max(0, start - padding);
        end = Math.Min(samples.Length - 1, end + padding);

        var result = new float[end - start + 1];
        Array.Copy(samples, start, result, 0, result.Length);
        return result;
    }

    private static float[] AppendAndTrim(float[] existing, float[] chunk, int maxLength)
    {
        if (chunk.Length >= maxLength)
        {
            return chunk[^maxLength..];
        }

        var keepExisting = Math.Min(existing.Length, Math.Max(0, maxLength - chunk.Length));
        var result = new float[keepExisting + chunk.Length];
        if (keepExisting > 0)
        {
            Array.Copy(existing, existing.Length - keepExisting, result, 0, keepExisting);
        }

        Array.Copy(chunk, 0, result, keepExisting, chunk.Length);
        return result;
    }

    private sealed class ProcessLoopbackCapture(
        int processId,
        AudioFingerprintMatcher.AudioFingerprintReference reference,
        string referenceName,
        TimeProvider timeProvider,
        ILogger logger,
        Action<AudioDeathDetection> publishDetection) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _runTask;
        private float[] _captureBuffer = [];
        private DateTimeOffset _lastDetectionAtUtc;
        private DateTimeOffset _lastAnalyzedAtUtc;

        public void Start()
        {
            _runTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _runTask?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }

            _cts.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var audioClient = await ActivateProcessLoopbackClientAsync(processId, cancellationToken).ConfigureAwait(false);
                audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                    ProcessLoopbackBufferDurationHns,
                    0,
                    ProcessLoopbackFormat,
                    Guid.Empty);

                using var captureReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                audioClient.SetEventHandle(captureReadyEvent.SafeWaitHandle.DangerousGetHandle());

                using var captureClient = audioClient.AudioCaptureClient;
                audioClient.Start();
                logger.LogInformation("Started process-loopback audio capture for pid {ProcessId}.", processId);

                while (!cancellationToken.IsCancellationRequested)
                {
                    captureReadyEvent.WaitOne(250);
                    cancellationToken.ThrowIfCancellationRequested();
                    DrainPackets(captureClient, timeProvider.GetUtcNow(), publishDetection);
                }

                audioClient.Stop();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Process-loopback audio capture failed for pid {ProcessId}.", processId);
            }
        }

        private void DrainPackets(
            AudioCaptureClient captureClient,
            DateTimeOffset observedAtUtc,
            Action<AudioDeathDetection> publish)
        {
            while (captureClient.GetNextPacketSize() > 0)
            {
                var bufferPointer = captureClient.GetBuffer(out var framesRead, out var flags);
                try
                {
                    var chunk = flags.HasFlag(AudioClientBufferFlags.Silent)
                        ? new float[framesRead]
                        : ReadPacketSamples(bufferPointer, framesRead);

                    if (chunk.Length == 0)
                    {
                        continue;
                    }

                    _captureBuffer = AppendAndTrim(_captureBuffer, chunk, ProcessLoopbackFormat.SampleRate * 6);
                }
                finally
                {
                    captureClient.ReleaseBuffer(framesRead);
                }
            }

            if (observedAtUtc - _lastAnalyzedAtUtc < AnalysisInterval || _captureBuffer.Length < ProcessLoopbackFormat.SampleRate)
            {
                return;
            }

            _lastAnalyzedAtUtc = observedAtUtc;
            AnalyzeLatestWindow(observedAtUtc, publish);
        }

        private float[] ReadPacketSamples(IntPtr bufferPointer, int framesRead)
        {
            if (bufferPointer == IntPtr.Zero || framesRead <= 0)
            {
                return [];
            }

            var byteCount = framesRead * ProcessLoopbackFormat.BlockAlign;
            var packetBytes = new byte[byteCount];
            Marshal.Copy(bufferPointer, packetBytes, 0, byteCount);
            return ConvertToMonoSamples(packetBytes, byteCount, ProcessLoopbackFormat);
        }

        private void AnalyzeLatestWindow(DateTimeOffset observedAtUtc, Action<AudioDeathDetection> publish)
        {
            var resampled = ResampleToTarget(_captureBuffer, ProcessLoopbackFormat.SampleRate, TargetSampleRate);
            var match = AudioFingerprintMatcher.Match(reference, resampled, TargetSampleRate);
            if (match.MatchingHashes < MinimumMatchingHashes
                || match.Confidence < MinimumConfidence
                || match.AlignedFrameCoverage < MinimumAlignedFrameCoverage
                || match.EnvelopeSimilarity < MinimumEnvelopeSimilarity
                || match.PeakSequenceSimilarity < MinimumPeakSequenceSimilarity)
            {
                return;
            }

            if (observedAtUtc - _lastDetectionAtUtc < AnalysisInterval)
            {
                return;
            }

            _lastDetectionAtUtc = observedAtUtc;
            publish(new AudioDeathDetection(processId, observedAtUtc, match.Confidence, referenceName));
            logger.LogInformation(
                "Death audio matched for pid {ProcessId}. Confidence={Confidence:F3} MatchingHashes={MatchingHashes} OffsetFrames={OffsetFrames} FrameCoverage={FrameCoverage:F3} Envelope={Envelope:F3} PeakSequence={PeakSequence:F3}",
                processId,
                match.Confidence,
                match.MatchingHashes,
                match.OffsetFrames,
                match.AlignedFrameCoverage,
                match.EnvelopeSimilarity,
                match.PeakSequenceSimilarity);
        }

        private static async Task<AudioClient> ActivateProcessLoopbackClientAsync(int pid, CancellationToken cancellationToken)
        {
            var activation = new AudioClientActivationParams
            {
                ActivationType = 1,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = (uint)pid,
                    ProcessLoopbackMode = 0,
                },
            };

            var activationMemory = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
            var propVariantMemory = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
            try
            {
                Marshal.StructureToPtr(activation, activationMemory, false);
                var propVariant = new PropVariant
                {
                    vt = (ushort)VarEnum.VT_BLOB,
                    blobVal = new Blob
                    {
                        Length = Marshal.SizeOf<AudioClientActivationParams>(),
                        Data = activationMemory,
                    },
                };
                Marshal.StructureToPtr(propVariant, propVariantMemory, false);

                var completionHandler = new ActivateAudioInterfaceCompletionHandler();
                var hr = ActivateAudioInterfaceAsync(
                    "VAD\\Process_Loopback",
                    typeof(IAudioClient).GUID,
                    propVariantMemory,
                    completionHandler,
                    out _);
                Marshal.ThrowExceptionForHR(hr);

                var activated = await completionHandler.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return new AudioClient((IAudioClient)activated);
            }
            finally
            {
                Marshal.FreeHGlobal(activationMemory);
                Marshal.FreeHGlobal(propVariantMemory);
            }
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Length;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort vt;

        [FieldOffset(8)]
        public Blob blobVal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public uint ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivateAudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public TaskCompletionSource<object> TaskSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object> Task => TaskSource.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out var hr, out var activatedInterface);
                if (hr < 0)
                {
                    TaskSource.SetException(Marshal.GetExceptionForHR(hr)!);
                }
                else
                {
                    TaskSource.SetResult(activatedInterface);
                }
            }
            catch (Exception ex)
            {
                TaskSource.SetException(ex);
            }
        }
    }
}
