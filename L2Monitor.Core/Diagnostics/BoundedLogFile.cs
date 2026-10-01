using System.Text;

namespace L2Monitor.Core.Diagnostics;

public sealed class BoundedLogFile
{
    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly int _maxFileSizeBytes;
    private readonly int _retainedFileCount;

    public BoundedLogFile(string filePath, int maxFileSizeBytes = 262_144, int retainedFileCount = 5)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A log file path is required.", nameof(filePath));
        }

        if (maxFileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileSizeBytes));
        }

        if (retainedFileCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedFileCount));
        }

        _filePath = filePath;
        _maxFileSizeBytes = maxFileSizeBytes;
        _retainedFileCount = retainedFileCount;
    }

    public void WriteLine(string line)
    {
        var payload = Encoding.UTF8.GetBytes((line ?? string.Empty) + Environment.NewLine);

        lock (_sync)
        {
            try
            {
                var directoryPath = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                RotateIfNeeded(payload.Length);
                using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(payload, 0, payload.Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve caller stability when bounded persistence is temporarily unavailable.
            }
        }
    }

    private void RotateIfNeeded(int incomingPayloadLength)
    {
        var currentLength = File.Exists(_filePath)
            ? new FileInfo(_filePath).Length
            : 0L;

        if (currentLength + incomingPayloadLength <= _maxFileSizeBytes)
        {
            return;
        }

        var archiveCount = _retainedFileCount - 1;
        if (archiveCount == 0)
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }

            return;
        }

        var oldestArchivePath = GetArchivePath(archiveCount);
        if (File.Exists(oldestArchivePath))
        {
            File.Delete(oldestArchivePath);
        }

        for (var index = archiveCount - 1; index >= 1; index--)
        {
            var sourcePath = GetArchivePath(index);
            if (File.Exists(sourcePath))
            {
                File.Move(sourcePath, GetArchivePath(index + 1));
            }
        }

        if (File.Exists(_filePath))
        {
            File.Move(_filePath, GetArchivePath(1));
        }
    }

    private string GetArchivePath(int index)
    {
        var directoryPath = Path.GetDirectoryName(_filePath) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(_filePath);
        var extension = Path.GetExtension(_filePath);
        return Path.Combine(directoryPath, $"{fileNameWithoutExtension}.{index}{extension}");
    }
}
