using System.Diagnostics;
using System.IO;

namespace L2Monitor.Tray.Bootstrap;

internal sealed record AgentLaunchResult(bool IsAvailable, bool WasStarted, string? ExecutablePath, string? ErrorMessage);

internal sealed class AgentProcessLauncher
{
    private static readonly string[] AgentProcessNames = ["AdenPlus.Agent", "L2Monitor.Agent"];

    private readonly string _baseDirectory;

    public AgentProcessLauncher(string? baseDirectory = null)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
    }

    public AgentLaunchResult EnsureStarted()
    {
        if (IsAgentRunning())
        {
            return new AgentLaunchResult(true, false, null, null);
        }

        var executablePath = ResolveExecutablePath(_baseDirectory);
        if (executablePath is null)
        {
            return new AgentLaunchResult(
                false,
                false,
                null,
                "Не найден внутренний компонент Aden+. Распакуйте архив целиком и запустите приложение снова.");
        }

        try
        {
            using var process = Process.Start(CreateStartInfo(executablePath));
            if (process is null)
            {
                return new AgentLaunchResult(false, false, executablePath, "Windows не смогла запустить фоновый компонент Aden+.");
            }

            return new AgentLaunchResult(true, true, executablePath, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new AgentLaunchResult(false, false, executablePath, $"Не удалось запустить фоновый компонент Aden+: {ex.Message}");
        }
    }

    internal static string? ResolveExecutablePath(string baseDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "runtime", "agent", "AdenPlus.Agent.exe"),
            Path.Combine(baseDirectory, "agent", "AdenPlus.Agent.exe"),
            Path.Combine(baseDirectory, "..", "agent", "AdenPlus.Agent.exe"),
            Path.Combine(baseDirectory, "agent", "L2Monitor.Agent.exe"),
            Path.Combine(baseDirectory, "..", "agent", "L2Monitor.Agent.exe"),
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath) => new()
    {
        FileName = executablePath,
        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    };

    private static bool IsAgentRunning()
    {
        foreach (var processName in AgentProcessNames)
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Any(static process => !process.HasExited))
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }
}
