// Matthew W, 2026-08-12

namespace OpenDAO.Launcher;

internal sealed record IntroMovieEntry(string ResourceName, string PhysicalPath);

internal sealed record IntroSequencePlan(
    IReadOnlyList<IntroMovieEntry> Movies,
    IReadOnlyList<string> Diagnostics)
{
    private static readonly string[] DefaultIntroMovies =
    [
        "dragon_age_ea_logo.bik",
        "dragon_age_main.bik"
    ];

    public static IntroSequencePlan Build(InstallationScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        var movies = new List<IntroMovieEntry>();
        var diagnostics = new List<string>();
        var ini = ReadIniMovieSection(diagnostics);

        if (ini.TryGetValue("DisableIntroMovies", out var disabled) &&
            (disabled == "1" || disabled.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add("DragonAge.ini disables the original intro movies.");
            return new IntroSequencePlan(movies, diagnostics);
        }

        foreach (var resourceName in ReadOrderedMovieNames(ini))
        {
            var match = scan.Movies.FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                resourceName,
                StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                diagnostics.Add($"{resourceName} was not found in the installation.");
                continue;
            }

            movies.Add(new IntroMovieEntry(resourceName, match));
        }

        return new IntroSequencePlan(movies, diagnostics);
    }

    private static IReadOnlyList<string> ReadOrderedMovieNames(
        IReadOnlyDictionary<string, string> ini)
    {
        var assigned = ini
            .Where(entry => entry.Key.StartsWith("Movie", StringComparison.OrdinalIgnoreCase) &&
                entry.Key.Length > 5 &&
                int.TryParse(entry.Key.AsSpan(5), out _) &&
                entry.Value.Length > 0)
            .OrderBy(entry => int.Parse(entry.Key[5..]))
            .Select(entry => entry.Value)
            .ToArray();

        return assigned.Length > 0 ? assigned : DefaultIntroMovies;
    }

    private static IReadOnlyDictionary<string, string> ReadIniMovieSection(
        ICollection<string> diagnostics)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settingsPath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
            "BioWare",
            "Dragon Age",
            "Settings",
            "DragonAge.ini");
        if (!File.Exists(settingsPath))
        {
            diagnostics.Add("DragonAge.ini was not found; using the shipped intro order.");
            return values;
        }

        try
        {
            var inMoviesSection = false;
            foreach (var rawLine in File.ReadLines(settingsPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inMoviesSection = line[1..^1].Equals(
                        "Movies",
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inMoviesSection)
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        catch (IOException exception)
        {
            diagnostics.Add($"DragonAge.ini could not be read: {exception.Message}");
        }

        return values;
    }
}
