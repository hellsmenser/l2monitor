using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using L2Monitor.Core.Api;
using L2Monitor.Tray.Api;
using L2Monitor.Tray.Bootstrap;
using L2Monitor.Tray.Presentation;
using MediaColor = System.Windows.Media.Color;

namespace L2Monitor.Tray.Ui;

internal sealed partial class MainWindow : Window
{
    private readonly LocalControlApiClient _apiClient;
    private readonly IAutostartService _autostartService;
    private readonly ObservableCollection<ConnectionRow> _connections = [];
    private readonly ObservableCollection<IncidentRow> _incidents = [];
    private TrayDashboardSnapshot? _lastDashboard;
    private string? _pendingTelegramLinkCode;
    private bool _allowExit;
    private bool _refreshInFlight;
    private bool _settingsDirty;
    private bool _deliveryModeSelectionDirty;
    private bool _suppressDirtyTracking;
    private bool _actionsEnabled = true;

    public MainWindow(LocalControlApiClient apiClient, IAutostartService? autostartService = null)
    {
        _apiClient = apiClient;
        _autostartService = autostartService ?? new DisabledAutostartService();
        InitializeComponent();

        ConnectionsList.ItemsSource = _connections;
        IncidentsList.ItemsSource = _incidents;

        EndpointText.Visibility = Visibility.Collapsed;
        RefreshButton.Click += async (_, _) => await RefreshNowAsync(forceSettings: false).ConfigureAwait(true);
        TestNotificationButton.Click += async (_, _) => await TriggerTestNotificationAsync().ConfigureAwait(true);
        ReloadButton.Click += async (_, _) => await TriggerReloadAsync().ConfigureAwait(true);
        RestartButton.Click += async (_, _) => await TriggerRestartAsync().ConfigureAwait(true);
        SaveSettingsButton.Click += async (_, _) => await SaveSettingsAsync().ConfigureAwait(true);
        BindTelegramChatButton.Click += async (_, _) => await StartTelegramChatLinkAsync().ConfigureAwait(true);
        ConfirmTelegramLinkButton.Click += async (_, _) => await ConfirmTelegramChatLinkAsync().ConfigureAwait(true);
        CopyTelegramLinkCodeButton.Click += (_, _) => CopyTelegramLinkCode();
        SaveTelegramSecretButton.Click += async (_, _) => await SaveTelegramSecretAsync().ConfigureAwait(true);
        SaveCloudSecretButton.Click += async (_, _) => await SaveCloudSecretAsync().ConfigureAwait(true);

        DeliveryEnabledInput.Checked += OnSettingsEdited;
        DeliveryEnabledInput.Unchecked += OnSettingsEdited;
        TelegramDeliveryModeRadio.Checked += OnDeliveryModeChecked;
        CloudDeliveryModeRadio.Checked += OnDeliveryModeChecked;
        IgnoredWindowTitlesInput.TextChanged += OnSettingsEdited;
        DisconnectNotificationEnabledInput.Checked += OnSettingsEdited;
        DisconnectNotificationEnabledInput.Unchecked += OnSettingsEdited;
        GhostDisconnectMessageTemplateInput.TextChanged += OnSettingsEdited;
        ProcessExitedNotificationEnabledInput.Checked += OnSettingsEdited;
        ProcessExitedNotificationEnabledInput.Unchecked += OnSettingsEdited;
        ProcessExitedMessageTemplateInput.TextChanged += OnSettingsEdited;
        DeadStartedNotificationEnabledInput.Checked += OnSettingsEdited;
        DeadStartedNotificationEnabledInput.Unchecked += OnSettingsEdited;
        DeadStartedMessageTemplateInput.TextChanged += OnSettingsEdited;

        Closing += OnWindowClosing;

        ApplyAutostartState();
        AutostartInput.Checked += OnAutostartChanged;
        AutostartInput.Unchecked += OnAutostartChanged;
        UpdateSettingsDirtyState();
        ApplyOfflineBadges();
        UpdateDeliveryModePanels();
        UpdateNotificationTemplateInputs();
    }

    public event EventHandler<TrayPresentationSummary>? TraySummaryChanged;

    public void ShowWindow()
    {
        SettingsTab.IsSelected = true;

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void RequestExit() => _allowExit = true;

    public Task<ActionResponseDto> StopAgentAsync() =>
        _apiClient.TriggerShutdownAsync(CancellationToken.None);

    public async Task<bool> RefreshNowAsync(bool forceSettings)
    {
        if (_refreshInFlight)
        {
            return false;
        }

        _refreshInFlight = true;
        SetBusyState("Обновляю данные...");

        try
        {
            var dashboard = await _apiClient.GetDashboardAsync(CancellationToken.None).ConfigureAwait(true);
            ApplyDashboard(dashboard, forceSettings);
            SetReadyState($"Последнее обновление: {DateTime.Now:T}");
            return true;
        }
        catch (AgentConnectionException ex)
        {
            ApplyOfflineState(TrayLabelCanon.DescribeFailure(ex));
            return false;
        }
        catch (LocalControlApiException ex)
        {
            ApplyOfflineState(TrayLabelCanon.DescribeFailure(ex));
            return false;
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    public async Task TriggerTestNotificationAsync()
    {
        await RunActionAsync(
            "Запускаю тест уведомления...",
            () => _apiClient.TriggerTestNotificationAsync(CancellationToken.None),
            forceSettingsRefresh: false).ConfigureAwait(true);
    }

    public async Task TriggerReloadAsync()
    {
        await RunActionAsync(
            "Перечитываю конфигурацию агента...",
            () => _apiClient.TriggerReloadAsync(CancellationToken.None),
            forceSettingsRefresh: true).ConfigureAwait(true);
    }

    public async Task TriggerRestartAsync()
    {
        await RunActionAsync(
            "Запрашиваю перезапуск агента...",
            () => _apiClient.TriggerRestartAsync(CancellationToken.None),
            forceSettingsRefresh: true).ConfigureAwait(true);
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            SetBusyState("Сохраняю настройки...");
            if (!TryCreateTraySettingsRequest(
                    ResolveDeliveryModeForSave(),
                    DeliveryEnabledInput.IsChecked == true && IsLocalModeSelected(),
                    IgnoredWindowTitlesInput.Text,
                    DisconnectNotificationEnabledInput.IsChecked == true,
                    GhostDisconnectMessageTemplateInput.Text,
                    ProcessExitedNotificationEnabledInput.IsChecked == true,
                    ProcessExitedMessageTemplateInput.Text,
                    DeadStartedNotificationEnabledInput.IsChecked == true,
                    DeadStartedMessageTemplateInput.Text,
                    out var request,
                    out var validationMessage))
            {
                System.Windows.MessageBox.Show(this, validationMessage, "Проверьте настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetReadyState("Настройки не сохранены: проверьте введённые значения.");
                return;
            }

            await _apiClient.UpdateTraySettingsAsync(request!, CancellationToken.None).ConfigureAwait(true);
            _settingsDirty = false;
            _deliveryModeSelectionDirty = false;
            UpdateSettingsDirtyState();
            await RefreshNowAsync(forceSettings: true).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is LocalControlApiException or AgentConnectionException)
        {
            var message = TrayLabelCanon.DescribeFailure(ex);
            System.Windows.MessageBox.Show(this, message, "Не удалось сохранить настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetReadyState(message);
        }
    }

    private async Task SaveTelegramSecretAsync()
    {
        try
        {
            SetBusyState("Обновляю токен Telegram...");
            await _apiClient.UpdateTelegramSecretAsync(
                new UpdateTelegramSecretRequestDto(TelegramSecretInput.Password),
                CancellationToken.None).ConfigureAwait(true);
            TelegramSecretInput.Clear();
            await RefreshNowAsync(forceSettings: false).ConfigureAwait(true);
            SetReadyState("Токен Telegram обновлён.");
        }
        catch (Exception ex) when (ex is LocalControlApiException or AgentConnectionException)
        {
            var message = TrayLabelCanon.DescribeFailure(ex);
            System.Windows.MessageBox.Show(this, message, "Не удалось обновить токен Telegram", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetReadyState(message);
        }
    }

    private async Task SaveCloudSecretAsync()
    {
        try
        {
            SetBusyState("Обновляю ключ облака...");
            await _apiClient.UpdateCloudSecretAsync(
                new UpdateCloudSecretRequestDto(CloudSecretInput.Password),
                CancellationToken.None).ConfigureAwait(true);
            CloudSecretInput.Clear();
            await RefreshNowAsync(forceSettings: false).ConfigureAwait(true);
            SetReadyState("Ключ облака обновлён.");
        }
        catch (Exception ex) when (ex is LocalControlApiException or AgentConnectionException)
        {
            var message = TrayLabelCanon.DescribeFailure(ex);
            System.Windows.MessageBox.Show(this, message, "Не удалось обновить ключ облака", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetReadyState(message);
        }
    }

    private async Task RunActionAsync(
        string busyMessage,
        Func<Task<ActionResponseDto>> action,
        bool forceSettingsRefresh)
    {
        try
        {
            SetBusyState(busyMessage);
            var result = await action().ConfigureAwait(true);
            var presentation = TrayLabelCanon.DescribeActionResult(result.Result);
            await RefreshNowAsync(forceSettingsRefresh).ConfigureAwait(true);
            SetReadyState(presentation);
        }
        catch (Exception ex) when (ex is LocalControlApiException or AgentConnectionException)
        {
            var message = TrayLabelCanon.DescribeFailure(ex);
            System.Windows.MessageBox.Show(this, message, "Действие завершилось ошибкой", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetReadyState(message);
        }
    }

    private void ApplyDashboard(TrayDashboardSnapshot dashboard, bool forceSettings)
    {
        _lastDashboard = dashboard;
        var summary = TrayStatusPresenter.FromDashboard(dashboard);
        HeadlineText.Text = summary.Headline;
        DetailText.Text = summary.Detail;

        UpdateModeBadge(dashboard.Status.CurrentMode);
        UpdateAgentStatusBadge(AgentBadgeBorder, AgentBadgeText, dashboard.Status.AgentStatus);
        UpdateHealthBadge(DeliveryBadgeBorder, DeliveryBadgeText, dashboard.Status.Delivery.State);

        ModeValueText.Text = TranslateMode(dashboard.Status.CurrentMode);
        AgentStatusValueText.Text = TrayLabelCanon.DescribeAgentStatus(dashboard.Status.AgentStatus);
        ConnectionCountValueText.Text = $"Клиентов: {dashboard.Status.ActiveConnectionCount}; проб: {dashboard.Status.ProbeCount}";
        DeliveryHealthValueText.Text = FormatHealthPanelText(dashboard.Status.Delivery, lastKnown: false);
        LastIncidentValueText.Text = FormatLastIncidentValue(dashboard.Status.LastIncident, lastKnown: false);

        PopulateConnections(dashboard.Status.Connections);
        PopulateIncidents(dashboard.Incidents.Items);
        ApplySettingsIfAllowed(dashboard.Settings, forceSettings);

        TraySummaryChanged?.Invoke(this, summary);
    }

    private void ApplyOfflineState(string message)
    {
        var summary = TrayStatusPresenter.Offline(message, _lastDashboard);
        HeadlineText.Text = summary.Headline;
        DetailText.Text = summary.Detail;
        EndpointText.Text = "Адрес: недоступен";
        ApplyOfflineBadges();

        if (_lastDashboard is null)
        {
            ModeValueText.Text = "Неизвестно";
            AgentStatusValueText.Text = "Недоступен";
            ConnectionCountValueText.Text = "Нет данных о прошлой активности.";
            DeliveryHealthValueText.Text = "Нет данных о прошлой доставке.";
            LastIncidentValueText.Text = "Нет данных о прошлых инцидентах.";
            _connections.Clear();
            _incidents.Clear();
        }
        else
        {
            ModeValueText.Text = $"{TranslateMode(_lastDashboard.Status.CurrentMode)} (последнее известное)";
            AgentStatusValueText.Text = "Сейчас недоступен";
            ConnectionCountValueText.Text = $"Клиентов: {_lastDashboard.Status.ActiveConnectionCount}; проб: {_lastDashboard.Status.ProbeCount} (последнее известное)";
            DeliveryHealthValueText.Text = FormatHealthPanelText(_lastDashboard.Status.Delivery, lastKnown: true);
            LastIncidentValueText.Text = FormatLastIncidentValue(_lastDashboard.Status.LastIncident, lastKnown: true);
        }

        TraySummaryChanged?.Invoke(this, summary);
        SetReadyState("Агент недоступен.");
    }

    private void ApplySettingsIfAllowed(SettingsResponseDto settings, bool forceSettings)
    {
        ApplySettingsMetadata(settings);

        if (_settingsDirty && !forceSettings)
        {
            return;
        }

        _suppressDirtyTracking = true;
        try
        {
            var deliveryEnabled = !string.Equals(settings.Settings.DeliveryMode, "Disabled", StringComparison.OrdinalIgnoreCase);
            DeliveryEnabledInput.IsChecked = deliveryEnabled;
            CloudDeliveryModeRadio.IsChecked = string.Equals(settings.Settings.DeliveryMode, "Cloud", StringComparison.OrdinalIgnoreCase);
            TelegramDeliveryModeRadio.IsChecked = !CloudDeliveryModeRadio.IsChecked;

            IgnoredWindowTitlesInput.Text = settings.Settings.IgnoredWindowTitlesText ?? string.Empty;
            DisconnectNotificationEnabledInput.IsChecked = settings.Settings.DisconnectNotificationEnabled;
            GhostDisconnectMessageTemplateInput.Text = ResolveDisconnectTemplateText(settings.Settings);
            ProcessExitedNotificationEnabledInput.IsChecked = settings.Settings.ProcessExitedNotificationEnabled;
            ProcessExitedMessageTemplateInput.Text = settings.Settings.ProcessExitedMessageTemplate ?? string.Empty;
            DeadStartedNotificationEnabledInput.IsChecked = settings.Settings.DeadStartedNotificationEnabled;
            DeadStartedMessageTemplateInput.Text = settings.Settings.DeadStartedMessageTemplate ?? string.Empty;

            _settingsDirty = false;
            _deliveryModeSelectionDirty = false;
            UpdateSettingsDirtyState();
        }
        finally
        {
            _suppressDirtyTracking = false;
            UpdateDeliveryModePanels();
            UpdateNotificationTemplateInputs();
        }
    }

    private void PopulateConnections(IReadOnlyList<AgentConnectionDto> connections)
    {
        _connections.Clear();
        foreach (var connection in connections)
        {
            _connections.Add(new ConnectionRow(
                connection.WindowTitle ?? string.Empty,
                DescribeConnectionDisplayState(connection.State),
                FormatTrayTimestamp(connection.ObservedAtUtc)));
        }
    }

    private void PopulateIncidents(IReadOnlyList<AgentIncidentDto> incidents)
    {
        _incidents.Clear();
        foreach (var incident in incidents)
        {
            _incidents.Add(new IncidentRow(
                DescribeIncidentDisplaySeverity(incident.Severity),
                DescribeIncidentDisplayKind(incident.Kind),
                TrayLabelCanon.DescribeIncidentSummary(incident),
                FormatTrayTimestamp(incident.OccurredAtUtc)));
        }
    }

    internal static string FormatTrayTimestamp(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    internal static string FormatLastIncidentValue(AgentIncidentDto? incident, bool lastKnown)
    {
        if (incident is null)
        {
            return lastKnown
                ? "В последнем известном состоянии инцидентов не было."
                : "Новых инцидентов нет.";
        }

        var suffix = lastKnown ? " (последнее известное)" : string.Empty;
        var kind = DescribeIncidentDisplayKind(incident.Kind);
        var summary = TrayLabelCanon.DescribeIncidentSummary(incident);
        var value = string.Equals(kind, summary, StringComparison.Ordinal)
            ? kind
            : $"{kind}: {summary}";
        return $"{value}{suffix}";
    }

    internal static string FormatHealthPanelText(ComponentHealthDto? deliveryHealth, bool lastKnown)
    {
        var suffix = lastKnown ? " (последнее известное)" : string.Empty;
        return $"{TrayLabelCanon.DescribeHealthSummary(deliveryHealth)}{suffix}";
    }

    internal static string DescribeConnectionDisplayState(string state) => TrayLabelCanon.DescribeConnectionState(state);

    internal static string DescribeIncidentDisplayKind(string kind) => TrayLabelCanon.DescribeIncidentKind(kind);

    internal static string DescribeIncidentDisplaySeverity(string severity) => TrayLabelCanon.DescribeSeverity(severity);

    private void OnSettingsEdited(object? sender, RoutedEventArgs e) => MarkSettingsDirty();
    private void OnSettingsEdited(object? sender, TextChangedEventArgs e) => MarkSettingsDirty();

    private void MarkSettingsDirty()
    {
        if (_suppressDirtyTracking)
        {
            return;
        }

        _settingsDirty = true;
        UpdateSettingsDirtyState();
        UpdateDeliveryModePanels();
        UpdateNotificationTemplateInputs();
    }

    private void ApplyAutostartState()
    {
        try
        {
            AutostartInput.IsChecked = _autostartService.IsEnabled();
        }
        catch
        {
            AutostartInput.IsChecked = false;
            AutostartInput.IsEnabled = false;
            AutostartInput.ToolTip = "Не удалось прочитать настройки автозапуска Windows.";
        }
    }

    private void OnAutostartChanged(object? sender, RoutedEventArgs e)
    {
        if (!AutostartInput.IsEnabled)
        {
            return;
        }

        try
        {
            _autostartService.SetEnabled(AutostartInput.IsChecked == true);
        }
        catch (Exception ex)
        {
            AutostartInput.Checked -= OnAutostartChanged;
            AutostartInput.Unchecked -= OnAutostartChanged;
            ApplyAutostartState();
            if (AutostartInput.IsEnabled)
            {
                AutostartInput.Checked += OnAutostartChanged;
                AutostartInput.Unchecked += OnAutostartChanged;
            }
            System.Windows.MessageBox.Show(
                this,
                $"Не удалось изменить автозапуск Aden+: {ex.Message}",
                "Автозапуск Aden+",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void UpdateSettingsDirtyState()
    {
        if (_settingsDirty)
        {
            SettingsDirtyText.Text = "Есть несохранённые изменения. «Сохранить настройки» применяет выбранный режим, исключения и тексты уведомлений.";
            SettingsDirtyText.Foreground = new SolidColorBrush(MediaColor.FromRgb(146, 98, 16));
        }
        else
        {
            SettingsDirtyText.Text = "Несохранённых изменений нет.";
            SettingsDirtyText.Foreground = new SolidColorBrush(MediaColor.FromRgb(98, 112, 134));
        }
    }

    private void OnDeliveryModeChecked(object? sender, RoutedEventArgs e)
    {
        if (!_suppressDirtyTracking)
        {
            _deliveryModeSelectionDirty = true;
        }

        UpdateDeliveryModePanels();
        UpdateNotificationTemplateInputs();
        MarkSettingsDirty();
    }

    private void UpdateDeliveryModePanels()
    {
        var enabled = DeliveryEnabledInput.IsChecked == true;
        var isLocal = enabled && IsLocalModeSelected();
        var isCloud = enabled && !IsLocalModeSelected();
        var hasPendingCode = HasPendingTelegramLinkCode(_pendingTelegramLinkCode);

        DeliveryConfigPanel.Opacity = enabled ? 1.0 : 0.55;
        TelegramDeliveryModeRadio.IsEnabled = _actionsEnabled && enabled;
        CloudDeliveryModeRadio.IsEnabled = _actionsEnabled && enabled;
        BindTelegramChatButton.IsEnabled = _actionsEnabled && enabled && isLocal;
        ConfirmTelegramLinkButton.IsEnabled = _actionsEnabled && enabled && isLocal && hasPendingCode;
        CopyTelegramLinkCodeButton.IsEnabled = _actionsEnabled && enabled && isLocal && hasPendingCode;
        SaveTelegramSecretButton.IsEnabled = _actionsEnabled && enabled && isLocal;
        SaveCloudSecretButton.IsEnabled = _actionsEnabled && enabled && isCloud;
        TestNotificationButton.IsEnabled = _actionsEnabled && isLocal && !_deliveryModeSelectionDirty;
        TestNotificationButton.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        TelegramLinkCodeText.IsHitTestVisible = hasPendingCode;
        TelegramLinkCodeText.Focusable = hasPendingCode;
        TelegramLinkCodeText.IsTabStop = hasPendingCode;
        LocalDeliveryPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        CloudDeliveryPanel.Visibility = isCloud ? Visibility.Visible : Visibility.Collapsed;
        DeliveryDisabledHintText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        DeliveryDisabledHintText.Text = "Когда доставка выключена, агент продолжает мониторинг, но не отправляет уведомления.";
    }

    private void UpdateNotificationTemplateInputs()
    {
        GhostDisconnectMessageTemplateInput.IsEnabled = DisconnectNotificationEnabledInput.IsChecked == true;
        ProcessExitedMessageTemplateInput.IsEnabled = ProcessExitedNotificationEnabledInput.IsChecked == true;
        DeadStartedMessageTemplateInput.IsEnabled = DeadStartedNotificationEnabledInput.IsChecked == true;
    }

    private void SetBusyState(string message)
    {
        StatusText.Text = message;
        SetActionsEnabled(false);
    }

    private void SetReadyState(string message)
    {
        StatusText.Text = message;
        SetActionsEnabled(true);
    }

    private void SetActionsEnabled(bool enabled)
    {
        _actionsEnabled = enabled;
        SaveSettingsButton.IsEnabled = enabled;
        TelegramDeliveryModeRadio.IsEnabled = enabled && DeliveryEnabledInput.IsChecked == true;
        CloudDeliveryModeRadio.IsEnabled = enabled && DeliveryEnabledInput.IsChecked == true;
        BindTelegramChatButton.IsEnabled = enabled && DeliveryEnabledInput.IsChecked == true && IsLocalModeSelected();
        ConfirmTelegramLinkButton.IsEnabled = enabled && DeliveryEnabledInput.IsChecked == true && IsLocalModeSelected() && !string.IsNullOrWhiteSpace(_pendingTelegramLinkCode);
        CopyTelegramLinkCodeButton.IsEnabled = enabled && DeliveryEnabledInput.IsChecked == true && IsLocalModeSelected() && !string.IsNullOrWhiteSpace(_pendingTelegramLinkCode);
        SaveTelegramSecretButton.IsEnabled = enabled && DeliveryEnabledInput.IsChecked == true && IsLocalModeSelected();
        SaveCloudSecretButton.IsEnabled = enabled && DeliveryEnabledInput.IsChecked == true && !IsLocalModeSelected();
        RefreshButton.IsEnabled = enabled;
        TestNotificationButton.IsEnabled = enabled
            && DeliveryEnabledInput.IsChecked == true
            && IsLocalModeSelected()
            && !_deliveryModeSelectionDirty;
        ReloadButton.IsEnabled = enabled;
        RestartButton.IsEnabled = enabled;
    }

    private void ApplyOfflineBadges()
    {
        UpdateNeutralBadge(ModeBadgeBorder, ModeBadgeText, "Режим неизвестен");
        UpdateErrorBadge(AgentBadgeBorder, AgentBadgeText, "Агент недоступен");
        UpdateNeutralBadge(DeliveryBadgeBorder, DeliveryBadgeText, "Доставка неизвестна");
    }

    private static void ApplySecretPresence(Border border, TextBlock label, bool hasSecret)
    {
        label.Text = hasSecret ? "Секрет сохранён" : "Секрет отсутствует";
        if (hasSecret)
        {
            border.Background = new SolidColorBrush(MediaColor.FromRgb(29, 73, 53));
            label.Foreground = new SolidColorBrush(MediaColor.FromRgb(167, 227, 191));
        }
        else
        {
            border.Background = new SolidColorBrush(MediaColor.FromRgb(81, 64, 28));
            label.Foreground = new SolidColorBrush(MediaColor.FromRgb(241, 206, 121));
        }
    }

    private void ApplyChatBindingStatus(long? chatId)
    {
        var isBound = chatId is not null;
        TelegramChatBindingBorder.Background = new SolidColorBrush(
            isBound ? MediaColor.FromRgb(29, 73, 53) : MediaColor.FromRgb(81, 64, 28));
        TelegramChatBindingText.Foreground = new SolidColorBrush(
            isBound ? MediaColor.FromRgb(167, 227, 191) : MediaColor.FromRgb(241, 206, 121));
        TelegramChatBindingText.Text = isBound ? "Чат для уведомлений привязан" : "Чат для уведомлений не привязан";
        BindTelegramChatButton.Content = isBound ? "Получить новый код" : "Получить код";

        if (isBound)
        {
            ClearPendingTelegramLinkUi("Привязка завершена. Код больше не нужен.");
        }
    }

    private void ApplyPendingTelegramLink(TelegramChatLinkSessionDto? pendingLink)
    {
        if (pendingLink is null || string.IsNullOrWhiteSpace(pendingLink.Code))
        {
            ClearPendingTelegramLinkUi("Сначала нажмите «Получить код».");
            return;
        }

        _pendingTelegramLinkCode = pendingLink.Code;
        TelegramLinkCodeText.Text = pendingLink.Code;
        TelegramLinkHintText.Text = "Отправьте код боту в личном чате, затем подтвердите привязку.";
        TelegramLinkHintText.Foreground = new SolidColorBrush(MediaColor.FromRgb(98, 112, 134));
        UpdateDeliveryModePanels();
    }

    internal static string ResolveDisconnectTemplateText(LocalControlSettingsDto settings) =>
        settings.GhostDisconnectMessageTemplate
        ?? settings.ClientDisconnectedMessageTemplate
        ?? string.Empty;

    internal static bool HasPendingTelegramLinkCode(string? code) =>
        !string.IsNullOrWhiteSpace(code);

    private void ApplySettingsMetadata(SettingsResponseDto settings)
    {
        ApplySecretPresence(TelegramSecretStatusBorder, TelegramSecretStatusText, settings.Secrets.HasTelegramBotToken);
        ApplySecretPresence(CloudSecretStatusBorder, CloudSecretStatusText, settings.Secrets.HasCloudAuthKey);
        ApplyChatBindingStatus(settings.Settings.TelegramChatId);
        ApplyPendingTelegramLink(settings.PendingTelegramChatLink);
    }
    private void UpdateModeBadge(string mode)
    {
        if (string.Equals(mode, "Cloud", StringComparison.OrdinalIgnoreCase))
        {
            UpdateBadge(ModeBadgeBorder, ModeBadgeText, "Облако", MediaColor.FromRgb(41, 60, 102), MediaColor.FromRgb(183, 204, 255));
            return;
        }

        if (string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase))
        {
            UpdateBadge(ModeBadgeBorder, ModeBadgeText, "Локально", MediaColor.FromRgb(29, 73, 53), MediaColor.FromRgb(167, 227, 191));
            return;
        }

        UpdateNeutralBadge(ModeBadgeBorder, ModeBadgeText, "Доставка выключена");
    }

    private static void UpdateHealthBadge(Border border, TextBlock label, string state)
    {
        var text = TrayLabelCanon.DescribeHealthState(state);
        if (TrayStateClassifier.IsHealthyState(state))
        {
            UpdateBadge(border, label, text, MediaColor.FromRgb(29, 73, 53), MediaColor.FromRgb(167, 227, 191));
            return;
        }

        if (TrayStateClassifier.IsWarningState(state))
        {
            UpdateBadge(border, label, text, MediaColor.FromRgb(81, 64, 28), MediaColor.FromRgb(241, 206, 121));
            return;
        }

        if (TrayStateClassifier.IsErrorState(state))
        {
            UpdateErrorBadge(border, label, text);
            return;
        }

        UpdateNeutralBadge(border, label, text);
    }

    private static void UpdateAgentStatusBadge(Border border, TextBlock label, string status)
    {
        var text = TrayLabelCanon.DescribeAgentStatus(status);
        if (string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase))
        {
            UpdateBadge(border, label, text, MediaColor.FromRgb(29, 73, 53), MediaColor.FromRgb(167, 227, 191));
            return;
        }

        if (string.Equals(status, "Starting", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Stopped", StringComparison.OrdinalIgnoreCase))
        {
            UpdateBadge(border, label, text, MediaColor.FromRgb(81, 64, 28), MediaColor.FromRgb(241, 206, 121));
            return;
        }

        if (string.Equals(status, "Faulted", StringComparison.OrdinalIgnoreCase))
        {
            UpdateErrorBadge(border, label, text);
            return;
        }

        UpdateNeutralBadge(border, label, text);
    }

    private static void UpdateErrorBadge(Border border, TextBlock label, string text) =>
        UpdateBadge(border, label, text, MediaColor.FromRgb(84, 45, 51), MediaColor.FromRgb(255, 180, 184));

    private static void UpdateNeutralBadge(Border border, TextBlock label, string text) =>
        UpdateBadge(border, label, text, MediaColor.FromRgb(52, 62, 74), MediaColor.FromRgb(212, 220, 230));

    private static void UpdateBadge(Border border, TextBlock label, string text, MediaColor backColor, MediaColor foreColor)
    {
        label.Text = text;
        border.Background = new SolidColorBrush(backColor);
        label.Foreground = new SolidColorBrush(foreColor);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private bool IsLocalModeSelected() => TelegramDeliveryModeRadio.IsChecked == true;

    private string ResolveDeliveryModeForSave() =>
        DeliveryEnabledInput.IsChecked == true
            ? IsLocalModeSelected() ? "Local" : "Cloud"
            : "Disabled";

    private static string TranslateMode(string mode) =>
        string.Equals(mode, "Cloud", StringComparison.OrdinalIgnoreCase)
            ? "Облако"
            : string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase)
                ? "Локально / Telegram"
                : string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase)
                    ? "Выключено"
                    : mode;

    internal static int CalculateOverviewSplitDistance(
        int totalHeight,
        int splitterWidth,
        int panel1MinSize,
        int panel2MinSize,
        int preferredTopHeight)
    {
        var availableHeight = totalHeight - splitterWidth;
        if (availableHeight <= 0)
        {
            return 0;
        }

        var maxTopHeight = Math.Max(0, availableHeight - panel2MinSize);
        var minTopHeight = Math.Min(panel1MinSize, maxTopHeight);
        return Math.Clamp(preferredTopHeight, minTopHeight, maxTopHeight);
    }

    internal static bool TryCreateTraySettingsRequest(
        string deliveryMode,
        bool telegramDeliveryEnabled,
        string? ignoredWindowTitlesText,
        bool disconnectNotificationEnabled,
        string? ghostDisconnectMessageTemplate,
        bool processExitedNotificationEnabled,
        string? processExitedMessageTemplate,
        bool deadStartedNotificationEnabled,
        string? deadStartedMessageTemplate,
        out UpdateTraySettingsRequestDto? request,
        out string? validationMessage)
    {
        request = new UpdateTraySettingsRequestDto(new TrayEditableSettingsDto(
            deliveryMode,
            telegramDeliveryEnabled,
            NormalizeOptionalMultilineText(ignoredWindowTitlesText),
            disconnectNotificationEnabled,
            NormalizeOptionalText(ghostDisconnectMessageTemplate),
            processExitedNotificationEnabled,
            NormalizeOptionalText(processExitedMessageTemplate),
            deadStartedNotificationEnabled,
            NormalizeOptionalText(deadStartedMessageTemplate),
            BackendBaseUrl: null));
        validationMessage = null;
        return true;
    }

    internal static bool TryCreateSettingsRequest(
        int loopbackPort,
        int pollIntervalMs,
        int idleTimeoutSec,
        int minConfirmLifetimeSec,
        string deliveryMode,
        string? backendBaseUrl,
        bool telegramDeliveryEnabled,
        string? telegramChatId,
        string? ignoredWindowTitlesText,
        string? ghostDisconnectMessageTemplate,
        string? clientDisconnectedMessageTemplate,
        string? processExitedMessageTemplate,
        out UpdateSettingsRequestDto? request,
        out string? validationMessage)
    {
        if (!TryParseOptionalChatId(telegramChatId, out var parsedChatId))
        {
            request = null;
            validationMessage = "Идентификатор чата Telegram должен быть целым числом или пустым.";
            return false;
        }

        request = new UpdateSettingsRequestDto(new LocalControlSettingsDto(
            loopbackPort,
            pollIntervalMs,
            idleTimeoutSec,
            minConfirmLifetimeSec,
            deliveryMode,
            NormalizeOptionalText(backendBaseUrl),
            telegramDeliveryEnabled,
            parsedChatId,
            NormalizeOptionalMultilineText(ignoredWindowTitlesText),
            NormalizeOptionalText(ghostDisconnectMessageTemplate),
            NormalizeOptionalText(clientDisconnectedMessageTemplate),
            NormalizeOptionalText(processExitedMessageTemplate)));
        validationMessage = null;
        return true;
    }

    internal static bool TryCreateSettingsRequest(
        int loopbackPort,
        int pollIntervalMs,
        int idleTimeoutSec,
        int minConfirmLifetimeSec,
        string deliveryMode,
        string? backendBaseUrl,
        bool telegramDeliveryEnabled,
        string? telegramChatId,
        out UpdateSettingsRequestDto? request,
        out string? validationMessage) =>
        TryCreateSettingsRequest(
            loopbackPort,
            pollIntervalMs,
            idleTimeoutSec,
            minConfirmLifetimeSec,
            deliveryMode,
            backendBaseUrl,
            telegramDeliveryEnabled,
            telegramChatId,
            null,
            null,
            null,
            null,
            out request,
            out validationMessage);

    internal static bool TryParseOptionalChatId(string? value, out long? chatId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            chatId = null;
            return true;
        }

        if (long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            chatId = result;
            return true;
        }

        chatId = null;
        return false;
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeOptionalMultilineText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var lines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private async Task StartTelegramChatLinkAsync()
    {
        try
        {
            SetBusyState("Запрашиваю код привязки Telegram...");
            var response = await _apiClient.StartTelegramChatLinkAsync(CancellationToken.None).ConfigureAwait(true);
            _pendingTelegramLinkCode = response.Code;
            TelegramLinkCodeText.Text = string.IsNullOrWhiteSpace(response.Code) ? "Код не получен" : response.Code;
            TelegramLinkHintText.Text = "Отправьте код боту в личном чате, затем подтвердите привязку.";
            TelegramLinkHintText.Foreground = new SolidColorBrush(MediaColor.FromRgb(98, 112, 134));
            SetReadyState("Код привязки получен.");
        }
        catch (Exception ex) when (ex is LocalControlApiException or AgentConnectionException)
        {
            var message = TrayLabelCanon.DescribeFailure(ex);
            System.Windows.MessageBox.Show(this, message, "Не удалось получить код привязки", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetReadyState(message);
        }
        finally
        {
            UpdateDeliveryModePanels();
        }
    }

    private async Task ConfirmTelegramChatLinkAsync()
    {
        try
        {
            SetBusyState("Подтверждаю привязку Telegram по коду...");
            var response = await _apiClient.ConfirmTelegramChatLinkAsync(CancellationToken.None).ConfigureAwait(true);
            var presentation = TrayLabelCanon.DescribeActionResult(response.Result);
            if (string.Equals(response.Result.Outcome, "completed", StringComparison.OrdinalIgnoreCase))
            {
                ClearPendingTelegramLinkUi("Чат подтверждён и привязан.");
            }
            else
            {
                TelegramLinkHintText.Text = presentation;
                TelegramLinkHintText.Foreground = new SolidColorBrush(MediaColor.FromRgb(146, 64, 14));
            }

            await RefreshNowAsync(forceSettings: false).ConfigureAwait(true);
            SetReadyState(presentation);
        }
        catch (Exception ex) when (ex is LocalControlApiException or AgentConnectionException)
        {
            var message = TrayLabelCanon.DescribeFailure(ex);
            System.Windows.MessageBox.Show(this, message, "Не удалось подтвердить привязку", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetReadyState(message);
        }
        finally
        {
            UpdateDeliveryModePanels();
        }
    }

    private void ClearPendingTelegramLinkUi(string hintText)
    {
        _pendingTelegramLinkCode = null;
        TelegramLinkCodeText.Text = "Сначала нажмите «Получить код».";
        TelegramLinkHintText.Text = hintText;
        TelegramLinkHintText.Foreground = new SolidColorBrush(MediaColor.FromRgb(98, 112, 134));
        UpdateDeliveryModePanels();
    }

    private void CopyTelegramLinkCode()
    {
        if (string.IsNullOrWhiteSpace(_pendingTelegramLinkCode))
        {
            return;
        }

        System.Windows.Clipboard.SetText(_pendingTelegramLinkCode);
        SetReadyState("Код привязки скопирован.");
    }

    private sealed record ConnectionRow(string WindowTitle, string State, string ObservedAt);
    private sealed record IncidentRow(string Severity, string Kind, string Summary, string OccurredAt);
}


