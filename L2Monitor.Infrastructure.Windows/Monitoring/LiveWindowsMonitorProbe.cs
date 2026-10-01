using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace L2Monitor.Infrastructure.Windows;

internal sealed class LiveWindowsMonitorProbe(TimeProvider timeProvider) : IWindowsMonitorProbe
{
    private const int GameplayPort = 7777;
    private const int SessionPort = 17453;
    private const int ErrorInsufficientBuffer = 122;
    private readonly TimeProvider _timeProvider = timeProvider;

    public Task<WindowsMonitorProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var capturedAtUtc = _timeProvider.GetUtcNow();
        var tcpRows = GetEstablishedTcpRows()
            .Where(static row => row.RemotePort is GameplayPort or SessionPort)
            .GroupBy(static row => row.OwningPid)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        var exportedConnections = new List<WindowsMonitorConnectionRecord>();
        var activeClientCount = 0;
        var candidateProcessCount = 0;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!IsCandidateProcess(process))
                {
                    continue;
                }

                candidateProcessCount++;

                var processRows = tcpRows.TryGetValue(process.Id, out var rows)
                    ? rows
                    : [];
                var assessment = AssessRemotePorts(processRows.Select(static row => row.RemotePort));

                if (assessment.CountsAsActive)
                {
                    activeClientCount++;
                }

                if (string.Equals(assessment.State, "Disconnected", StringComparison.Ordinal))
                {
                    continue;
                }

                exportedConnections.Add(new WindowsMonitorConnectionRecord(
                    Id: $"pid-{process.Id}",
                    ProcessName: process.ProcessName,
                    WindowTitle: SafeGetWindowTitle(process),
                    State: assessment.State,
                    ObservedAtUtc: capturedAtUtc,
                    Forensics: BuildForensics(assessment, processRows)));
            }
        }

        exportedConnections.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id));

        return Task.FromResult(new WindowsMonitorProbeResult(
            CapturedAtUtc: capturedAtUtc,
            CandidateProcessCount: candidateProcessCount,
            GameConnectionCount: activeClientCount,
            Connections: exportedConnections));
    }

    internal static GameConnectionAssessment AssessRemotePorts(IEnumerable<int> remotePorts)
    {
        var ports = remotePorts.Distinct().ToHashSet();
        var hasGameplayPort = ports.Contains(GameplayPort);
        var hasSessionPort = ports.Contains(SessionPort);

        if (hasGameplayPort && hasSessionPort)
        {
            return new GameConnectionAssessment("Connected", CountsAsActive: true);
        }

        if (hasGameplayPort)
        {
            return new GameConnectionAssessment(
                "GameplayOnly",
                CountsAsActive: true,
                new AgentConnectionForensics(
                    "gameplay_only",
                    "Gameplay socket observed without session socket.",
                    EstablishedRowCount: ports.Count,
                    ObservedRemotePorts: ports.OrderBy(static port => port).ToArray()));
        }

        if (hasSessionPort)
        {
            return new GameConnectionAssessment("Connecting", CountsAsActive: false);
        }

        return new GameConnectionAssessment("Disconnected", CountsAsActive: false);
    }

    private static bool IsCandidateProcess(Process process)
    {
        try
        {
            return process.Id > 0
                && process.ProcessName.StartsWith("L2", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeGetWindowTitle(Process process)
    {
        try
        {
            return string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? null
                : process.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<OwnedTcpRow> GetEstablishedTcpRows()
    {
        var bufferSize = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, order: true, af: 2, tableClass: TcpTableClass.TcpTableOwnerPidAll, reserved: 0);
        if (result != ErrorInsufficientBuffer)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedTcpTable(buffer, ref bufferSize, order: true, af: 2, tableClass: TcpTableClass.TcpTableOwnerPidAll, reserved: 0);
            if (result != 0)
            {
                return [];
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rows = new List<OwnedTcpRow>(rowCount);
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (var i = 0; i < rowCount; i++)
            {
                var nativeRow = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                if (nativeRow.State == TcpState.Established)
                {
                    rows.Add(new OwnedTcpRow(
                        nativeRow.OwningPid,
                        ToPort(nativeRow.RemotePort),
                        new IPAddress(nativeRow.RemoteAddress)));
                }

                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ToPort(uint rawPort)
    {
        var bytes = BitConverter.GetBytes(rawPort);
        return (bytes[0] << 8) + bytes[1];
    }

    private static AgentConnectionForensics? BuildForensics(GameConnectionAssessment assessment, IReadOnlyList<OwnedTcpRow> rows)
    {
        if (assessment.Forensics is null)
        {
            return null;
        }

        var observedPorts = rows
            .Select(static row => row.RemotePort)
            .Distinct()
            .OrderBy(static port => port)
            .ToArray();

        var establishedRowCount = rows.Count;
        return assessment.Forensics with
        {
            EstablishedRowCount = establishedRowCount,
            ObservedRemotePorts = observedPorts,
        };
    }

    internal sealed record GameConnectionAssessment(
        string State,
        bool CountsAsActive,
        AgentConnectionForensics? Forensics = null);

    private sealed record OwnedTcpRow(int OwningPid, int RemotePort, IPAddress RemoteAddress);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public TcpState State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public int OwningPid;
    }

    private enum TcpTableClass
    {
        TcpTableBasicListener,
        TcpTableBasicConnections,
        TcpTableBasicAll,
        TcpTableOwnerPidListener,
        TcpTableOwnerPidConnections,
        TcpTableOwnerPidAll,
        TcpTableOwnerModuleListener,
        TcpTableOwnerModuleConnections,
        TcpTableOwnerModuleAll
    }

    private enum TcpState : uint
    {
        Established = 5,
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int sizePointer,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int af,
        TcpTableClass tableClass,
        uint reserved);
}
