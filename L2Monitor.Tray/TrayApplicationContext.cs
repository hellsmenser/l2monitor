using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using L2Monitor.Tray.Api;
using L2Monitor.Tray.Bootstrap;
using L2Monitor.Tray.Presentation;
using L2Monitor.Tray.Ui;
using L2Monitor.Tray.Updates;
using L2Monitor.Core.Api;
using Microsoft.Extensions.Logging;

namespace L2Monitor.Tray;

internal sealed class TrayApplicationContext : IDisposable
{
    private readonly MainWindow _mainWindow;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _openMenuItem;
    private readonly Forms.ToolStripMenuItem _refreshMenuItem;
    private readonly Forms.ToolStripMenuItem _testNotificationMenuItem;
    private readonly Forms.ToolStripMenuItem _reloadMenuItem;
    private readonly Forms.ToolStripMenuItem _restartMenuItem;
    private readonly Forms.ToolStripMenuItem _exitMenuItem;
    private readonly DispatcherTimer _refreshTimer;
    private readonly ILogger<TrayApplicationContext> _logger;
    private readonly WpfApplication _application;
    private readonly Icon _applicationIcon;
    private readonly UpdateNotificationStateStore _updateNotificationStore;
    private UpdateNotificationRecord? _lastUpdateNotification;

    public TrayApplicationContext(
        LocalControlApiClient apiClient,
        IAutostartService autostartService,
        ILogger<TrayApplicationContext> logger,
        WpfApplication application,
        bool startMinimized)
    {
        _application = application;
        _logger = logger;
        _applicationIcon = LoadApplicationIcon();
        _updateNotificationStore = new UpdateNotificationStateStore();
        _lastUpdateNotification = _updateNotificationStore.Load();
        _mainWindow = new MainWindow(apiClient, autostartService);
        _mainWindow.TraySummaryChanged += OnTraySummaryChanged;
        _mainWindow.UpdateStatusChanged += OnUpdateStatusChanged;

        _openMenuItem = new Forms.ToolStripMenuItem("Открыть окно", null, (_, _) => _mainWindow.ShowWindow());
        _refreshMenuItem = new Forms.ToolStripMenuItem("Обновить", null, async (_, _) => await _mainWindow.RefreshNowAsync(forceSettings: false).ConfigureAwait(true));
        _testNotificationMenuItem = new Forms.ToolStripMenuItem("Тест уведомления", null, async (_, _) => await _mainWindow.TriggerTestNotificationAsync().ConfigureAwait(true));
        _reloadMenuItem = new Forms.ToolStripMenuItem("Перечитать конфиг", null, async (_, _) => await _mainWindow.TriggerReloadAsync().ConfigureAwait(true));
        _restartMenuItem = new Forms.ToolStripMenuItem("Перезапустить агент", null, async (_, _) => await _mainWindow.TriggerRestartAsync().ConfigureAwait(true));
        _exitMenuItem = new Forms.ToolStripMenuItem("Выйти из Aden+", null, async (_, _) => await ExitApplicationAsync().ConfigureAwait(true));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            _openMenuItem,
            _refreshMenuItem,
            new Forms.ToolStripSeparator(),
            _testNotificationMenuItem,
            _reloadMenuItem,
            _restartMenuItem,
            new Forms.ToolStripSeparator(),
            _exitMenuItem,
        ]);

        _notifyIcon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "Aden+",
            Icon = _applicationIcon,
            ContextMenuStrip = menu,
        };

        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
        _notifyIcon.BalloonTipClicked += (_, _) => _mainWindow.ShowWindow();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _refreshTimer.Tick += async (_, _) => await _mainWindow.RefreshNowAsync(forceSettings: false).ConfigureAwait(true);
        _refreshTimer.Start();

        if (startMinimized)
        {
            _mainWindow.Show();
            _mainWindow.Hide();
        }
        else
        {
            _mainWindow.ShowWindow();
        }
        _ = _mainWindow.RefreshNowAsync(forceSettings: true);
    }

    private void OnNotifyIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _mainWindow.ShowWindow();
        }
    }

    private void OnTraySummaryChanged(object? sender, TrayPresentationSummary summary)
    {
        _notifyIcon.Icon = SelectTrayIcon(_applicationIcon, summary.Severity);

        _notifyIcon.Text = summary.Tooltip.Length <= 63
            ? summary.Tooltip
            : summary.Tooltip[..63];
    }

    private void OnUpdateStatusChanged(object? sender, ClientUpdateDto update)
    {
        var now = DateTimeOffset.UtcNow;
        if (!UpdateNotificationPolicy.ShouldNotify(update, _lastUpdateNotification, now))
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = "Доступно обновление Aden+";
        _notifyIcon.BalloonTipText = update.Required
            ? $"Ваша версия {update.CurrentVersion} устарела. Доступна {update.LatestVersion}. Обновление обязательно. Откройте Aden+."
            : $"Ваша версия {update.CurrentVersion} устарела. Доступна {update.LatestVersion}. Откройте Aden+ для обновления.";
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(10000);

        _lastUpdateNotification = new UpdateNotificationRecord(update.LatestVersion!, now);
        try
        {
            _updateNotificationStore.Save(_lastUpdateNotification);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Failed to persist update-notification throttle state. ExceptionType={ExceptionType}",
                ex.GetType().Name);
        }
    }

    private async Task ExitApplicationAsync()
    {
        _logger.LogInformation("Aden+ exit requested.");
        _refreshTimer.Stop();
        _notifyIcon.Visible = false;

        try
        {
            await _mainWindow.StopAgentAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aden+ agent did not acknowledge shutdown.");
        }

        _mainWindow.RequestExit();
        _mainWindow.Close();
        _application.Shutdown();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var icon = Icon.ExtractAssociatedIcon(executablePath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Fall back to a cloned system icon if Windows cannot read the executable resource.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    internal static Icon SelectTrayIcon(Icon applicationIcon, TraySeverity _) => applicationIcon;
}
