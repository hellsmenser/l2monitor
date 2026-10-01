using L2Monitor.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace L2Monitor.Core.Tests.Diagnostics;

public sealed class BoundedLogFileTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "L2Monitor.Core.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteLine_RotatesAndLimitsRetainedFiles()
    {
        Directory.CreateDirectory(_rootPath);
        var logPath = Path.Combine(_rootPath, "agent.log");
        var logFile = new BoundedLogFile(logPath, maxFileSizeBytes: 90, retainedFileCount: 3);

        for (var index = 0; index < 12; index++)
        {
            logFile.WriteLine($"line-{index:D2}-abcdefghijklmnopqrstuvwxyz");
        }

        var files = Directory.GetFiles(_rootPath, "*.log")
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["agent.1.log", "agent.2.log", "agent.log"], files);

        var combinedContents = string.Join(
            Environment.NewLine,
            Directory.GetFiles(_rootPath, "*.log")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));

        Assert.DoesNotContain("line-00", combinedContents, StringComparison.Ordinal);
        Assert.Contains("line-11", combinedContents, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteLine_WithSingleRetainedFile_KeepsOnlyActiveLog()
    {
        Directory.CreateDirectory(_rootPath);
        var logPath = Path.Combine(_rootPath, "agent.log");
        var logFile = new BoundedLogFile(logPath, maxFileSizeBytes: 70, retainedFileCount: 1);

        for (var index = 0; index < 6; index++)
        {
            logFile.WriteLine($"single-{index:D2}-abcdefghijklmnopqrstuvwxyz");
        }

        var files = Directory.GetFiles(_rootPath)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["agent.log"], files);

        var contents = File.ReadAllText(logPath);
        Assert.DoesNotContain("single-00", contents, StringComparison.Ordinal);
        Assert.Contains("single-05", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteLine_WhenArchiveIsLocked_DoesNotThrowAndRecoversLater()
    {
        Directory.CreateDirectory(_rootPath);
        var logPath = Path.Combine(_rootPath, "agent.log");
        var logFile = new BoundedLogFile(logPath, maxFileSizeBytes: 70, retainedFileCount: 2);

        logFile.WriteLine("line-01-abcdefghijklmnopqrstuvwxyz");
        logFile.WriteLine("line-02-abcdefghijklmnopqrstuvwxyz");

        var archivePath = Path.Combine(_rootPath, "agent.1.log");
        Assert.True(File.Exists(archivePath));

        using (new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var exception = Record.Exception(() => logFile.WriteLine("line-03-abcdefghijklmnopqrstuvwxyz"));
            Assert.Null(exception);
        }

        logFile.WriteLine("line-04-abcdefghijklmnopqrstuvwxyz");

        var combinedContents = string.Join(
            Environment.NewLine,
            Directory.GetFiles(_rootPath, "*.log")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));

        Assert.DoesNotContain("line-03", combinedContents, StringComparison.Ordinal);
        Assert.Contains("line-04", combinedContents, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_Log_WhenActiveFileIsLocked_DoesNotThrowAndWritesAfterUnlock()
    {
        Directory.CreateDirectory(_rootPath);
        var logPath = Path.Combine(_rootPath, "agent.log");
        File.WriteAllText(logPath, "seed");

        var provider = new BoundedFileLoggerProvider(logPath, maxFileSizeBytes: 256, retainedFileCount: 2);
        var logger = provider.CreateLogger("Tests");

        using (new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var exception = Record.Exception(() => logger.Log(
                LogLevel.Error,
                new EventId(7, "locked"),
                "first message",
                null,
                static (state, _) => state));

            Assert.Null(exception);
        }

        logger.Log(
            LogLevel.Error,
            new EventId(8, "unlocked"),
            "second message",
            null,
            static (state, _) => state);

        var contents = File.ReadAllText(logPath);
        Assert.DoesNotContain("first message", contents, StringComparison.Ordinal);
        Assert.Contains("second message", contents, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
