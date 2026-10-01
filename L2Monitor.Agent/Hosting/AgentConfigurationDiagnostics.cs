namespace L2Monitor.Agent.Hosting;

internal sealed record AgentConfigurationDiagnostic(
    string Kind,
    string Severity,
    string Summary);
