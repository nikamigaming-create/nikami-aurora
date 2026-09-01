using System.Text.Json.Nodes;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Catalogs;

public sealed class GdaExperienceTableProvider(IJsonStore store, IRuntimeEnvironment environment)
{
    public string Error { get; private set; } = string.Empty;

    public DragonAgeOriginsExperienceTable? Load()
    {
        Error = string.Empty;
        var path = ResolveCatalogPath();
        if (path.Length == 0) return Fail("experience-catalog-absent");
        var document = store.Read(path);
        if (document?["schema"]?.GetValue<string>() != "opendao-gda-catalog-v1" ||
            document["tables"]?[DragonAgeOriginsExperienceTable.TableName] is not JsonObject table ||
            table["columns"] is not JsonArray columns || table["rows"] is not JsonArray rows)
            return Fail("experience-table-absent");

        var indices = new Dictionary<long, int>();
        for (var index = 0; index < columns.Count; index++)
            if (columns[index]?["hash"] is JsonValue hash && hash.TryGetValue<long>(out var value))
                indices[value] = index;
        if (!indices.TryGetValue(DragonAgeOriginsExperienceTable.LevelColumnHash, out var levelIndex) ||
            !indices.TryGetValue(DragonAgeOriginsExperienceTable.MinimumExperienceColumnHash,
                out var experienceIndex))
            return Fail("experience-columns-absent");

        var thresholds = new List<DragonAgeLevelThreshold>();
        foreach (var row in rows.OfType<JsonArray>())
        {
            if (levelIndex >= row.Count || experienceIndex >= row.Count ||
                row[levelIndex] is not JsonValue levelValue ||
                row[experienceIndex] is not JsonValue experienceValue ||
                !levelValue.TryGetValue<int>(out var level) ||
                !experienceValue.TryGetValue<int>(out var experience))
                return Fail("experience-row-invalid");
            thresholds.Add(new DragonAgeLevelThreshold(level, experience));
        }

        try { return new DragonAgeOriginsExperienceTable(thresholds); }
        catch (InvalidDataException) { return Fail("experience-thresholds-invalid"); }
    }

    private string ResolveCatalogPath()
    {
        var configured = environment.Get("OPENDAO_GDA_CATALOG");
        var candidates = new[]
        {
            configured,
            DaoRuntimePaths.Cache("dao-gda.json"),
            Path.Combine(environment.ExecutableDirectory, "Nikami.Aurora.GodotRuntimeData",
                "cache", "dao-world", "dao-gda.json"),
            Path.Combine(environment.ExecutableDirectory, "Nikami.Aurora.GodotRuntimeData",
                "dao-world", "dao-gda.json")
        };
        return candidates.FirstOrDefault(path => path.Length > 0 && store.Exists(path)) ?? string.Empty;
    }

    private DragonAgeOriginsExperienceTable? Fail(string error)
    {
        Error = error;
        return null;
    }
}
