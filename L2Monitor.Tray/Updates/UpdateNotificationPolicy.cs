using System.Text.Json;
using System.IO;
using L2Monitor.Core.Api;

namespace L2Monitor.Tray.Updates;

internal static class UpdateNotificationPolicy
{
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromDays(1);

    public static bool ShouldNotify(
        ClientUpdateDto update,
        UpdateNotificationRecord? previous,
        DateTimeOffset now)
    {
        if (!update.IsUpdateAvailable
            || string.IsNullOrWhiteSpace(update.LatestVersion))
        {
            return false;
        }

        return previous is null
            || !string.Equals(previous.Version, update.LatestVersion, StringComparison.Ordinal)
            || now - previous.NotifiedAtUtc >= ReminderInterval;
    }
}

internal sealed record UpdateNotificationRecord(string Version, DateTimeOffset NotifiedAtUtc);

internal sealed class UpdateNotificationStateStore
{
    private const int MaxStateBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public UpdateNotificationStateStore(string? rootDirectory = null)
    {
        var root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "L2Monitor",
            "tray");
        _path = Path.Combine(root, "update-notification.json");
    }

    public UpdateNotificationRecord? Load()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length is <= 0 or > MaxStateBytes)
            {
                return null;
            }

            return JsonSerializer.Deserialize<UpdateNotificationRecord>(File.ReadAllBytes(_path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(UpdateNotificationRecord record)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Update notification state path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
