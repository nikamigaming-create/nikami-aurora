using System.Text.Json.Nodes;
using Godot;
using OpenDAO.Application.Abstractions;
using OpenDAO.Domain.World;
using OpenDAO.Domain.Story;
using OpenDAO.Rendering;

namespace OpenDAO.Infrastructure.World;

public sealed class GodotWorldContentLoader(
    IJsonStore store,
    IGodotModelCache modelCache,
    IStaticWorldBatchBuilder batchBuilder,
    IDaoTerrainMaterialFactory terrainMaterials,
    IDaoWaterMaterialFactory waterMaterials,
    IAuthoredNavigationGridSource navigationSource,
    IAuthoredWorldBlockerBuilder blockerBuilder,
    IAuthoredLightingResolver lightingResolver,
    StoryState story,
    IWorldLoadScheduler scheduler) : IWorldContentLoader
{
    public async Task<WorldLoadResult> LoadAsync(WorldProfile profile, Node3D destination,
        CancellationToken cancellationToken)
    {
        var staging = new Node3D
        {
            Name = "WorldStaging",
            Visible = false,
            ProcessMode = Node.ProcessModeEnum.Disabled
        };
        destination.AddChild(staging);
        var cacheHits = modelCache.Hits;
        var cacheMisses = modelCache.Misses;
        var authoredBlockers = 0;
        AuthoredNavigationGrid? navigation = null;
        try
        {
            var metrics = new LoadMetrics();
            var area = store.Read(profile.AreaFile);
            var actorDocument = profile.ActorFile.Length > 0 ? store.Read(profile.ActorFile) : area;
            var actorRoot = profile.ActorRoot.Length > 0 ? profile.ActorRoot : profile.AreaRoot;
            await modelCache.WarmAsync(EnumerateModelPaths(profile, area, actorDocument, actorRoot),
                destination, cancellationToken);
            scheduler.Reset();
            if (profile.SceneFile.Length > 0)
                metrics += await LoadComposedScene(profile.SceneFile, staging, cancellationToken);
            else if (area is not null)
            {
                metrics += await LoadDefinitionTable(area["terrain"]?["patches"] as JsonObject,
                    profile.AreaRoot, staging, static (_, _) => WorldCollisionPolicy.Terrain,
                    (name, mesh) => terrainMaterials.Create(profile, name, mesh),
                    cancellationToken);
                metrics += await LoadDefinitionTable(area["props"] as JsonObject,
                    profile.AreaRoot, staging, WorldCollisionPolicy.ForProp,
                    (name, mesh) => waterMaterials.Create(profile, name, mesh),
                    cancellationToken);
                metrics += await LoadDefinitionTable(area["trees"] as JsonObject,
                    profile.AreaRoot, staging, static (_, _) => WorldCollisionPolicy.VisualOnly,
                    null,
                    cancellationToken);
            }

            var actors = actorDocument?["actors"] is JsonArray actorRecords
                ? await LoadActors(actorRecords, actorRoot, staging, cancellationToken)
                : 0;
            var authoredLights = area?["lights"] is JsonArray lightRecords
                ? LoadLights(lightRecords, staging)
                : 0;

            if (area?["placeables"] is JsonArray placeables)
            {
                metrics += await LoadPlaceableVisuals(placeables, profile.AreaRoot, staging,
                    cancellationToken);
                authoredBlockers = blockerBuilder.Build(ReadAuthoredBlockers(placeables), staging);
                metrics += new LoadMetrics(0, 0, authoredBlockers);
            }

            navigation = navigationSource.Load(profile);
            var lighting = lightingResolver.Resolve(profile,
                ReadLightingProfile(area?["environment"] as JsonObject,
                    area?["lights"] as JsonArray));
            if (navigation is not null)
            {
                GD.Print($"OPENDAO_AUTHORED_NAVIGATION status=pass source={navigation.SourcePath} " +
                         $"columns={navigation.Columns} rows={navigation.Rows} " +
                         $"walkable={navigation.Accessibility.Count(value => value == 1)}");
            }
            else GD.Print($"OPENDAO_AUTHORED_NAVIGATION status=unavailable layout={profile.LayoutName}");

            staging.ProcessMode = Node.ProcessModeEnum.Inherit;
            staging.Visible = true;
            await scheduler.YieldIfNeededAsync(destination, cancellationToken);
            return WorldLoadResult.Complete(metrics.Instances, actors, metrics.DrawNodes,
                metrics.CollisionShapes, authoredBlockers, authoredLights, lighting, navigation,
                modelCache.Hits - cacheHits,
                modelCache.Misses - cacheMisses, scheduler.YieldCount,
                scheduler.MaxWorkSliceMilliseconds);
        }
        catch (OperationCanceledException)
        {
            staging.QueueFree();
            return WorldLoadResult.Failed("world-load-cancelled");
        }
        catch (Exception error)
        {
            staging.QueueFree();
            GD.PushError($"OpenDAO world load failed: {error}");
            return WorldLoadResult.Failed(error.Message);
        }
    }

    private async Task<LoadMetrics> LoadComposedScene(string path, Node3D destination,
        CancellationToken cancellationToken)
    {
        var scene = modelCache.Instantiate(path);
        if (scene is null) throw new InvalidOperationException("composed-scene-load-failed");
        destination.AddChild(scene);
        var meshes = scene.FindChildren("*", "MeshInstance3D", true, false)
            .OfType<MeshInstance3D>().ToArray();
        foreach (var mesh in meshes)
        {
            mesh.Layers = WorldCollisionPolicy.IsMinimapVisible(mesh.Name)
                ? WorldRenderLayers.GameplayAndMinimap
                : WorldRenderLayers.Gameplay;
        }
        var drawNodes = meshes.Length;
        await scheduler.YieldIfNeededAsync(destination, cancellationToken, true);
        return new LoadMetrics(drawNodes, drawNodes, 0);
    }

    private async Task<LoadMetrics> LoadDefinitionTable(JsonObject? table, string root,
        Node3D destination, Func<string, string, WorldDefinitionPolicy> policyFactory,
        Func<string, Mesh, Material?>? materialFactory,
        CancellationToken cancellationToken)
    {
        if (table is null) return default;
        var metrics = new LoadMetrics();
        foreach (var (definitionName, value) in table)
        {
            if (value is not JsonObject definition) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var relative = definition["file"]?.GetValue<string>() ?? string.Empty;
            if (relative.Length == 0 || IsEffect(relative) ||
                definition["instances"] is not JsonArray instanceRecords) continue;
            var path = Path.IsPathRooted(relative)
                ? relative
                : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var packed = modelCache.Load(path);
            if (packed is null) continue;
            var transforms = instanceRecords.OfType<JsonObject>().Select(ReadTransform).ToArray();
            var policy = policyFactory(definitionName, relative);
            Func<Mesh, Material?>? definitionMaterialFactory = materialFactory is null
                ? null
                : mesh => materialFactory(definitionName, mesh);
            var result = await batchBuilder.BuildAsync(packed, definitionName, transforms,
                destination, policy.Render, policy.RenderLayers, policy.Collision,
                policy.CollisionLayer, definitionMaterialFactory,
                cancellationToken);
            metrics += new LoadMetrics(result.Instances, result.DrawNodes, result.CollisionShapes);
            await scheduler.YieldIfNeededAsync(destination, cancellationToken);
        }

        return metrics;
    }

    private async Task<int> LoadActors(JsonArray actors, string root, Node3D destination,
        CancellationToken cancellationToken)
    {
        var loaded = 0;
        foreach (var actor in actors.OfType<JsonObject>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(actor["active"]?.GetValue<bool>() ?? true)) continue;
            var relative = actor["file"]?.GetValue<string>() ??
                           actor["model"]?.GetValue<string>() ?? string.Empty;
            if (relative.Length == 0) continue;
            var path = Path.IsPathRooted(relative)
                ? relative
                : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var node = modelCache.Instantiate(path);
            if (node is null) continue;
            var resref = actor["tag"]?.GetValue<string>() ??
                         actor["resref"]?.GetValue<string>() ??
                         actor["template"]?.GetValue<string>() ?? string.Empty;
            node.Name = SanitizeNodeName(resref.Length > 0 ? resref : $"Actor{loaded}");
            node.Transform = ReadTransform(actor);
            node.SetMeta("dao_actor", true);
            node.SetMeta("dao_resref", resref);
            destination.AddChild(node);
            PlayDefaultAnimation(node);
            loaded++;
            await scheduler.YieldIfNeededAsync(destination, cancellationToken);
        }

        return loaded;
    }

    private static int LoadLights(JsonArray lights, Node3D destination)
    {
        var loaded = 0;
        foreach (var record in lights.OfType<JsonObject>())
        {
            if (ReadAffectDomain(record) == 1) continue;
            var sourceColor = record["color"] as JsonArray;
            if (sourceColor is not { Count: >= 3 }) continue;
            var red = Number(sourceColor[0]);
            var green = Number(sourceColor[1]);
            var blue = Number(sourceColor[2]);
            var encoded = DaoLightEncoding.Encode(red, green, blue);
            var name = record["name"]?.GetValue<string>() ?? $"AuthoredLight{loaded}";
            var variation = Number(record["intensity_variation"]);
            var light = new AuthoredPointLight
            {
                Name = SanitizeNodeName(name),
                Position = ConvertPosition(ReadVector(record["position"] as JsonArray)),
                OmniRange = Math.Max(0.1f, Number(record["radius"])),
                LightColor = encoded.Color,
                LightEnergy = encoded.Energy,
                BaseEnergy = encoded.Energy,
                Variation = variation,
                Period = Number(record["intensity_period"]),
                PeriodDelta = Number(record["intensity_period_delta"]),
                Phase = loaded * 0.713f,
                ShadowEnabled = name.Contains("Fire", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Candle", StringComparison.OrdinalIgnoreCase)
            };
            destination.AddChild(light);
            loaded++;
        }
        return loaded;
    }

    private async Task<LoadMetrics> LoadPlaceableVisuals(JsonArray placeables, string root,
        Node3D destination, CancellationToken cancellationToken)
    {
        var metrics = new LoadMetrics();
        var attempted = 0;
        var failed = 0;
        foreach (var record in placeables.OfType<JsonObject>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(record["active"]?.GetValue<bool>() ?? true) ||
                record["visual"] is not JsonObject visual ||
                !string.Equals(visual["status"]?.GetValue<string>(), "shared-model",
                    StringComparison.OrdinalIgnoreCase)) continue;
            attempted++;
            var relative = visual["file"]?.GetValue<string>() ?? string.Empty;
            if (relative.Length == 0)
            {
                failed++;
                continue;
            }
            var path = ResolvePath(root, relative);
            var node = modelCache.Instantiate(path);
            if (node is null)
            {
                failed++;
                continue;
            }
            var template = record["template"]?.GetValue<string>() ?? string.Empty;
            var tag = record["tag"]?.GetValue<string>() ?? template;
            node.Name = "Placeable_" + SanitizeNodeName(tag.Length > 0 ? tag : $"Object{attempted}");
            node.Transform = ReadTransform(record);
            node.SetMeta("dao_placeable", true);
            node.SetMeta("dao_interactive", true);
            node.SetMeta("dao_resref", template);
            node.SetMeta("dao_tag", tag);
            node.SetMeta("dao_placeable_model", visual["model"]?.GetValue<string>() ?? string.Empty);
            var sourcePosition = ReadVector(record["position"] as JsonArray);
            var storyObject = story.Create(template, tag, StoryObjectKind.Placeable,
                new StoryPosition(sourcePosition.X, sourcePosition.Y, sourcePosition.Z),
                new Dictionary<string, object?>
                {
                    ["visualModel"] = visual["model"]?.GetValue<string>() ?? string.Empty,
                    ["source"] = "installed-placeable-types-gda"
                });
            node.SetMeta("dao_story_handle", storyObject.Handle);
            destination.AddChild(node);
            var meshes = node.FindChildren("*", "MeshInstance3D", true, false)
                .OfType<MeshInstance3D>().ToArray();
            foreach (var mesh in meshes)
            {
                mesh.Layers = WorldRenderLayers.Gameplay;
                mesh.CreateTrimeshCollision();
            }
            var collisionShapes = 0;
            foreach (var body in node.FindChildren("*", "StaticBody3D", true, false)
                         .OfType<StaticBody3D>())
            {
                body.CollisionLayer = 2;
                body.CollisionMask = 0;
                body.SetMeta("dao_placeable", true);
                body.SetMeta("dao_tag", tag);
                collisionShapes += body.FindChildren("*", "CollisionShape3D", true, false).Count;
            }
            PlayDefaultAnimation(node);
            metrics += new LoadMetrics(1, meshes.Length, collisionShapes);
            GD.Print($"OPENDAO_PLACEABLE_VISUAL status=ready tag={tag} model=" +
                     $"{visual["model"]?.GetValue<string>() ?? string.Empty} meshes={meshes.Length} " +
                     $"collision_shapes={collisionShapes} source=installed-placeable-types-gda");
            await scheduler.YieldIfNeededAsync(destination, cancellationToken);
        }
        GD.Print($"OPENDAO_PLACEABLE_VISUALS status={(failed == 0 ? "ready" : "partial")} " +
                 $"loaded={attempted - failed} attempted={attempted} failed={failed} exported={placeables.Count}");
        return metrics;
    }

    private static AuthoredLightingProfile? ReadLightingProfile(JsonObject? environment,
        JsonArray? lights)
    {
        if (environment is null) return null;
        return new AuthoredLightingProfile(
            environment["probe_loaded"]?.GetValue<bool>() ?? false,
            ReadFloatArray(environment["probe_matrix_r"] as JsonArray, 16),
            ReadFloatArray(environment["probe_matrix_g"] as JsonArray, 16),
            ReadFloatArray(environment["probe_matrix_b"] as JsonArray, 16),
            ReadFloatArray(environment["sun_color"] as JsonArray, 3),
            ReadFloatArray(environment["character_sun_color"] as JsonArray, 4),
            ReadFloatArray(environment["sun_direction"] as JsonArray, 3),
            ReadFloatArray(environment["fog_color"] as JsonArray, 3),
            Number(environment["sun_intensity"]),
            environment["probe_matrix_resource"]?.GetValue<string>() ?? string.Empty,
            ReadPointLightProfiles(lights, 2),
            ReadPointLightProfiles(lights, 1));
    }

    private static AuthoredPointLightProfile[] ReadPointLightProfiles(JsonArray? lights, int affectDomain)
    {
        if (lights is null) return [];
        return lights.OfType<JsonObject>()
            .Where(record => ReadAffectDomain(record) == affectDomain)
            .Select(record =>
        {
            var position = ReadVector(record["position"] as JsonArray);
            var color = ReadVector(record["color"] as JsonArray);
            return new AuthoredPointLightProfile(
                record["name"]?.GetValue<string>() ?? string.Empty,
                position.X, position.Y, position.Z,
                color.X, color.Y, color.Z,
                Math.Max(0.0001f, Number(record["radius"])));
        }).ToArray();
    }

    private static int ReadAffectDomain(JsonObject record) =>
        record["affect_domain"]?.GetValue<int>() ?? 2;

    private static float[] ReadFloatArray(JsonArray? values, int expected)
    {
        var result = new float[expected];
        if (values is null) return result;
        for (var index = 0; index < Math.Min(expected, values.Count); index++)
            result[index] = Number(values[index]);
        return result;
    }

    private static Transform3D ReadTransform(JsonObject record)
    {
        var position = ReadVector(record["position"] as JsonArray);
        var values = record["rotation"] as JsonArray;
        var rotation = values is { Count: >= 4 }
            ? new Quaternion(Number(values[0]), Number(values[1]), Number(values[2]), Number(values[3]))
            : Quaternion.Identity;
        var conversion = new Basis(Vector3.Right, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        var basis = conversion * new Basis(rotation) * conversion.Inverse();
        var scale = record["scale"]?.GetValue<float>() ?? 1.0f;
        return new Transform3D(basis.Scaled(Vector3.One * scale),
            new Vector3(position.X, position.Z, -position.Y));
    }

    private static Vector3 ReadVector(JsonArray? value) => value is { Count: >= 3 }
        ? new Vector3(Number(value[0]), Number(value[1]), Number(value[2]))
        : Vector3.Zero;

    private static Vector3 ConvertPosition(Vector3 source) =>
        new(source.X, source.Z, -source.Y);

    private static float Number(JsonNode? value) => value?.GetValue<float>() ?? 0;

    private static IEnumerable<string> EnumerateModelPaths(WorldProfile profile, JsonNode? area,
        JsonNode? actorDocument, string actorRoot)
    {
        if (profile.SceneFile.Length > 0) yield return profile.SceneFile;
        if (area is not null)
        {
            foreach (var path in EnumerateTablePaths(area["terrain"]?["patches"] as JsonObject,
                         profile.AreaRoot)) yield return path;
            foreach (var path in EnumerateTablePaths(area["props"] as JsonObject, profile.AreaRoot))
                yield return path;
            foreach (var path in EnumerateTablePaths(area["trees"] as JsonObject, profile.AreaRoot))
                yield return path;
            if (area["placeables"] is JsonArray placeables)
                foreach (var record in placeables.OfType<JsonObject>())
                {
                    if (!(record["active"]?.GetValue<bool>() ?? true) ||
                        record["visual"] is not JsonObject visual ||
                        !string.Equals(visual["status"]?.GetValue<string>(), "shared-model",
                            StringComparison.OrdinalIgnoreCase)) continue;
                    var relative = visual["file"]?.GetValue<string>() ?? string.Empty;
                    if (relative.Length > 0) yield return ResolvePath(profile.AreaRoot, relative);
                }
        }

        if (actorDocument?["actors"] is not JsonArray actors) yield break;
        foreach (var actor in actors.OfType<JsonObject>())
        {
            if (!(actor["active"]?.GetValue<bool>() ?? true)) continue;
            var relative = actor["file"]?.GetValue<string>() ??
                           actor["model"]?.GetValue<string>() ?? string.Empty;
            if (relative.Length > 0) yield return ResolvePath(actorRoot, relative);
        }
    }

    private static IEnumerable<string> EnumerateTablePaths(JsonObject? table, string root)
    {
        if (table is null) yield break;
        foreach (var definition in table.Select(entry => entry.Value).OfType<JsonObject>())
        {
            var relative = definition["file"]?.GetValue<string>() ?? string.Empty;
            if (relative.Length > 0 && !IsEffect(relative)) yield return ResolvePath(root, relative);
        }
    }

    private static string ResolvePath(string root, string relative) => Path.IsPathRooted(relative)
        ? relative
        : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static IReadOnlyList<WorldBlockerPlacement> ReadAuthoredBlockers(JsonArray placeables)
    {
        var blockers = new List<WorldBlockerPlacement>();
        foreach (var record in placeables.OfType<JsonObject>())
        {
            if (!(record["active"]?.GetValue<bool>() ?? true)) continue;
            var template = record["template"]?.GetValue<string>() ?? string.Empty;
            var tag = record["tag"]?.GetValue<string>() ?? template;
            var semantic = $"{template} {tag}".ToLowerInvariant();
            var kind = semantic.Contains("invisible_wide", StringComparison.Ordinal)
                ? WorldBlockerKind.InvisibleWide
                : semantic.Contains("door", StringComparison.Ordinal)
                    ? WorldBlockerKind.Door
                    : semantic.Contains("blocker", StringComparison.Ordinal)
                        ? WorldBlockerKind.InvisibleStandard
                        : (WorldBlockerKind?)null;
            if (kind is null) continue;

            var position = ReadVector(record["position"] as JsonArray);
            var values = record["rotation"] as JsonArray;
            var rotation = values is { Count: >= 4 }
                ? new System.Numerics.Quaternion(Number(values[0]), Number(values[1]),
                    Number(values[2]), Number(values[3]))
                : System.Numerics.Quaternion.Identity;
            blockers.Add(new WorldBlockerPlacement(template, tag,
                new System.Numerics.Vector3(position.X, position.Y, position.Z), rotation, kind.Value));
        }

        return blockers;
    }

    private static bool IsEffect(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name.StartsWith("fxe_", StringComparison.Ordinal) ||
               name.StartsWith("fxp_", StringComparison.Ordinal) ||
               name.StartsWith("fxm_", StringComparison.Ordinal) ||
               name.StartsWith("fxa_", StringComparison.Ordinal) ||
               name.StartsWith("fxc_", StringComparison.Ordinal);
    }

    private static string SanitizeNodeName(string value) => value.Replace('/', '_').Replace(':', '_');

    private static void PlayDefaultAnimation(Node root)
    {
        foreach (var player in root.FindChildren("*", "AnimationPlayer", true, false)
                     .OfType<AnimationPlayer>())
            foreach (var name in player.GetAnimationList())
            {
                if (name == "RESET") continue;
                var animation = player.GetAnimation(name);
                if (animation is not null) animation.LoopMode = Animation.LoopModeEnum.Linear;
                player.Play(name);
                return;
            }
    }

    private readonly record struct LoadMetrics(int Instances, int DrawNodes, int CollisionShapes)
    {
        public static LoadMetrics operator +(LoadMetrics left, LoadMetrics right) =>
            new(left.Instances + right.Instances, left.DrawNodes + right.DrawNodes,
                left.CollisionShapes + right.CollisionShapes);
    }
}
