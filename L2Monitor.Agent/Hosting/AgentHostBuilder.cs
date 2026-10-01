using L2Monitor.Agent.Backend;
using L2Monitor.Agent.Runtime;
using L2Monitor.Core.Diagnostics;
using L2Monitor.Infrastructure.Windows.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace L2Monitor.Agent.Hosting;

internal static class AgentHostBuilder
{
    public static IHost Build(
        string[] args,
        Action<WebApplicationBuilder>? configureBuilder = null,
        int? loopbackPortOverride = null)
    {
        var launchOptions = AgentLaunchOptions.Parse(args);
        var builder = WebApplication.CreateBuilder(args);
        var bootstrapSettings = AgentConfigurationService.LoadBootstrapSettings();

        builder.Environment.ContentRootPath = AppContext.BaseDirectory;

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            options.SingleLine = true;
        });
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new BoundedFileLoggerProvider(
            Path.Combine(AgentConfigurationService.ResolveRootDirectory(), "logs", "agent.log")));

        builder.Services.AddSingleton(launchOptions);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AgentRuntimeStateStore>();
        builder.Services.AddSingleton<AgentLifecycleMarkerStore>();
        builder.Services.AddSingleton<AgentControlStateStore>();
        builder.Services.AddSingleton(AgentRuntimeOptions.Default);
        builder.Services.AddSingleton<AgentConfigurationService>();
        builder.Services.AddSingleton<IAgentRestartCoordinator, AgentRestartCoordinator>();
        builder.Services.AddHttpClient();
        builder.Services
            .AddHttpClient(AgentNotificationSender.TelegramHttpClientName)
            .RemoveAllLoggers();
        builder.Services.AddL2MonitorWindowsInfrastructure();
        builder.Services.AddSingleton<IAgentRuntimeProbe, WindowsMonitorProbeAdapter>();
        builder.Services.AddSingleton<IAgentNotificationSender, AgentNotificationSender>();
        builder.Services.AddSingleton<AgentCommandService>();
        builder.Services.AddHostedService<AgentBackendConnectionService>();
        builder.Services.AddHostedService<AgentRuntimeService>();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(
                System.Net.IPAddress.Loopback,
                loopbackPortOverride ?? bootstrapSettings.Loopback.Port);
        });

        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        app.MapLocalControlApi();
        return app;
    }
}
