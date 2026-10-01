using Microsoft.Win32;
using System.IO;

namespace L2Monitor.Tray.Bootstrap;

internal interface IAutostartService
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

internal sealed class WindowsAutostartService : IAutostartService
{
    internal const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunValueName = "Aden+";

    private readonly string _executablePath;

    public WindowsAutostartService(string? executablePath = null)
    {
        _executablePath = Path.GetFullPath(executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к Aden+.exe."));
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
        var storedCommand = key?.GetValue(RunValueName) as string;
        return CommandsMatch(storedCommand, BuildRunCommand(_executablePath));
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть раздел автозапуска Windows.");

        if (enabled)
        {
            key.SetValue(RunValueName, BuildRunCommand(_executablePath), RegistryValueKind.String);
            return;
        }

        key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    internal static string BuildRunCommand(string executablePath) => $"\"{Path.GetFullPath(executablePath)}\" --background";

    internal static bool CommandsMatch(string? actual, string expected) =>
        string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}

internal sealed class DisabledAutostartService : IAutostartService
{
    public bool IsEnabled() => false;
    public void SetEnabled(bool enabled) { }
}
