using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CodecConverter.Services;

/// <summary>
/// Bezeichnungen fuer Versionen, gleich wie im Naming Standardizer: Fassung (falls im Namen) und Codec,
/// nur Buchstaben und Ziffern, getrennt mit "-" ("Extended-HEVC").
/// </summary>
public static class NamingLabels
{
    private static readonly (Regex Pattern, string Label)[] _editions =
    {
        (new Regex(@"(?<![a-z0-9])extended(?![a-z0-9])", RegexOptions.IgnoreCase), "Extended"),
        (new Regex(@"(?<![a-z0-9])director'?s[ ._-]?cut(?![a-z0-9])", RegexOptions.IgnoreCase), "DirectorsCut"),
        (new Regex(@"(?<![a-z0-9])final[ ._-]?cut(?![a-z0-9])", RegexOptions.IgnoreCase), "FinalCut"),
        (new Regex(@"(?<![a-z0-9])unrated(?![a-z0-9])", RegexOptions.IgnoreCase), "Unrated"),
        (new Regex(@"(?<![a-z0-9])uncut(?![a-z0-9])", RegexOptions.IgnoreCase), "Uncut"),
        (new Regex(@"(?<![a-z0-9])remaster(ed)?(?![a-z0-9])", RegexOptions.IgnoreCase), "Remastered"),
        (new Regex(@"(?<![a-z0-9])theatrical(?![a-z0-9])", RegexOptions.IgnoreCase), "Theatrical"),
        (new Regex(@"(?<![a-z0-9])imax(?![a-z0-9])", RegexOptions.IgnoreCase), "IMAX"),
        (new Regex(@"(?<![a-z0-9])special[ ._-]?edition(?![a-z0-9])", RegexOptions.IgnoreCase), "SpecialEdition"),
        (new Regex(@"(?<![a-z0-9])ultimate(?![a-z0-9])", RegexOptions.IgnoreCase), "Ultimate"),
        (new Regex(@"(?<![a-z0-9])kinofassung(?![a-z0-9])", RegexOptions.IgnoreCase), "Kinofassung"),
        (new Regex(@"(?<![a-z0-9])langfassung(?![a-z0-9])", RegexOptions.IgnoreCase), "Langfassung")
    };

    /// <summary>
    /// Die Fassung(en), die im Dateinamen stehen, z. B. "Extended", oder null.
    /// </summary>
    /// <param name="name">Dateiname ohne Endung.</param>
    /// <returns>Die Fassung oder null.</returns>
    public static string? Edition(string name)
    {
        var found = _editions.Where(e => e.Pattern.IsMatch(name)).Select(e => e.Label).ToList();
        return found.Count == 0 ? null : string.Join("-", found);
    }

    /// <summary>
    /// Bezeichnung einer Version aus Fassung und Codec.
    /// </summary>
    /// <param name="edition">Die Fassung oder null.</param>
    /// <param name="codec">Der Codec, wie ffprobe ihn nennt.</param>
    /// <returns>Die Bezeichnung.</returns>
    public static string Label(string? edition, string codec)
        => edition is null ? EncoderCatalog.CodecLabel(codec) : edition + "-" + EncoderCatalog.CodecLabel(codec);
}
