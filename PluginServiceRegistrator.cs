using Jellyfin.Plugin.CodecConverter.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CodecConverter;

/// <summary>
/// Registriert die Dienste des Plugins.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LibraryIndex>();
        serviceCollection.AddSingleton<JobStore>();
        serviceCollection.AddSingleton<LiveLog>();
        serviceCollection.AddSingleton<SourceInfoCache>();
        serviceCollection.AddSingleton<LinkKeeper>();
        serviceCollection.AddSingleton<JobRunner>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<JobRunner>());
    }
}
