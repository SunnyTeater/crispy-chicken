using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CodecConverter.Configuration;

/// <summary>
/// Einstellungen des Plugins.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets den Sicherungsordner fuer Originale. Leer = Standardordner im Jellyfin-Datenordner.
    /// </summary>
    public string BackupPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets die Aufbewahrung der Originale in Tagen. 0 = nie automatisch loeschen.
    /// </summary>
    public int BackupRetentionDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets die Stream-Grenze: Jobs laufen nur, solange weniger Streams aktiv sind.
    /// </summary>
    public int StreamLimit { get; set; } = 2;

    /// <summary>
    /// Gets or sets die Anzahl der ffmpeg-Threads. 0 = automatisch.
    /// </summary>
    public int Threads { get; set; }

    /// <summary>
    /// Gets or sets das VAAPI-Geraet fuer die VAAPI-Encoder.
    /// </summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";
}
