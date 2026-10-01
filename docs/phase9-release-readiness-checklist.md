# Phase 9 Release-Readiness Checklist

This checklist is the productized regression gate for Task 10 / subtask 4.

Run the full gate from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Verify-L2MonitorReleaseReadiness.ps1
```

## Automated gate

1. Telegram direct mode
   - Command: `dotnet test .\L2Monitor.Agent.Tests\L2Monitor.Agent.Tests.csproj -c Release --filter "FullyQualifiedName=L2Monitor.Agent.Tests.Api.LocalControlApiContractTests.Status_ReportsTelegramDirectModeConfiguredWhenLocalSettingsAndSecretArePresent"`
   - Guarantees: the agent API can be configured into Local delivery mode with Telegram enabled, persists the target chat id and bot-token presence, and reports Telegram direct delivery as configured without requiring a live Telegram round-trip.
2. Cloud offline mode
   - Command: `dotnet test .\L2Monitor.Agent.Tests\L2Monitor.Agent.Tests.csproj -c Release --filter "FullyQualifiedName=L2Monitor.Agent.Tests.Api.LocalControlApiContractTests.Status_ReportsCloudBackendMisconfiguredWhenBackendBaseUrlIsInvalid"`
   - Guarantees: an unreachable cloud backend in Cloud mode reports `unreachable` health and records a support-visible `backend_degraded` incident instead of failing silently.
3. Tray reconnect behavior
   - Command: `dotnet test .\L2Monitor.Tray.Tests\L2Monitor.Tray.Tests.csproj -c Release --filter "FullyQualifiedName~L2Monitor.Tray.Tests.Api.LocalControlApiClientTests"`
   - Guarantees: the tray keeps using the active endpoint until rediscovery is needed, updates cached ports from the agent dashboard, heals stale bootstrap tokens, and supports recovery-mode requests without an API token.
4. Shared bootstrap contract
   - Command: `dotnet test .\L2Monitor.Tray.Tests\L2Monitor.Tray.Tests.csproj -c Release --filter "FullyQualifiedName~L2Monitor.Tray.Tests.Bootstrap.AgentBootstrapDiscoveryTests"`
   - Guarantees: tray bootstrap discovery reads the dedicated bootstrap export contract without resolving the agent data root directly.
5. Autostart and install persistence
   - Command: `powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Verify-L2MonitorInstaller.ps1`
   - Guarantees: install/upgrade/uninstall preserve the supported app layout, keep durable agent data, register the single user-facing Aden+ autostart entry, preserve it across upgrade, and remove current and legacy Run entries on uninstall.

## Explicitly deferred

- Legacy `MonitorEngine` regression coverage. The live agent no longer uses that state machine; keeping it in the release gate produced false confidence about dead logic instead of protecting current runtime behavior.

- Live Telegram Bot API delivery against external network dependencies.
- Live cloud backend reachability against a real deployed backend.
- Real desktop logon/manual tray UX smoke checks outside the automated harness.

Those remain environment-dependent validation steps and are intentionally kept out of the deterministic in-repo regression gate.
