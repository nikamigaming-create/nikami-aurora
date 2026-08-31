using System.Text.Json;
using Godot;

namespace Nikami.Aurora.GodotRuntime.Launcher;

internal sealed record InstallationScan(
    string GameRoot,
    IReadOnlyList<string> Archives,
    IReadOnlyList<string> Areas,
    IReadOnlyList<string> Movies,
    string GuiArchive);

internal static class InstallationLocator
{
    public const string GuiArchiveRelativePath = "packages/core/data/guiexport.erf";

    public static string RootFromExecutable(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("That executable does not exist.", fullPath);
        }
        if (!Path.GetFileName(fullPath).Equals("DAOrigins.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Select DAOrigins.exe from the bin_ship directory.");
        }

        var executableDirectory = Directory.GetParent(fullPath)?.FullName;
        if (executableDirectory is null ||
            !Path.GetFileName(executableDirectory).Equals("bin_ship", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("DAOrigins.exe must be inside the bin_ship directory.");
        }
        return Directory.GetParent(executableDirectory)?.FullName
            ?? throw new InvalidDataException("The game root could not be resolved.");
    }

    public static InstallationScan Scan(string gameRoot)
    {
        var normalizedRoot = GameInstallation.NormalizeRoot(gameRoot);
        GameInstallation.ValidateRoot(normalizedRoot);

        var files = Directory.EnumerateFiles(normalizedRoot, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden |
                FileAttributes.System |
                FileAttributes.ReparsePoint
        })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var archives = files.Where(path =>
                Path.GetExtension(path).Equals(".erf", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".rim", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var areas = files.Where(path =>
                Path.GetExtension(path).Equals(".are", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var movies = files.Where(path =>
                Path.GetExtension(path).Equals(".bik", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var guiArchive = Path.Combine(
            normalizedRoot,
            GuiArchiveRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return new InstallationScan(normalizedRoot, archives, areas, movies, guiArchive);
    }
}

internal static class GameInstallation
{
    public const string RootEnvironmentVariable = "DRAGON_AGE_GODOT_GAME_ROOT";
    public const string SettingsPath = "user://runtime-settings.json";

    public static readonly string[] DefaultExecutableDirectories =
    [
        Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "Dragon Age Ultimate Edition", "bin_ship"),
        Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "Dragon Age Origins", "bin_ship")
    ];

    public static string DefaultRoot()
    {
        foreach (var executableDirectory in DefaultExecutableDirectories)
        {
            var executable = Path.Combine(executableDirectory, "DAOrigins.exe");
            if (!File.Exists(executable))
            {
                continue;
            }
            try
            {
                var root = InstallationLocator.RootFromExecutable(executable);
                ValidateRoot(root);
                return root;
            }
            catch (IOException)
            {
                // Continue to the next conventional installation location.
            }
        }
        return string.Empty;
    }

    public static string ResolveConfiguredRoot()
    {
        var environmentRoot = System.Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return NormalizeRoot(environmentRoot);
        }

        try
        {
            var store = new RuntimeSettingsStore(ProjectSettings.GlobalizePath(SettingsPath));
            if (store.Exists)
            {
                return NormalizeRoot(store.Load().GameRoot);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            // A corrupt preference must not block manual installation selection.
        }
        return DefaultRoot();
    }

    public static string NormalizeRoot(string selectedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedDirectory);
        var expanded = System.Environment.ExpandEnvironmentVariables(selectedDirectory.Trim().Trim('"'));
        var fullPath = Path.GetFullPath(expanded);
        if (File.Exists(fullPath))
        {
            return InstallationLocator.RootFromExecutable(fullPath);
        }
        if (Path.GetFileName(fullPath).Equals("bin_ship", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(fullPath)?.FullName
                ?? throw new InvalidDataException("The game root could not be resolved.");
        }
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static void ValidateRoot(string normalizedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRoot);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException("Dragon Age installation was not found: " + normalizedRoot);
        }

        var executable = Path.Combine(normalizedRoot, "bin_ship", "DAOrigins.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("DAOrigins.exe was not found in bin_ship.", executable);
        }
        var guiArchive = Path.Combine(
            normalizedRoot,
            InstallationLocator.GuiArchiveRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(guiArchive))
        {
            throw new FileNotFoundException("The installed GUI archive was not found.", guiArchive);
        }
    }
}

internal sealed record RuntimeSettings(string GameRoot, bool MusicMuted);

internal sealed class RuntimeSettingsStore(string path)
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public bool Exists => File.Exists(path);

    public RuntimeSettings Load()
    {
        var settings = JsonSerializer.Deserialize<RuntimeSettings>(
            File.ReadAllText(path),
            ReadOptions);
        return settings is not null && !string.IsNullOrWhiteSpace(settings.GameRoot)
            ? settings
            : throw new InvalidDataException("Runtime settings do not contain a game root.");
    }

    public void Save(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(settings, WriteOptions) + System.Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
