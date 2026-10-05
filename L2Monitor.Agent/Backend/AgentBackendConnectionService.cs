using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using L2Monitor.Agent.Hosting;
using L2Monitor.Agent.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace L2Monitor.Agent.Backend;

internal sealed class AgentBackendConnectionService(
    AgentConfigurationService configuration,
    AgentControlStateStore controlState,
    TimeProvider timeProvider,
    ILogger<AgentBackendConnectionService> logger) : BackgroundService
{
    private static readonly TimeSpan ConfigurationPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private const int MaxMessageBytes = 64 * 1024;

    private readonly object _socketSync = new();
    private readonly AgentConfigurationService _configuration = configuration;
    private readonly AgentControlStateStore _controlState = controlState;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<AgentBackendConnectionService> _logger = logger;
    private ClientWebSocket? _activeSocket;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = _configuration.GetSnapshot();
            if (!TryResolveCloudConnection(snapshot, out _, out var webSocketUri, out var authKey, out var error))
            {
                SetHealth("disabled", error);
                await DelayAsync(ConfigurationPollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RunSocketSessionAsync(webSocketUri, authKey, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "Соединение WebSocket с backend потеряно. Uri={WebSocketUri}", webSocketUri);
                SetHealth("unreachable", $"WebSocket недоступен: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка протокола WebSocket. Uri={WebSocketUri}", webSocketUri);
                SetHealth("protocol_error", $"Ошибка WebSocket: {ex.Message}");
            }

            await DelayAsync(ReconnectDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket? socket;
        lock (_socketSync)
        {
            socket = _activeSocket;
        }

        if (socket?.State == WebSocketState.Open)
        {
            try
            {
                await SendJsonAsync(socket, new { type = "shutdown" }, cancellationToken).ConfigureAwait(false);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "agent shutdown", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "Не удалось отправить завершающее сообщение WebSocket.");
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunSocketSessionAsync(Uri webSocketUri, string authKey, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", new AuthenticationHeaderValue("Bearer", authKey).ToString());

        lock (_socketSync)
        {
            _activeSocket = socket;
        }

        try
        {
            await socket.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);
            await SendJsonAsync(socket, new
            {
                type = "hello",
                machine_fingerprint = Environment.MachineName,
                machine_label = Environment.MachineName,
                client_version = ResolveClientVersion(),
            }, cancellationToken).ConfigureAwait(false);

            var helloAck = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
            EnsureMessageType(helloAck, "hello_ack");
            SetHealth("healthy", "WebSocket с backend подключён.", lastSuccessAtUtc: _timeProvider.GetUtcNow());

            using var heartbeatTimer = new PeriodicTimer(HeartbeatInterval, _timeProvider);
            var receiveTask = ReceiveJsonAsync(socket, cancellationToken);
            var heartbeatTask = heartbeatTimer.WaitForNextTickAsync(cancellationToken).AsTask();

            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var completedTask = await Task.WhenAny(receiveTask, heartbeatTask).ConfigureAwait(false);
                if (completedTask == receiveTask)
                {
                    EnsureMessageType(await receiveTask.ConfigureAwait(false), "heartbeat_ack");
                    SetHealth("healthy", "WebSocket с backend подключён.", lastSuccessAtUtc: _timeProvider.GetUtcNow());
                    receiveTask = ReceiveJsonAsync(socket, cancellationToken);
                }

                if (completedTask == heartbeatTask)
                {
                    if (!await heartbeatTask.ConfigureAwait(false))
                    {
                        break;
                    }

                    await SendJsonAsync(socket, new { type = "heartbeat" }, cancellationToken).ConfigureAwait(false);
                    heartbeatTask = heartbeatTimer.WaitForNextTickAsync(cancellationToken).AsTask();
                }
            }
        }
        finally
        {
            lock (_socketSync)
            {
                if (ReferenceEquals(_activeSocket, socket))
                {
                    _activeSocket = null;
                }
            }
        }
    }

    internal static bool TryResolveCloudConnection(
        AgentConfigurationSnapshot snapshot,
        out Uri backendBaseUri,
        out Uri webSocketUri,
        out string authKey,
        out string error)
    {
        backendBaseUri = null!;
        webSocketUri = null!;
        authKey = string.Empty;

        if (!string.Equals(snapshot.Settings.Delivery.Mode, "Cloud", StringComparison.OrdinalIgnoreCase))
        {
            error = "Облачный режим выключен.";
            return false;
        }

        if (!AgentBackendUriPolicy.TryResolve(snapshot.Settings.Cloud.BackendBaseUrl, out var resolvedBackendBaseUri))
        {
            error = "Для облачного режима нужен HTTPS-адрес backend; HTTP разрешён только для loopback-разработки.";
            return false;
        }

        backendBaseUri = resolvedBackendBaseUri;

        authKey = snapshot.Secrets.CloudAuthKey.Trim();
        if (authKey.Length == 0)
        {
            error = "Для WebSocket не задан ключ доступа.";
            return false;
        }

        var builder = new UriBuilder(backendBaseUri)
        {
            Scheme = backendBaseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/ws/agent",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        webSocketUri = builder.Uri;
        error = string.Empty;
        return true;
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException($"Backend закрыл WebSocket: {result.CloseStatus} {result.CloseStatusDescription}");
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new WebSocketException("Backend отправил сообщение WebSocket неподдерживаемого типа.");
            }

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > MaxMessageBytes)
            {
                throw new WebSocketException("Сообщение WebSocket превышает допустимый размер.");
            }
            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(stream.ToArray());
            }
        }
    }

    private static void EnsureMessageType(JsonDocument message, string expectedType)
    {
        using (message)
        {
            if (!message.RootElement.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), expectedType, StringComparison.Ordinal))
            {
                throw new WebSocketException($"Ожидалось сообщение '{expectedType}'.");
            }
        }
    }

    private void SetHealth(
        string state,
        string summary,
        DateTimeOffset? checkedAtUtc = null,
        DateTimeOffset? lastSuccessAtUtc = null) =>
        _controlState.SetLastBackendHealth(new AgentComponentHealthRecord(
            state,
            summary,
            checkedAtUtc ?? _timeProvider.GetUtcNow(),
            lastSuccessAtUtc));

    private static string ResolveClientVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
