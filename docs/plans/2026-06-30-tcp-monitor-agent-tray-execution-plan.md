# L2Monitor Agent + Tray Execution Plan

> For implementers: follow the accepted design in `2026-06-30-tcp-monitor-agent-tray-design.md`.
>
> This is a phased execution plan for turning `tcp-monitor-mvp` into the new `agent + tray` product baseline.

## Goal

Ship a Windows product where:

- a hidden user-level agent starts at login
- the agent performs L2 detection and notification delivery
- a tray app provides visibility and control
- local Telegram mode and cloud mode are both supported
- secrets are removed from plain config and stored safely

## Planning Rules

- Preserve current detection behavior before improving it.
- Keep the agent as the single source of truth.
- Avoid introducing tray-owned logic.
- Keep bootstrap discovery as an agent-owned runtime contract, not tray access into agent storage.
- Remove secret leakage early.
- Keep cloud mode production-shaped even before the backend exists.

## Deferred Backend Planning Note

The accepted baseline for this plan remains the user-level agent plus tray design. Future backend protocol work is intentionally deferred, but the current architectural recommendation should guide later implementation planning:

- retain REST for bootstrap/admin/config flows
- likely use one persistent WebSocket per running agent for liveness if fast unreachable detection is required
- think in terms of `license -> machines -> sessions` rather than a single key-to-connection mapping
- represent `agent unreachable` separately from gameplay disconnect incidents
- finalize the protocol later in parallel with real backend implementation on both sides

## Phase 0: Repo and Baseline Cleanup

### Objective

Prepare the repo so implementation can proceed without dragging MVP assumptions forward blindly.

### Deliverables

- `docs/plans/2026-06-30-tcp-monitor-agent-tray-design.md`
- this execution plan
- clear repo target structure
- explicit note about current secret leakage in build outputs

### Tasks

1. Create a `docs/plans/` planning area and keep design docs there.
2. Decide target solution structure for:
   - `L2Monitor.Core`
   - `L2Monitor.Infrastructure.Windows`
   - `L2Monitor.Agent`
   - `L2Monitor.Tray`
3. Audit current build/output/config leakage:
   - copied `appsettings.json`
   - published artifacts with secrets
4. Define migration constraints:
   - no behavior regressions in detection semantics
   - no tray-owned notification sending
   - no service-specific assumptions in baseline

### Dependencies

- none

### Notes

- This phase is mostly done at the planning level, but the codebase still needs the actual cleanup work.

## Phase 1: Extract Core Domain Logic

### Objective

Separate reusable logic from the current console MVP.

### Deliverables

- `L2Monitor.Core` project
- extracted state machine and shared models
- minimal tests for core behavior

### Tasks

1. Create `L2Monitor.Core`.
2. Move domain models into core:
   - client/process state
   - connection summary
   - incident/event model
   - health/status DTOs
   - settings DTOs
3. Move monitor state engine into core.
4. Introduce abstractions for:
   - process/window discovery
   - TCP snapshot provider
   - notification transport
   - config provider
   - secret store
5. Replace direct console-loop coupling with a core orchestrator shape.
6. Add tests around:
   - timeout detection
   - recovery
   - process exit behavior
   - configuration validation

### Dependencies

- Phase 0

### Can Run In Parallel

- model extraction
- state-machine tests
- contract/interface definition

## Phase 2: Build Windows Infrastructure Layer

### Objective

Move Windows-specific plumbing behind explicit adapters.

### Deliverables

- `L2Monitor.Infrastructure.Windows` project
- Win32/process/TCP adapters
- config persistence implementation
- DPAPI secret store implementation

### Tasks

1. Create `L2Monitor.Infrastructure.Windows`.
2. Move or rewrite current Windows-specific logic into adapters:
   - window-title lookup
   - process enumeration
   - TCP table lookup by PID
3. Implement config file persistence for non-secrets.
4. Implement DPAPI-backed secret store.
5. Add masking helpers for secret presence/status.
6. Add tests where feasible for:
   - secret store roundtrip
   - config validation and `not configured yet` behavior

### Dependencies

- Phase 1 contracts should exist first

### Can Run In Parallel

- discovery adapters
- secret store
- config persistence

## Phase 3: Create the Hidden User-Level Agent

### Objective

Replace the console app with a hidden user-level runtime that owns detection and notifications.

### Deliverables

- `L2Monitor.Agent` project
- startup path without visible console
- runtime loop owned by the agent
- agent-owned health snapshot and incident buffer

### Tasks

1. Create `L2Monitor.Agent`.
2. Build the composition root for core + Windows infrastructure.
3. Replace current `Thread.Sleep` loop with a cancellable hosted runtime loop.
4. Persist current runtime snapshot in memory for tray/API access.
5. Add structured logging.
6. Add reload/restart-safe configuration application.
7. Ensure the agent can run without a visible console window.

### Dependencies

- Phase 1
- Phase 2

### Can Run In Parallel

- hosted runtime scaffolding
- logging/health plumbing
- config application behavior

## Phase 4: Delivery Abstraction and Modes

### Objective

Introduce real mode selection while keeping the cloud agent protocol minimal.

### Deliverables

- `TelegramDirectTransport`
- `AgentBackendConnectionService`
- `NotificationRouter`
- delivery health model

### Tasks

1. Wrap current Telegram delivery behind transport abstraction.
2. Move current message formatting into a reusable notification path.
3. Implement the backend connection against the real contract:
   - one `GET /public/client-version` per process
   - one active, reconnectable `WebSocket /ws/agent`
4. Do not add HTTP relay endpoints or unrelated server-side product knowledge to the agent or tray.
5. Add delivery health states:
   - healthy
   - misconfigured
   - auth failed
   - unreachable
   - protocol error
6. Ensure cloud mode reports honest red/offline states when backend does not exist.
7. Add test notification flow.

### Deferred Follow-Up For Later Backend Work

When backend implementation begins, revisit this phase and finalize:

- session identity and reconnect rules
- machine/session mapping beneath a license
- server-side representation of backend outages versus actual game disconnect incidents

### Dependencies

- Phase 3 agent runtime

### Can Run In Parallel

- Telegram transport refactor
- backend WebSocket transport
- health state modeling

## Phase 5: Secret and Config Hardening

### Objective

Stop shipping or persisting secrets in normal config/output paths.

### Deliverables

- plain config with no secrets
- encrypted secret store in active use
- explicit `not configured yet` state when protected secrets are absent

### Tasks

1. Remove bot token and cloud auth key from normal config schema.
2. Make `secrets.dat` the only supported runtime secret source and remove plaintext fallback/import behavior.
3. Ensure agent writes secrets only to DPAPI-backed store.
4. Ensure API returns only masked secret status.
5. Fix build/publish behavior so secret-bearing config is not copied to artifacts.
6. Verify config path overrides affect only plain config loading and never secret discovery or cleanup.

### Acceptance Criteria

- `appsettings.json` contains only non-secret config
- `secrets.dat` is the only supported runtime source of secrets
- missing or empty `secrets.dat` yields deterministic `not configured yet` behavior
- runtime ignores secrets placed in `appsettings.json` or `secrets.json`
- startup does not import, rewrite, sanitize, or delete plaintext secret files
- README and task wording do not imply automatic migration or backward compatibility with MVP plaintext secret storage

### Dependencies

- Phase 2 secret store
- Phase 4 delivery modes

### Must Happen Before

- any installer/release work

## Phase 6: Local Loopback Control API

### Objective

Expose the agent’s state and control surface to the tray.

### Deliverables

- local loopback API
- auth mechanism for local callers
- stable DTO surface

### Tasks

1. Implement loopback-only HTTP binding.
2. Add local auth secret/token model.
3. Implement endpoints:
   - `GET /v1/status`
   - `GET /v1/connections`
   - `GET /v1/incidents`
   - `GET /v1/settings`
   - `PUT /v1/settings`
   - `POST /v1/settings/secrets/telegram`
   - `POST /v1/settings/secrets/cloud`
   - `POST /v1/actions/test-notification`
   - `POST /v1/actions/reload`
   - `POST /v1/actions/restart-agent`
4. Add DTO contract tests.
5. Add basic compatibility/versioning strategy for the API.

### Dependencies

- Phase 3
- Phase 4
- Phase 5

## Phase 7: Tray UI

### Objective

Provide a usable operator UI without moving logic into the tray.

### Deliverables

- `L2Monitor.Tray` project
- tray icon/menu
- status window
- connections view
- settings editor
- delivery/backend health visualization

### Tasks

1. Create `L2Monitor.Tray`.
2. Implement tray icon and context actions.
3. Implement status window with:
   - current mode
   - active L2 connection list
   - incident summary
   - delivery/backend health
4. Implement settings UI backed only by the local API.
5. Implement secret update forms without read-back of secret values.
6. Implement check/test actions.
7. Add reconnect handling if the agent restarts.

### Dependencies

- Phase 6 local API

### Can Run In Parallel

- visual shell
- settings forms
- status/connection views

## Phase 8: Startup and Installer

### Objective

Make the product installable and auto-started at user login.

### Deliverables

- installer flow
- user-logon autostart for agent
- user-logon autostart for tray
- upgrade-safe config/secret persistence

### Tasks

1. Choose installer stack.
2. Define installation directories.
3. Register agent for user-logon start.
4. Register tray for user-logon start.
5. Preserve config and secret store across upgrades.
6. Define uninstall behavior:
   - remove binaries
   - optionally preserve user data
7. Test install, upgrade, repair, and uninstall paths.

### Dependencies

- Phase 5 secret handling
- Phase 7 tray

## Phase 9: Hardening and Release Readiness

### Objective

Make the product operable, diagnosable, and supportable.

### Deliverables

- stable logging
- failure visibility
- configuration diagnostics
- release checklist

### Tasks

1. Add startup diagnostics for config and secret issues.
2. Add clear logging for delivery failures and backend failures.
3. Add degraded-state UI surfacing.
4. Add log rotation or bounded log persistence.
5. Add crash/restart recovery expectations for the agent.
6. Add regression checklist for:
   - detection correctness
   - Telegram direct mode
   - cloud offline mode
   - tray reconnect behavior
   - autostart

### Dependencies

- all previous phases

## Initial Agent Tasks

These are the best next tasks to hand to implementation agents.

### Task A: Solution Restructure

Create the new solution/project layout and wire empty project references:

- `L2Monitor.Core`
- `L2Monitor.Infrastructure.Windows`
- `L2Monitor.Agent`
- `L2Monitor.Tray`

Success criteria:

- solution builds
- no behavior change yet required

### Task B: Core Extraction

Move monitor models and `MonitorEngine` behavior into `L2Monitor.Core`.

Success criteria:

- unit tests cover current event behavior
- old MVP can still be adapted to run using extracted core logic

### Task C: Secret Leakage Remediation

Remove secret-bearing config from copied/published artifacts and define the new config/secret split.

Success criteria:

- no bot token or cloud auth key in normal build outputs
- secret/config contract documented with no plaintext fallback narrative

### Task D: Agent Runtime Skeleton

Create a hidden user-level agent shell with structured startup, cancellation, and dependency injection.

Success criteria:

- agent runs without visible console
- runtime loop lifecycle exists

## Dependency Summary

Critical path:

1. repo structure
2. core extraction
3. Windows infrastructure
4. agent runtime
5. delivery abstraction
6. secrets/config hardening
7. local API
8. tray
9. installer

## Explicit Deferrals

Do not spend time on these in the baseline:

- Windows Service support
- cross-machine orchestration
- fake backend simulator
- non-Windows support
- pre-login runtime behavior
