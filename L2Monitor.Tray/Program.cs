using System.Net.Http;
using System.IO;
using System.Windows;
using L2Monitor.Core.Diagnostics;
using L2Monitor.Tray.Api;
using L2Monitor.Tray.Bootstrap;
using Microsoft.Extensions.Logging;
using WpfApplication = System.Windows.Application;

namespace L2Monitor.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\AdenPlus.Tray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                "Aden+ уже запущен. Откройте приложение через значок в области уведомлений.",
                "Aden+",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new BoundedFileLoggerProvider(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "L2Monitor",
                    "tray",
                    "logs",
                    "tray.log")));
        });
        var logger = loggerFactory.CreateLogger("Aden+");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            logger.LogError(args.ExceptionObject as Exception, "Unhandled app-domain exception. Terminating: {IsTerminating}", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
        logger.LogInformation("Aden+ starting.");

        var agentLaunch = new AgentProcessLauncher().EnsureStarted();
        if (!agentLaunch.IsAvailable)
        {
            logger.LogError("Aden+ agent startup failed. Error={Error}", agentLaunch.ErrorMessage);
        }
        else if (agentLaunch.WasStarted)
        {
            logger.LogInformation("Aden+ agent started. Path={AgentPath}", agentLaunch.ExecutablePath);
        }

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        var bootstrapDiscovery = new AgentBootstrapDiscovery();
        var apiClient = new LocalControlApiClient(httpClient, bootstrapDiscovery);
        var app = new WpfApplication
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        app.DispatcherUnhandledException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unhandled WPF dispatcher exception.");
            args.Handled = true;
        };

        var autostartService = new WindowsAutostartService();
        using var trayContext = new TrayApplicationContext(
            apiClient,
            autostartService,
            loggerFactory.CreateLogger<TrayApplicationContext>(),
            app,
            startMinimized: ShouldStartMinimized(args));
        app.Run();
        logger.LogInformation("Aden+ stopped.");
    }

    internal static bool ShouldStartMinimized(IEnumerable<string> args) =>
        args.Any(static arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
}
