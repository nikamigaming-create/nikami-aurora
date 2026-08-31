using System.Text.Json.Nodes;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.Abilities;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Catalogs;

public sealed class GdaAbilityCatalog(IJsonStore store) : IAbilityCatalog
{
    private static readonly IReadOnlyDictionary<string, long> RequiredColumns =
        new Dictionary<string, long>
        {
            ["id"] = 1727777078,
            ["label"] = 3030163814,
            ["nameStrRef"] = 846460685,
            ["descStrRef"] = 1353253814,
            ["icon"] = 3168647281,
            ["abilityType"] = 3785946346,
            ["guiType"] = 3727849837,
            ["cost"] = 4030559806,
            ["targetType"] = 258221097,
            ["range"] = 2180162436,
            ["useType"] = 2901394626,
            ["script"] = 103378247,
            ["cooldown"] = 401839034,
        };

    private readonly Dictionary<int, AbilityDefinition> records = [];
    public string SourcePath { get; private set; } = string.Empty;
    public string TableSha256 { get; private set; } = string.Empty;
    public string Error { get; private set; } = string.Empty;

    public bool Load(string path)
    {
        SourcePath = path;
        Error = string.Empty;
        records.Clear();
        var document = store.Read(path);
        if (document is null) return Fail("ability-catalog-open-failed");
        if (document["schema"]?.GetValue<string>() != "opendao-gda-catalog-v1")
            return Fail("ability-catalog-schema-invalid");
        if (document["tables"]?["abi_base"] is not JsonObject table ||
            table["columns"] is not JsonArray columns || table["rows"] is not JsonArray rows)
            return Fail("ability-catalog-table-absent");

        var indices = new Dictionary<long, int>();
        for (var i = 0; i < columns.Count; i++)
            if (columns[i]?["hash"] is JsonNode hash) indices[hash.GetValue<long>()] = i;
        foreach (var required in RequiredColumns)
            if (!indices.ContainsKey(required.Value)) return Fail("ability-catalog-column-absent:" + required.Key);

        TableSha256 = table["sha256"]?.GetValue<string>() ?? string.Empty;
        foreach (var node in rows.OfType<JsonArray>())
        {
            JsonNode? Cell(string field) => node[indices[RequiredColumns[field]]];
            var id = Cell("id")?.GetValue<int>() ?? 0;
            var provenance = new Dictionary<string, object?>
            {
                ["basis"] = "installed-abi_base-gda-row",
                ["table"] = "abi_base",
                ["entry"] = table["entry"]?.GetValue<string>() ?? string.Empty,
                ["sha256"] = TableSha256,
                ["id"] = id,
                ["columnHashes"] = RequiredColumns,
            };
            records[id] = new(id, Text(Cell("label")), Int(Cell("nameStrRef")), Int(Cell("descStrRef")),
                Text(Cell("icon")), Int(Cell("abilityType")), Int(Cell("guiType")), Float(Cell("cost")),
                Int(Cell("targetType")), Float(Cell("range")), Int(Cell("useType")), Text(Cell("script")),
                Float(Cell("cooldown")), provenance);
        }
        return true;
    }

    public AbilityDefinition? Find(int abilityId) => records.GetValueOrDefault(abilityId);
    private bool Fail(string error) { Error = error; return false; }
    private static string Text(JsonNode? node) => node?.ToString() ?? string.Empty;
    private static int Int(JsonNode? node) => node?.GetValue<int>() ?? 0;
    private static float Float(JsonNode? node) => node?.GetValue<float>() ?? 0;
}
