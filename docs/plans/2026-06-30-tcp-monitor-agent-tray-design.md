# L2Monitor Agent + Tray Design

> Status: accepted direction as of 2026-06-30
>
> This design replaces the earlier service-first direction. The baseline runtime is now a user-level background agent plus a tray UI.

## Goal

Turn the current `tcp-monitor-mvp` into a Windows product that:

- starts automatically when the user logs in
- runs without a visible console
- monitors active Lineage II clients reliably in the interactive user session
- shows a tray UI with current connections, status, and settings
- supports both local Telegram-direct delivery and cloud delivery
- uses a real cloud contract even before the backend exists
- stores secrets outside normal config files

## Why This Design

The current monitor does not rely only on TCP and PIDs. It also uses visible window title discovery via `user32`:

- `tcp-monitor-mvp/Platform/Win32.cs`
- `tcp-monitor-mvp/Services/ProcessFilter.cs`
- `tcp-monitor-mvp/MonitorApplication.cs`

That makes a pure Windows Service a poor fit for the current detection model, because the truth source lives in the interactive user session. A user-level agent matches the real runtime environment better and avoids session-0 complexity.

## Non-Goals

- no Windows Service in the baseline release
- no fake/mock backend process for cloud mode
- no support for pre-login monitoring
- no multi-user shared-machine orchestration in this phase
- no web dashboard in this phase

## Product Shape

The target architecture is:

1. `L2Monitor.Core`
2. `L2Monitor.Infrastructure.Windows`
3. `L2Monitor.Agent`
4. `L2Monitor.Tray`
5. cloud backend client contract

## Component Responsibilities

### `L2Monitor.Core`

Pure application/domain layer.

Contains:

- monitor state machine
- models for connections, clients, incidents, health, and settings
- delivery abstractions
- agent/tray API DTOs
- validation logic

Does not contain:

- Win32 interop
- DPAPI calls
- tray UI
- HTTP binding details
- Telegram-specific networking

### `L2Monitor.Infrastructure.Windows`

Windows-only adapters and persistence.

Contains:

- process enumeration
- TCP snapshot and PID ownership lookup
- window title access
- Win32 interop
- config file persistence
- DPAPI secret store
- autostart integration for the user session
- local loopback API hosting helpers
- Telegram direct transport implementation
- cloud relay transport implementation

### `L2Monitor.Agent`

Hidden user-level runtime and source of truth.

Owns:

- process/window detection
- TCP correlation
- current runtime state
- monitor polling cadence
- event detection
- health calculation
- local/cloud notification dispatch
- config load/save
- secret load/save
- local control API

Runs:

- at user logon
- without console window
- continuously in the background while the user is signed in

### `L2Monitor.Tray`

User-facing control plane only.

Owns:

- tray icon
- status window
- current L2 connections list
- settings editor
- backend/local delivery health view
- test notification actions
- restart/reload actions against the agent

Does not own:

- monitor logic
- notification sending
- secrets as the authoritative store
- durable runtime state

## Runtime Model

The agent is the only authoritative runtime process.

High-level flow:

1. Agent starts at user login.
2. Agent loads non-secret config and protected secrets.
3. Agent polls processes, windows, and TCP tables.
4. Agent updates monitored client state.
5. Agent emits incidents and delivery attempts.
6. Tray reads state and pushes settings changes through the local API.

## Detection Flow

Per poll tick:

1. Enumerate candidate processes.
2. Resolve visible window titles for those processes.
3. Build TCP summary by PID.
4. Correlate process identity + title + TCP ownership.
5. Apply monitor state machine.
6. Update current active client list.
7. Emit incidents such as timeout, recovery, and exit.

The key architectural reason for the user-level agent is that steps 1-4 depend on interactive-session visibility.

## Delivery Modes

### `Local`

Agent sends directly to Telegram Bot API.

Requirements:

- bot token
- target binding / chat configuration

Health states:

- `healthy`
- `misconfigured`
- `auth_failed`
- `unreachable`
- `rate_limited`

### `Cloud`

Agent sends to a real backend contract.

Requirements:

- `BackendBaseUrl`
- `AuthKey`

Behavior before backend exists:

- checks are real
- failed connectivity is shown honestly in UI
- status should be red, for example `offline`, `auth failed`, or `unreachable`
- no local fake success behavior

Health states:

- `healthy`
- `misconfigured`
- `auth_failed`
- `unreachable`
- `protocol_error`

## Notification Architecture

Use a transport abstraction in core:

- `INotificationTransport`
- `TelegramDirectTransport`
- `CloudRelayTransport`
- `NotificationRouter`

The router selects exactly one active mode from config.

The tray must never send notifications by itself.

## Local Agent API

Tray talks only to the agent over loopback HTTP.

Bootstrap rule:

- tray must not resolve or read the agent data root directly
- agent exports a dedicated bootstrap snapshot for endpoint/auth discovery
- bootstrap export is a runtime contract, not a general storage API for the tray

Transport rules:

- bind to `127.0.0.1` only
- require local authentication even on loopback
- expose a stable versioned API

Suggested endpoints:

- `GET /v1/status`
- `GET /v1/connections`
- `GET /v1/incidents?limit=50`
- `GET /v1/settings`
- `PUT /v1/settings`
- `POST /v1/settings/secrets/telegram`
- `POST /v1/settings/secrets/cloud`
- `POST /v1/actions/test-notification`
- `POST /v1/actions/reload`
- `POST /v1/actions/restart-agent`

`/v1/status` should include at least:

- agent running state
- current mode
- active L2 client count
- current client summaries
- last scan time
- delivery health
- backend health
- last incident time
- last notification result

## Cloud Backend Contract

Cloud mode deliberately has a minimal network surface.

Minimum contract:

- one `GET /public/client-version` per agent process
- one active, reconnectable `WebSocket /ws/agent`

The bearer key is used only for the WebSocket handshake. The agent does not call
unrelated server-side product APIs or expose their state to the tray.

## Deferred Future Backend Direction

This section records the current backend recommendation so it is not lost. It is intentionally not the final protocol design and does not change the accepted baseline architecture in this document.

Current guidance for later backend work:

- Keep the accepted runtime baseline as user-level agent plus tray.
- Keep REST for bootstrap, admin, configuration, and explicit actions.
- If fast agent-unreachable detection matters, center backend liveness on one persistent WebSocket per running agent rather than REST-only ping/check loops.
- Model cloud presence as `license -> machines -> sessions`, not `one auth key -> one connection`.
- Distinguish backend-visible `agent unreachable` from actual Lineage II/game disconnect incidents.
- Finalize the wire protocol later, in parallel with real backend implementation work on both the agent and backend sides.

Open questions to defer until backend implementation starts:

- exact WebSocket handshake and re-auth flow
- session lifecycle and reconnect semantics
- what remains on REST versus what moves to streaming/push channels
- how backend health, agent liveness, and gameplay incidents are represented separately

## Config and Secret Storage

### Normal Config

Keep regular config in JSON.

Contains:

- monitor timings and thresholds
- port lists
- title patterns
- delivery mode
- backend base URL
- tray/UI preferences
- non-secret target metadata

Does not contain:

- Telegram bot token
- cloud auth key
- local API auth secret

### Secret Storage

Secrets are owned by the agent and stored separately from normal config.

Contains:

- Telegram bot token
- cloud auth key
- local API secret if persisted

Protection model:

- Windows DPAPI
- user-scope protection is acceptable in the new baseline because the runtime is user-level
- the tray should still never display raw secrets back after save
- API responses return only masked values or presence flags

Recommended shape:

- one JSON config file
- one encrypted secret-store file

## Startup and Autostart

Baseline startup model:

- installer registers the agent to launch at user logon
- tray also launches at user logon, or the tray is launched by the agent depending on implementation choice

Design preference:

- agent and tray are separate processes
- both can autostart
- tray reconnects if the agent restarts

This keeps UI crashes from killing monitoring.

## Health Model

Expose explicit health rather than hiding failures.

Top-level status categories:

- `agent_running`
- `monitor_healthy`
- `monitor_degraded`
- `delivery_healthy`
- `delivery_degraded`
- `backend_healthy`
- `backend_unreachable`
- `misconfigured`

UI should surface these clearly with color/state indicators.

## Risks

### 1. Secrets already leaked in current build outputs

Current MVP copies `appsettings.json` into build artifacts. That must be fixed before shipping any installer-based workflow.

### 2. Agent-only means no pre-login monitoring

Accepted tradeoff for the baseline architecture.

### 3. Tray/agent protocol drift

Version the local API and keep DTO ownership centralized in core.

### 4. Loopback API trust boundary

`localhost` is not automatically trusted. Authentication and loopback-only binding are required.

### 5. Duplicate ownership

Only the agent may own detection, runtime state, and notification emission.

## Migration from Current MVP

Current MVP responsibilities in one process:

- config load
- process/window scan
- TCP analysis
- state machine
- console rendering
- Telegram sending

Migration target:

- move state machine, models, and contracts into `Core`
- move Windows-specific discovery into `Infrastructure.Windows`
- turn main loop into `Agent`
- replace console renderer with local API + tray UI
- replace cleartext secret handling with DPAPI-backed secret store

## Execution Direction

Phase order should now be:

1. extract core from `tcp-monitor-mvp`
2. create user-level `L2Monitor.Agent`
3. add delivery abstraction and cloud/local modes
4. add DPAPI secret store and remove secret usage from plain config
5. add loopback API
6. build tray UI
7. add installer and logon autostart

## Deferred Option

If a real requirement appears later for operation outside the logged-in session, service support can be added as a future architecture track. It is no longer part of the baseline design.
