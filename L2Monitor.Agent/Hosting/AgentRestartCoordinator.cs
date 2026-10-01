using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace L2Monitor.Agent.Hosting;

internal interface IAgentRestartCoordinator
{
    AgentRestartDecision ScheduleRestart();
}

internal sealed record AgentRestartDecision(bool ShouldStop, string Outcome, string Message);

internal sealed class AgentRestartCoordinator : IAgentRestartCoordinator
{
    private readonly AgentLaunchOptions _launchOptions;
    private readonly ILogger<AgentRestartCoordinator> _logger;

    public AgentRestartCoordinator(AgentLaunchOptions launchOptions, ILogger<AgentRestartCoordinator> logger)
    {
        _launchOptions = launchOptions;
        _logger = logger;
    }

    public AgentRestartDecision ScheduleRestart()
    {
        var plan = AgentRestartSupport.TryCreateRelaunchPlan(
            Environment.ProcessPath,
            ResolveEntryAssemblyPath(),
            _launchOptions.ForwardedArgs,
            Environment.ProcessId);

        if (plan is null)
        {
            _logger.LogWarning("Agent restart was requested, but no relaunch plan could be constructed.");
            return new AgentRestartDecision(
                ShouldStop: false,
                Outcome: "rejected",
                Message: "Agent restart is unavailable in the current launch mode.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = plan.FileName,
                WorkingDirectory = plan.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in plan.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogWarning("Agent restart was requested, but relaunch process creation returned null.");
                return new AgentRestartDecision(
                    ShouldStop: false,
                    Outcome: "rejected",
                    Message: "Agent restart could not launch a replacement process.");
            }

            _logger.LogInformation(
                "Agent replacement process queued. FileName={FileName} Arguments={Arguments}",
                plan.FileName,
                string.Join(" ", plan.Arguments));

            return new AgentRestartDecision(
                ShouldStop: true,
                Outcome: "accepted",
                Message: "Agent restart has been queued.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent restart was requested, but replacement process launch failed.");
            return new AgentRestartDecision(
                ShouldStop: false,
                Outcome: "rejected",
                Message: $"Agent restart could not launch a replacement process: {ex.Message}");
        }
    }

    private static string? ResolveEntryAssemblyPath()
    {
        var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return null;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }
}

internal sealed record AgentRelaunchPlan(string FileName, string WorkingDirectory, IReadOnlyList<string> Arguments);

internal static class AgentRestartSupport
{
    private const string RestartParentArgument = "--restart-parent";

    public static int? ParseRestartParent(string[] args, out string[] filteredArgs)
    {
        var remaining = new List<string>(args.Length);
        int? restartParent = null;

        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], RestartParentArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length
                    && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid)
                    && parsedPid > 0)
                {
                    restartParent = parsedPid;
                    index++;
                    continue;
                }

                continue;
            }

            remaining.Add(args[index]);
        }

        filteredArgs = remaining.ToArray();
        return restartParent;
    }

    public static void WaitForParentExit(int? restartParent, int timeoutMilliseconds = 15000)
    {
        if (restartParent is not > 0)
        {
            return;
        }

        try
        {
            using var parent = Process.GetProcessById(restartParent.Value);
            parent.WaitForExit(timeoutMilliseconds);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public static AgentRelaunchPlan? TryCreateRelaunchPlan(
        string? processPath,
        string? entryAssemblyPath,
        IReadOnlyList<string>? forwardedArgs,
        int restartParentPid)
    {
        if (restartParentPid <= 0)
        {
            return null;
        }

        var sanitizedArgs = SanitizeForwardedArgs(forwardedArgs);
        sanitizedArgs.Add(RestartParentArgument);
        sanitizedArgs.Add(restartParentPid.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(processPath)
            && File.Exists(processPath)
            && !IsDotnetHost(processPath))
        {
            return new AgentRelaunchPlan(
                processPath,
                Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory,
                sanitizedArgs);
        }

        if (!string.IsNullOrWhiteSpace(processPath)
            && File.Exists(processPath)
            && IsDotnetHost(processPath)
            && !string.IsNullOrWhiteSpace(entryAssemblyPath)
            && File.Exists(entryAssemblyPath))
        {
            var arguments = new List<string> { entryAssemblyPath };
            arguments.AddRange(sanitizedArgs);
            return new AgentRelaunchPlan(
                processPath,
                Path.GetDirectoryName(entryAssemblyPath) ?? AppContext.BaseDirectory,
                arguments);
        }

        if (!string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            var executablePath = Path.ChangeExtension(entryAssemblyPath, ".exe");
            if (File.Exists(executablePath))
            {
                return new AgentRelaunchPlan(
                    executablePath,
                    Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                    sanitizedArgs);
            }
        }

        return null;
    }

    private static bool IsDotnetHost(string processPath) =>
        string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase);

    private static List<string> SanitizeForwardedArgs(IReadOnlyList<string>? forwardedArgs)
    {
        var result = new List<string>();
        if (forwardedArgs is null)
        {
            return result;
        }

        for (var index = 0; index < forwardedArgs.Count; index++)
        {
            if (string.Equals(forwardedArgs[index], RestartParentArgument, StringComparison.OrdinalIgnoreCase))
            {
                index += 1;
                continue;
            }

            result.Add(forwardedArgs[index]);
        }

        return result;
    }
}
