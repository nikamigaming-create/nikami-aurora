using System.Text.Json.Nodes;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.Abilities;
using Nikami.Aurora.GodotRuntime.Domain.Characters;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Catalogs;

public sealed class GdaCharacterAbilityLoadoutProvider(
    IJsonStore store,
    IRuntimeEnvironment environment) : ICharacterAbilityLoadoutProvider
{
    private const long IdHash = 1727777078;
    private const long LabelHash = 3030163814;
    private const long StartingAbility1Hash = 1301029803;
    private const long StartingAbility2Hash = 1721856104;
    private const long BackgroundAbilityHash = 2616054951;
    private static readonly long[] QuickSlotHashes =
    [
        3137953710, 2719117039, 2302623020, 2418281581,
        3747977898, 3330190315, 3981703208, 4098410857,
    ];

    public string Error { get; private set; } = string.Empty;

    public CharacterAbilityLoadout? Resolve(CharacterProfile character)
    {
        Error = string.Empty;
        var path = ResolveCatalogPath();
        if (path.Length == 0)
        {
            return Fail("character-loadout-catalog-absent");
        }

        var document = store.Read(path);
        if (document?["schema"]?.GetValue<string>() != "opendao-gda-catalog-v1" ||
            document["tables"] is not JsonObject tables)
        {
            return Fail("character-loadout-catalog-invalid");
        }

        if (!TryTable(tables, "cla_data", out var classes) ||
            !TryTable(tables, "background_defaults", out var backgrounds) ||
            !TryTable(tables, "qbar", out var quickbar))
        {
            return Fail("character-loadout-table-absent");
        }
        if (!HasColumns(classes, StartingAbility1Hash, StartingAbility2Hash) ||
            !HasColumns(backgrounds, BackgroundAbilityHash) ||
            !HasColumns(quickbar, QuickSlotHashes))
        {
            return Fail("character-loadout-column-absent");
        }

        var classLabel = character.Class.ToLowerInvariant() switch
        {
            "warrior" => "Warrior",
            "mage" => "Wizard",
            "rogue" => "Rogue",
            _ => string.Empty,
        };
        if (classLabel.Length == 0 || !TryRow(classes, classLabel, out var classRow))
        {
            return Fail("character-loadout-class-absent");
        }

        var backgroundLabel = BackgroundLabel(character);
        if (backgroundLabel.Length == 0 || !TryRow(backgrounds, backgroundLabel, out var backgroundRow))
        {
            return Fail("character-loadout-background-absent");
        }

        if (!TryRow(quickbar, "player", out var quickbarRow))
        {
            return Fail("character-loadout-quickbar-absent");
        }

        var quickSlots = new Dictionary<int, int>();
        for (var index = 0; index < QuickSlotHashes.Length; index++)
        {
            var abilityId = Int(quickbar, quickbarRow, QuickSlotHashes[index]);
            if (abilityId > 0)
            {
                quickSlots.TryAdd(abilityId, index + 1);
            }
        }

        var grants = new List<CharacterAbilityGrant>();
        AddGrant(grants, classes, classRow, StartingAbility1Hash, quickSlots);
        AddGrant(grants, classes, classRow, StartingAbility2Hash, quickSlots);
        AddGrant(grants, backgrounds, backgroundRow, BackgroundAbilityHash, quickSlots);
        var distinct = grants.GroupBy(value => value.AbilityId).Select(value => value.First()).ToArray();
        return new CharacterAbilityLoadout(path,
            document["source"]?["sha256"]?.GetValue<string>() ?? string.Empty,
            classLabel, backgroundLabel, distinct);
    }

    private string ResolveCatalogPath()
    {
        var configured = environment.Get("OPENDAO_GDA_CATALOG");
        var candidates = new[]
        {
            configured,
            DaoRuntimePaths.Cache("dao-gda.json"),
            Path.Combine(environment.ExecutableDirectory, "Nikami.Aurora.GodotRuntimeData", "cache", "dao-world", "dao-gda.json"),
            Path.Combine(environment.ExecutableDirectory, "Nikami.Aurora.GodotRuntimeData", "dao-world", "dao-gda.json"),
        };
        return candidates.FirstOrDefault(path => path.Length > 0 && store.Exists(path)) ?? string.Empty;
    }

    private static string BackgroundLabel(CharacterProfile character)
    {
        var race = character.Race.ToLowerInvariant() switch
        {
            "human" => "Human",
            "elf" => "Elf",
            "dwarf" => "Dwarf",
            _ => string.Empty,
        };
        var classLabel = character.Class.ToLowerInvariant() switch
        {
            "warrior" => "Warrior",
            "rogue" => "Rogue",
            "mage" => "Mage",
            _ => string.Empty,
        };
        if (race.Length == 0 || classLabel.Length == 0)
        {
            return string.Empty;
        }

        if (classLabel == "Mage")
        {
            return $"{race}, Mage";
        }

        var origin = character.Origin.ToLowerInvariant() switch
        {
            "city-elf" => "City",
            "dalish-elf" => "Dalish",
            "dwarf-commoner" => "Commoner",
            "dwarf-noble" or "human-noble" => "Noble",
            _ => string.Empty,
        };
        return origin.Length == 0 ? string.Empty : $"{race}, {origin} {classLabel}";
    }

    private static void AddGrant(List<CharacterAbilityGrant> grants, Table table, JsonArray row,
        long columnHash, IReadOnlyDictionary<int, int> quickSlots)
    {
        var abilityId = Int(table, row, columnHash);
        if (abilityId <= 0)
        {
            return;
        }

        grants.Add(new CharacterAbilityGrant(abilityId, quickSlots.GetValueOrDefault(abilityId),
            table.Name, table.Entry, table.Sha256, columnHash, Text(table, row, LabelHash)));
    }

    private static bool TryTable(JsonObject tables, string name, out Table table)
    {
        table = default!;
        if (tables[name] is not JsonObject source || source["columns"] is not JsonArray columns ||
            source["rows"] is not JsonArray rows)
        {
            return false;
        }

        var indices = new Dictionary<long, int>();
        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index]?["hash"] is JsonValue hash && hash.TryGetValue<long>(out var value))
            {
                indices[value] = index;
            }
        }

        if (!indices.ContainsKey(IdHash) || !indices.ContainsKey(LabelHash))
        {
            return false;
        }

        table = new Table(name, source["entry"]?.GetValue<string>() ?? string.Empty,
            source["sha256"]?.GetValue<string>() ?? string.Empty, indices, rows);
        return true;
    }

    private static bool TryRow(Table table, string label, out JsonArray row)
    {
        row = table.Rows.OfType<JsonArray>().FirstOrDefault(value =>
            Text(table, value, LabelHash).Equals(label, StringComparison.OrdinalIgnoreCase))!;
        return row is not null;
    }

    private static bool HasColumns(Table table, params long[] hashes) =>
        hashes.All(table.Indices.ContainsKey);

    private static int Int(Table table, JsonArray row, long hash) =>
        table.Indices.TryGetValue(hash, out var index) && index < row.Count &&
        row[index] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result : 0;

    private static string Text(Table table, JsonArray row, long hash) =>
        table.Indices.TryGetValue(hash, out var index) && index < row.Count
            ? row[index]?.ToString() ?? string.Empty
            : string.Empty;

    private CharacterAbilityLoadout? Fail(string error)
    {
        Error = error;
        return null;
    }

    private sealed record Table(string Name, string Entry, string Sha256,
        IReadOnlyDictionary<long, int> Indices, JsonArray Rows);
}
