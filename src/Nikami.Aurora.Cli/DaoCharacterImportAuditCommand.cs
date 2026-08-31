using System.Security.Cryptography;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.Cli;

internal static class DaoCharacterImportAuditCommand
{
    private const string CatalogArchive = "packages/core/data/misc.erf";
    private const string MorphArchive = "packages/core/data/face.erf";
    private const string ModelHierarchyArchive = "packages/core/data/modelhierarchies.erf";
    private const string ModelMeshArchive = "packages/core/data/modelmeshdata.erf";

    public static DaoCharacterImportAuditResult Run(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var catalogPath = Contained(fullRoot, CatalogArchive);
        var morphPath = Contained(fullRoot, MorphArchive);
        var hierarchyPath = Contained(fullRoot, ModelHierarchyArchive);
        var meshPath = Contained(fullRoot, ModelMeshArchive);
        RequireFileHash(catalogPath,
            DragonAgeOriginsCharacterCreationCatalog.CatalogContainerSha256,
            "character catalog archive");
        RequireFileHash(morphPath,
            DragonAgeOriginsCharacterCreationCatalog.SourceContainerSha256,
            "character morph archive");

        var catalogs = ReadOnlyErf.Open(catalogPath);
        var morphs = ReadOnlyErf.Open(morphPath);
        var hierarchies = ReadOnlyErf.Open(hierarchyPath);
        var meshes = ReadOnlyErf.Open(meshPath);
        if (!catalogs.TryRead(DragonAgeOriginsCharacterCreationCatalog.CatalogResource,
                out var rawCatalog) ||
            Hex(rawCatalog) !=
            DragonAgeOriginsCharacterCreationCatalog.CatalogResourceSha256)
            throw new InvalidDataException(
                "Installed DAO character-creation catalog payload identity disagrees.");

        var entries = new List<DaoCharacterImportAuditEntry>();
        foreach (var appearance in DragonAgeOriginsCharacterCreationCatalog.Appearances)
        {
            if (!morphs.TryRead(appearance.MorphResource, out var payload))
            {
                entries.Add(new DaoCharacterImportAuditEntry(
                    appearance.SelectionKey, appearance.MorphResource, false,
                    "source-mop-absent", 0, 0, 0, 0, 0, [], [],
                    0, 0, 0, 0, 0, [], [], [], [], []));
                continue;
            }
            if (Hex(payload) != appearance.MorphSha256)
            {
                entries.Add(new DaoCharacterImportAuditEntry(
                    appearance.SelectionKey, appearance.MorphResource, false,
                    "source-mop-hash-mismatch", 0, 0, 0, 0, 0, [], [],
                    0, 0, 0, 0, 0, [], [], [], [], []));
                continue;
            }

            var morph = DragonAgeOriginsCharacterMorphDecoder.Decode(payload);
            if (morph.ResRef + ".mop" != appearance.MorphResource)
                throw new InvalidDataException(
                    $"Decoded MOP identity disagrees with selection {appearance.SelectionKey}.");
            var partMembers = morph.ModelParts
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => NormalizeMember(value, ".mmh"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var modifierMembers = morph.Modifiers
                .Select(value => NormalizeMember(value.Resource, ".mmh"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var missingParts = partMembers.Where(value => !hierarchies.Contains(value)).ToArray();
            var missingModifiers = modifierMembers.Where(value => !hierarchies.Contains(value)).ToArray();
            var decodedHierarchies = new List<DragonAgeCharacterModelHierarchy>();
            var hierarchyFailures = new List<string>();
            foreach (var member in partMembers.Concat(modifierMembers)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!hierarchies.TryRead(member, out var hierarchyPayload)) continue;
                try
                {
                    decodedHierarchies.Add(
                        DragonAgeOriginsCharacterModelHierarchyDecoder.Decode(
                            member, hierarchyPayload));
                }
                catch (InvalidDataException error)
                {
                    hierarchyFailures.Add(member + ":" + error.Message);
                }
            }
            var unsupportedKinds = decodedHierarchies
                .SelectMany(value => value.UninterpretedStructureKinds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var unsupportedSchemas = decodedHierarchies
                .SelectMany(value => value.StructureSchemas.Where(schema =>
                    value.UninterpretedStructureKinds.Contains(schema.Kind,
                        StringComparer.Ordinal)))
                .Select(schema => schema.Kind + ":" + string.Join(',', schema.FieldLabels))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var resolvedMeshes = decodedHierarchies
                .Select(value => NormalizeMember(value.MeshResource, ".msh"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var missingMeshes = decodedHierarchies
                .Select(value => NormalizeMember(value.MeshResource, ".msh"))
                .Where(value => !meshes.Contains(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var reason = missingParts.Length > 0 || missingModifiers.Length > 0
                ? "source-model-dependency-absent"
                : hierarchyFailures.Count > 0
                    ? "render-mmh-skeleton-graph-malformed"
                    : unsupportedKinds.Length > 0
                        ? "render-mmh-structure-uninterpreted"
                        : missingMeshes.Length > 0
                            ? "source-mesh-dependency-absent"
                            : "source-morph-crust-correspondence-contract-unavailable";
            entries.Add(new DaoCharacterImportAuditEntry(
                appearance.SelectionKey,
                appearance.MorphResource,
                true,
                reason,
                partMembers.Length,
                morph.Modifiers.Count,
                morph.ScalarOverrides.Count,
                morph.VectorOverrides.Count,
                morph.TextureOverrides.Count,
                missingParts,
                missingModifiers,
                decodedHierarchies.Count,
                decodedHierarchies.Sum(value => value.Nodes.Count),
                decodedHierarchies.Sum(value => value.MeshBindings.Count),
                decodedHierarchies.Sum(value => value.ControllerExports.Count),
                decodedHierarchies.Sum(value => value.MeshBindings.Sum(
                    binding => binding.BoneIndices.Count)),
                resolvedMeshes,
                missingMeshes,
                unsupportedKinds,
                unsupportedSchemas,
                hierarchyFailures));
        }

        var allMmhDecoded = entries.All(value =>
            value.MorphDecoded && value.MissingModelParts.Count == 0 &&
            value.MissingModifierTargets.Count == 0 &&
            value.HierarchyFailures.Count == 0 &&
            value.UninterpretedHierarchyKinds.Count == 0 &&
            value.MissingMeshResources.Count == 0);
        var blockers = new List<string>();
        if (!allMmhDecoded)
            blockers.Add("render-mmh-skeleton-graph-contract-incomplete");
        blockers.AddRange([
            "source-morph-crust-correspondence-contract-unavailable",
            "mao-mat-tint-texture-binding-incomplete",
            "source-outfit-body-selection-contract-unavailable",
            "source-standing-bed-pose-contract-unavailable"
        ]);
        return new DaoCharacterImportAuditResult(
            entries.Count,
            entries.Count(value => value.MorphDecoded),
            entries.Count(value => value.MorphDecoded &&
                                   value.MissingModelParts.Count == 0 &&
                                   value.MissingModifierTargets.Count == 0),
            entries.Sum(value => value.ModelHierarchiesDecoded),
            entries.Sum(value => value.SkeletonNodes),
            entries.Sum(value => value.MeshBindings),
            entries.Sum(value => value.ControllerExports),
            entries.Sum(value => value.BoneIndexReferences),
            entries.SelectMany(value => value.ResolvedMeshResources)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            allMmhDecoded,
            FreshImportReady: 0,
            LegacyEvidenceReady:
                DragonAgeOriginsCharacterCreationCatalog.Appearances.Count(value =>
                    value.HasLegacyEvidence),
            ModelHierarchyArchiveSha256: HashFile(hierarchyPath),
            ModelMeshArchiveSha256: HashFile(meshPath),
            Blockers: blockers,
            Entries: entries);
    }

    private static string NormalizeMember(string value, string extension)
    {
        var member = Path.GetFileName(value.Trim()).ToLowerInvariant();
        if (member.Length == 0)
            throw new InvalidDataException("DAO character model dependency is blank.");
        return member.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? member
            : member + extension;
    }

    private static string Contained(string root, string relative)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DAO character import path escapes the installed root.");
        return result;
    }

    private static void RequireFileHash(string path, string expected, string label)
    {
        var actual = HashFile(path);
        if (actual != expected)
            throw new InvalidDataException(
                $"Installed DAO {label} SHA-256 disagrees: expected={expected} actual={actual}");
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed record DaoCharacterImportAuditResult(
    int Selections,
    int MorphsDecoded,
    int SourceDependenciesPresent,
    int ModelHierarchiesDecoded,
    int SkeletonNodes,
    int MeshBindings,
    int ControllerExports,
    int BoneIndexReferences,
    IReadOnlyList<string> ResolvedMeshResources,
    bool ModelHierarchyContractReady,
    int FreshImportReady,
    int LegacyEvidenceReady,
    string ModelHierarchyArchiveSha256,
    string ModelMeshArchiveSha256,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<DaoCharacterImportAuditEntry> Entries);

internal sealed record DaoCharacterImportAuditEntry(
    string SelectionKey,
    string MorphResource,
    bool MorphDecoded,
    string Reason,
    int ModelParts,
    int MorphModifiers,
    int ScalarOverrides,
    int VectorOverrides,
    int TextureOverrides,
    IReadOnlyList<string> MissingModelParts,
    IReadOnlyList<string> MissingModifierTargets,
    int ModelHierarchiesDecoded,
    int SkeletonNodes,
    int MeshBindings,
    int ControllerExports,
    int BoneIndexReferences,
    IReadOnlyList<string> ResolvedMeshResources,
    IReadOnlyList<string> MissingMeshResources,
    IReadOnlyList<string> UninterpretedHierarchyKinds,
    IReadOnlyList<string> UninterpretedHierarchySchemas,
    IReadOnlyList<string> HierarchyFailures);
