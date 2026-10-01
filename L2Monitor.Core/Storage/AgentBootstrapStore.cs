using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace L2Monitor.Core.Storage;

public static class AgentStorageLayout
{
    public const string AgentHomeEnvVar = "L2MONITOR_AGENT_HOME";
    public const string InstallMetadataFileName = "install.json";
    public const string BootstrapFileName = "bootstrap.dat";
    public const string RuntimeDirectoryName = "runtime";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ResolveRootDirectory(string? baseDirectory = null)
    {
        var overridePath = Environment.GetEnvironmentVariable(AgentHomeEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var installedPath = TryResolveInstalledRootDirectory(baseDirectory);
        if (!string.IsNullOrWhiteSpace(installedPath))
        {
            return installedPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "L2Monitor", "agent");
    }

    public static string ResolveBootstrapDirectory(string? directoryOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(directoryOverride))
        {
            return directoryOverride;
        }

        var overridePath = Environment.GetEnvironmentVariable(AgentHomeEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "L2Monitor", RuntimeDirectoryName);
    }

    public static string? TryResolveInstalledRootDirectory(string? baseDirectory = null)
    {
        var resolvedBaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        var appDirectory = Directory.GetParent(resolvedBaseDirectory);
        if (appDirectory is null)
        {
            return null;
        }

        var metadataPath = Path.Combine(appDirectory.FullName, InstallMetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<InstalledLayoutMetadata>(File.ReadAllText(metadataPath), JsonOptions);
            return string.IsNullOrWhiteSpace(metadata?.AgentDataRoot)
                ? null
                : metadata.AgentDataRoot.Trim();
        }
        catch
        {
            return null;
        }
    }

    private sealed record InstalledLayoutMetadata(string? AgentDataRoot);
}

public sealed record AgentBootstrapRecord(int LoopbackPort, string? LocalApiToken);

[SupportedOSPlatform("windows")]
public static class AgentBootstrapStore
{
    private static readonly byte[] BootstrapEntropy = Encoding.UTF8.GetBytes("L2Monitor.Agent.BootstrapStore.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AgentBootstrapRecord? TryLoad()
        => TryLoadCore(null);

    public static AgentBootstrapRecord? TryLoad(string bootstrapDirectoryOverride)
        => TryLoadCore(bootstrapDirectoryOverride);

    public static void Save(AgentBootstrapRecord bootstrap)
        => SaveCore(null, bootstrap);

    public static void Save(string bootstrapDirectoryOverride, AgentBootstrapRecord bootstrap)
        => SaveCore(bootstrapDirectoryOverride, bootstrap);

    private static AgentBootstrapRecord? TryLoadCore(string? bootstrapDirectoryOverride)
    {
        var path = Path.Combine(
            AgentStorageLayout.ResolveBootstrapDirectory(bootstrapDirectoryOverride),
            AgentStorageLayout.BootstrapFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var payload = File.ReadAllBytes(path);
            if (payload.Length == 0)
            {
                return null;
            }

            var plainBytes = ProtectedData.Unprotect(payload, BootstrapEntropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AgentBootstrapRecord>(plainBytes, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCore(string? bootstrapDirectoryOverride, AgentBootstrapRecord bootstrap)
    {
        var bootstrapDirectory = AgentStorageLayout.ResolveBootstrapDirectory(bootstrapDirectoryOverride);
        Directory.CreateDirectory(bootstrapDirectory);

        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(bootstrap, JsonOptions);
        var protectedBytes = ProtectedData.Protect(plainBytes, BootstrapEntropy, DataProtectionScope.CurrentUser);
        var path = Path.Combine(bootstrapDirectory, AgentStorageLayout.BootstrapFileName);

        WriteFileAtomically(path, stream => stream.Write(protectedBytes));
    }

    private static void WriteFileAtomically(string path, Action<FileStream> writePayload)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Bootstrap file path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                writePayload(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
