namespace WowVR.Formats.Assets;

/// <summary>The MPQ archives of a 3.3.5a client, in load order. Later archives override earlier ones.</summary>
public sealed record ClientArchives(string Locale, IReadOnlyList<string> Paths)
{
    private static readonly string[] BaseArchives =
        ["common.MPQ", "common-2.MPQ", "expansion.MPQ", "lichking.MPQ"];

    private static readonly string[] LocaleArchiveFormats =
    [
        "locale-{0}.MPQ",
        "speech-{0}.MPQ",
        "expansion-locale-{0}.MPQ",
        "lichking-locale-{0}.MPQ",
        "expansion-speech-{0}.MPQ",
        "lichking-speech-{0}.MPQ",
    ];

    public static ClientArchives Find(string clientPath)
    {
        var dataDirectory = Path.Combine(clientPath, "Data");
        if (!Directory.Exists(dataDirectory))
        {
            throw new DirectoryNotFoundException($"No Data folder in the client path: {clientPath}");
        }

        var localeDirectory = FindLocaleDirectory(dataDirectory);
        var locale = Path.GetFileName(localeDirectory);

        var paths = new List<string>();
        var dataFiles = FilesByName(dataDirectory);
        var localeFiles = FilesByName(localeDirectory);

        paths.AddRange(Existing(dataFiles, BaseArchives));
        paths.AddRange(Existing(localeFiles, LocaleArchiveFormats.Select(f => string.Format(f, locale))));
        paths.AddRange(Patches(dataFiles, "patch"));
        paths.AddRange(Patches(localeFiles, $"patch-{locale}"));

        return new ClientArchives(locale, paths);
    }

    private static string FindLocaleDirectory(string dataDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(dataDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (FilesByName(directory).ContainsKey($"locale-{name}.MPQ"))
            {
                return directory;
            }
        }

        throw new DirectoryNotFoundException($"No locale folder (such as enUS) with a locale archive in {dataDirectory}");
    }

    /// <summary>Keyed case-insensitively, because file systems and installs disagree on case.</summary>
    private static Dictionary<string, string> FilesByName(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Existing(Dictionary<string, string> files, IEnumerable<string> names) =>
        names.Where(files.ContainsKey).Select(name => files[name]);

    /// <summary><c>prefix.MPQ</c>, then <c>prefix-*.MPQ</c> in name order.</summary>
    private static IEnumerable<string> Patches(Dictionary<string, string> files, string prefix)
    {
        var numbered = files.Keys
            .Where(name => name.StartsWith($"{prefix}-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".MPQ", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

        return Existing(files, [$"{prefix}.MPQ", .. numbered]);
    }
}
