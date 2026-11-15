using System;
using System.Collections.Generic;
using Jellyfin.Plugin.CodecConverter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CodecConverter;

/// <summary>
/// Wandelt Filme und Folgen per ffmpeg in einen anderen Videocodec um.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Pfade des Servers.</param>
    /// <param name="xmlSerializer">XML-Serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets die laufende Instanz.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Codec Converter";

    /// <inheritdoc />
    public override string Description => "Wandelt Filme und Folgen per ffmpeg in einen anderen Videocodec um.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7b738b42-d885-4a24-8312-97bdd0e0e32f");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "CodecConverter",
                DisplayName = "Codec Converter",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuIcon = "movie_filter"
            }
        };
    }
}
