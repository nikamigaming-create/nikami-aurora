using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.Cli;

internal static class DaoCharacterMshAuditCommand
{
    private const string ModelMeshArchive = "packages/core/data/modelmeshdata.erf";
    private const string ModelHierarchyArchive = "packages/core/data/modelhierarchies.erf";
    private const string MorphArchive = "packages/core/data/face.erf";

    public static DaoCharacterMshAuditResult Run(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var dependencyAudit = DaoCharacterImportAuditCommand.Run(fullRoot);
        var meshes = ReadOnlyErf.Open(Contained(fullRoot, ModelMeshArchive));
        var hierarchies = ReadOnlyErf.Open(Contained(fullRoot, ModelHierarchyArchive));
        var morphs = ReadOnlyErf.Open(Contained(fullRoot, MorphArchive));
        var entries = new List<DaoCharacterMshAuditEntry>();
        var decodedMeshes = new Dictionary<string, DragonAgeMshDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var member in dependencyAudit.ResolvedMeshResources)
        {
            if (!meshes.TryRead(member, out var payload))
            {
                entries.Add(new DaoCharacterMshAuditEntry(
                    member, false, "source-msh-absent", string.Empty,
                    0, 0, 0, 0, 0, 0, []));
                continue;
            }
            try
            {
                var decoded = DragonAgeOriginsMshDecoder.Decode(member, payload);
                decodedMeshes.Add(member, decoded);
                var influences = decoded.Submeshes.SelectMany(value =>
                    value.SkinInfluences).ToArray();
                var maximumPaletteIndex = influences.SelectMany(value => new[]
                    {
                        value.PaletteIndex0, value.PaletteIndex1,
                        value.PaletteIndex2, value.PaletteIndex3
                    }).DefaultIfEmpty().Max();
                var maximumWeightError = influences.Select(value => Math.Abs(
                        value.SourceWeights.X + value.SourceWeights.Y +
                        value.SourceWeights.Z + value.SourceWeights.W - 1))
                    .DefaultIfEmpty().Max();
                var declarationSignatures = decoded.Submeshes.Select(value =>
                        string.Join(';', value.VertexDeclarations.Select(declaration =>
                            $"{declaration.Stream}:{declaration.Offset}:" +
                            $"{declaration.DataType}:{declaration.Usage}:" +
                            $"{declaration.UsageIndex}:{declaration.Method}")))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                entries.Add(new DaoCharacterMshAuditEntry(
                    member, true, string.Empty, decoded.PayloadSha256,
                    decoded.Submeshes.Count,
                    decoded.Submeshes.Sum(value => value.Positions.Count),
                    decoded.Submeshes.Sum(value => value.Indices.Count),
                    decoded.VertexBufferBytes,
                    decoded.IndexBufferBytes,
                    maximumPaletteIndex,
                    declarationSignatures,
                    maximumWeightError,
                    decoded.Submeshes.Sum(value =>
                        value.ReconstructedTangentVertices)));
            }
            catch (InvalidDataException error)
            {
                entries.Add(new DaoCharacterMshAuditEntry(
                    member, false, error.Message, string.Empty,
                    0, 0, 0, 0, 0, 0, []));
            }
        }

        var signatures = entries.SelectMany(value => value.DeclarationSignatures)
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key,
                StringComparer.Ordinal)
            .Select(group => new DaoCharacterMshDeclarationSignature(
                group.Key, group.Count())).ToArray();
        var morphTopology = AuditMorphTopology(
            morphs, hierarchies, decodedMeshes);
        return new DaoCharacterMshAuditResult(
            dependencyAudit.Selections,
            dependencyAudit.ResolvedMeshResources.Count,
            entries.Count(value => value.Decoded),
            entries.Count(value => !value.Decoded),
            entries.Sum(value => value.Submeshes),
            entries.Sum(value => value.Vertices),
            entries.Sum(value => value.Indices),
            dependencyAudit.ModelMeshArchiveSha256,
            DragonAgeMshCoordinateBasis.SourceRightHandedZUp,
            morphTopology.Modifiers,
            morphTopology.Exact,
            morphTopology.Incompatible,
            morphTopology.Reasons,
            signatures,
            entries);
    }

    private static MorphTopologyAudit AuditMorphTopology(
        ReadOnlyErf morphs,
        ReadOnlyErf hierarchies,
        IReadOnlyDictionary<string, DragonAgeMshDefinition> meshes)
    {
        var modifiers = 0;
        var exact = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var appearance in DragonAgeOriginsCharacterCreationCatalog.Appearances)
        {
            if (!morphs.TryRead(appearance.MorphResource, out var payload))
            {
                AddReason(reasons, "source-mop-absent");
                continue;
            }
            var morph = DragonAgeOriginsCharacterMorphDecoder.Decode(payload);
            var baseSubmeshes = morph.ModelParts.Where(value =>
                    !string.IsNullOrWhiteSpace(value))
                .SelectMany(value => ResolveMsh(value, hierarchies, meshes).Submeshes)
                .ToArray();
            foreach (var modifier in morph.Modifiers)
            {
                modifiers++;
                var target = ResolveMsh(modifier.Resource, hierarchies, meshes);
                var targetExact = true;
                foreach (var targetSubmesh in target.Submeshes)
                {
                    var candidates = baseSubmeshes.Where(candidate =>
                            ExactTopology(candidate, targetSubmesh))
                        .ToArray();
                    if (candidates.Length != 1)
                    {
                        targetExact = false;
                        AddReason(reasons, candidates.Length == 0
                            ? "no-exact-vertex-index-correspondence"
                            : "ambiguous-exact-vertex-index-correspondence");
                        break;
                    }
                    _ = DragonAgeOriginsMshDecoder.BuildMorphTarget(
                        candidates[0], target, targetSubmesh, modifier.Weight);
                }
                if (targetExact) exact++;
            }
        }
        return new MorphTopologyAudit(
            modifiers, exact, modifiers - exact,
            reasons.OrderByDescending(value => value.Value)
                .ThenBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => new DaoCharacterMshMorphTopologyReason(
                    value.Key, value.Value)).ToArray());
    }

    private static DragonAgeMshDefinition ResolveMsh(
        string hierarchyResource,
        ReadOnlyErf hierarchies,
        IReadOnlyDictionary<string, DragonAgeMshDefinition> meshes)
    {
        var hierarchyMember = NormalizeMember(hierarchyResource, ".mmh");
        if (!hierarchies.TryRead(hierarchyMember, out var hierarchyPayload))
            throw new InvalidDataException(
                $"DAO morph hierarchy dependency is absent: {hierarchyMember}");
        var hierarchy = DragonAgeOriginsCharacterModelHierarchyDecoder.Decode(
            hierarchyMember, hierarchyPayload);
        var meshMember = NormalizeMember(hierarchy.MeshResource, ".msh");
        if (!meshes.TryGetValue(meshMember, out var mesh))
            throw new InvalidDataException(
                $"DAO morph MSH dependency is not decoded: {meshMember}");
        return mesh;
    }

    private static bool ExactTopology(
        DragonAgeMshSubmesh first, DragonAgeMshSubmesh second) =>
        first.Positions.Count == second.Positions.Count &&
        first.Indices.Count == second.Indices.Count &&
        first.Indices.SequenceEqual(second.Indices);

    private static void AddReason(IDictionary<string, int> reasons, string reason) =>
        reasons[reason] = reasons.TryGetValue(reason, out var count) ? count + 1 : 1;

    private static string NormalizeMember(string value, string extension)
    {
        var member = Path.GetFileName(value.Trim()).ToLowerInvariant();
        if (member.Length == 0)
            throw new InvalidDataException("DAO MSH audit dependency is blank.");
        return member.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? member : member + extension;
    }

    private static string Contained(string root, string relative)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DAO MSH audit path escapes the installed root.");
        return result;
    }
}

internal sealed record DaoCharacterMshAuditResult(
    int Selections,
    int MeshDependencies,
    int MeshesDecoded,
    int MeshesFailed,
    int Submeshes,
    int Vertices,
    int Indices,
    string ModelMeshArchiveSha256,
    DragonAgeMshCoordinateBasis CoordinateBasis,
    int MorphModifierPlacements,
    int ExactTopologyMorphModifierPlacements,
    int IncompatibleMorphModifierPlacements,
    IReadOnlyList<DaoCharacterMshMorphTopologyReason> MorphTopologyReasons,
    IReadOnlyList<DaoCharacterMshDeclarationSignature> DeclarationSignatures,
    IReadOnlyList<DaoCharacterMshAuditEntry> Entries);

internal sealed record DaoCharacterMshMorphTopologyReason(
    string Reason,
    int Placements);

internal sealed record DaoCharacterMshDeclarationSignature(
    string Signature,
    int Meshes);

internal sealed record DaoCharacterMshAuditEntry(
    string Member,
    bool Decoded,
    string Reason,
    string PayloadSha256,
    int Submeshes,
    int Vertices,
    int Indices,
    int VertexBufferBytes,
    int IndexBufferBytes,
    int MaximumPaletteIndex,
    IReadOnlyList<string> DeclarationSignatures,
    float MaximumSourceWeightSumError = 0,
    int ReconstructedTangentVertices = 0);

internal sealed record MorphTopologyAudit(
    int Modifiers,
    int Exact,
    int Incompatible,
    IReadOnlyList<DaoCharacterMshMorphTopologyReason> Reasons);
