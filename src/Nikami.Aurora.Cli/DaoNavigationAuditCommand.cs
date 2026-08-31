using System.Security.Cryptography;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.Cli;

internal static class DaoNavigationAuditCommand
{
    public static DaoNavigationAuditResult Run(string root, IEnumerable<string> layouts)
    {
        var installedRoot = Path.GetFullPath(root);
        var results = new List<DaoNavigationAuditEntry>();
        foreach (var layout in layouts.Select(value => value.Trim().ToLowerInvariant())
                     .Where(value => value.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.Ordinal))
        {
            if (!IsResRef(layout))
                throw new InvalidDataException($"Invalid DAO layout identity: {layout}");
            var relativePath = Path.Combine("packages", "core", "env", layout + ".arl");
            var path = Path.GetFullPath(Path.Combine(installedRoot, relativePath));
            if (!path.StartsWith(installedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"DAO navigation path escaped its root: {layout}");
            if (!File.Exists(path))
            {
                results.Add(new DaoNavigationAuditEntry(layout, "absent",
                    "source-core-arl-absent", relativePath, "", null));
                continue;
            }

            var payload = File.ReadAllBytes(path);
            try
            {
                var contract = DragonAgeOriginsNavigationGridDecoder.Decode(payload);
                results.Add(new DaoNavigationAuditEntry(layout,
                    contract.Ready ? "ready" : "unsupported",
                    contract.Ready ? "exact-grid-with-walkable-cells" : "walkable-cells-absent",
                    relativePath, Hex(SHA256.HashData(payload)), contract));
            }
            catch (InvalidDataException exception)
            {
                results.Add(new DaoNavigationAuditEntry(layout, "unsupported",
                    "source-arl-contract-invalid: " + exception.Message,
                    relativePath, Hex(SHA256.HashData(payload)), null));
            }
        }

        return new DaoNavigationAuditResult(results.Count,
            results.Count(result => result.Status == "ready"),
            results.Count(result => result.Status == "absent"),
            results.Count(result => result.Status == "unsupported"), results);
    }

    private static bool IsResRef(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}

internal sealed record DaoNavigationAuditResult(
    int Layouts,
    int Ready,
    int Absent,
    int Unsupported,
    IReadOnlyList<DaoNavigationAuditEntry> Results);

internal sealed record DaoNavigationAuditEntry(
    string Layout,
    string Status,
    string Reason,
    string SourceRelativePath,
    string PayloadSha256,
    DragonAgeNavigationGridContract? Contract);
