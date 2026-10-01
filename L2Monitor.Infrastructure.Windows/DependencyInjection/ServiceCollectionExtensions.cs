using Microsoft.Extensions.DependencyInjection;

namespace L2Monitor.Infrastructure.Windows.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddL2MonitorWindowsInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IWindowsMonitorProbe, LiveWindowsMonitorProbe>();
        services.AddSingleton<DeathAudioDetector>();
        services.AddSingleton<IAudioDeathDetector>(static serviceProvider => serviceProvider.GetRequiredService<DeathAudioDetector>());
        services.AddHostedService(static serviceProvider => serviceProvider.GetRequiredService<DeathAudioDetector>());
        return services;
    }
}
