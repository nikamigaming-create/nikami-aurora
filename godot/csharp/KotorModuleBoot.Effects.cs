using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Nikami.Aurora.Core;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;
using Nikami.Aurora.Profiles.Kotor;
using NumericsVector3 = System.Numerics.Vector3;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class KotorModuleBoot
{
    private static FirstEncounterAudioStreams LoadFirstEncounterAudio(
        FirstEncounterAudio source,
        string manifestDirectory) => new(
        LoadOwnedAudio(source.BlasterShot, manifestDirectory),
        LoadOwnedAudio(source.BlasterImpact, manifestDirectory),
        LoadOwnedAudio(source.BackgroundMusic, manifestDirectory),
        LoadOwnedAudio(source.BattleMusic, manifestDirectory));

    private static IReadOnlyDictionary<string, Cubemap> LoadOwnedEnvironmentMaps(
        IReadOnlyList<EnvironmentMapRecord>? sources,
        string manifestDirectory)
    {
        if (sources is null)
            throw new InvalidDataException("KOTOR environment-map inventory is missing");
        var textures = new Dictionary<string, Cubemap>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(manifestDirectory) + Path.DirectorySeparatorChar;
        foreach (var source in sources)
        {
            if (source.Schema != KotorEnvironmentMaterialPolicy.Schema ||
                string.IsNullOrWhiteSpace(source.Resref) || source.Resref.Length > 16 ||
                source.FaceOrder is null ||
                !source.FaceOrder.SequenceEqual(
                    KotorEnvironmentMaterialPolicy.FaceOrder,
                    StringComparer.Ordinal) ||
                source.SampleBasis != KotorEnvironmentMaterialPolicy.SampleBasis ||
                source.Faces is null || source.Faces.Count != 6 ||
                textures.ContainsKey(source.Resref))
                throw new InvalidDataException(
                    $"Environment-map contract drifted: {source.Resref}");

            var images = new Godot.Collections.Array<Godot.Image>();
            for (var layer = 0; layer < 6; layer++)
            {
                var face = source.Faces.SingleOrDefault(candidate => candidate.Layer == layer)
                    ?? throw new InvalidDataException(
                        $"Environment-map layer is missing: {source.Resref}/{layer}");
                if (face.Face != KotorEnvironmentMaterialPolicy.FaceOrder[layer] ||
                    face.RowTransform != KotorEnvironmentMaterialPolicy.RowTransform ||
                    face.Width <= 0 || face.Height <= 0 || face.Width != face.Height)
                    throw new InvalidDataException(
                        $"Environment-map face orientation drifted: {source.Resref}/{layer}");
                var path = Path.GetFullPath(Path.Combine(manifestDirectory,
                    face.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Environment-map face escapes the bundle: {face.Path}");
                var bytes = File.ReadAllBytes(path);
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(bytes));
                if (bytes.Length != face.ByteCount ||
                    !hash.Equals(face.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Environment-map payload drifted: {source.Resref}/{layer}");
                var image = new Godot.Image();
                if (image.LoadPngFromBuffer(bytes) != Error.Ok || image.IsEmpty() ||
                    image.GetWidth() != face.Width || image.GetHeight() != face.Height ||
                    image.GenerateMipmaps() != Error.Ok)
                    throw new InvalidDataException(
                        $"Environment-map face is not playable: {source.Resref}/{layer}");
                images.Add(image);
            }
            var cubemap = new Cubemap { ResourceName = source.Resref };
            if (cubemap.CreateFromImages(images) != Error.Ok)
                throw new InvalidDataException(
                    $"Godot could not create environment map: {source.Resref}");
            textures.Add(source.Resref, cubemap);
            GD.Print($"NIKAMI_AURORA_ENVIRONMENT_MAP status=validated " +
                     $"resref={source.Resref} faces=6 size={source.Faces[0].Width} " +
                     $"sourceSha256={source.SourceSha256}");
        }
        return textures;
    }

    private static FirstEncounterEffectTextures LoadFirstEncounterEffects(
        FirstEncounterEffects source,
        string manifestDirectory)
    {
        if (source.Schema != "nikami-aurora-kotor-first-encounter-effects-v2" ||
            Math.Abs(source.ProjectileSize - 0.09f) > 0.0001f ||
            !source.ProjectileUpdate.Equals("Explosion", StringComparison.OrdinalIgnoreCase) ||
            !source.ProjectileRender.Equals("Motion_Blur", StringComparison.OrdinalIgnoreCase) ||
            !source.ProjectileBlend.Equals("Lighten", StringComparison.OrdinalIgnoreCase) ||
            source.ProjectileFlags != 0x42 ||
            Math.Abs(source.ProjectileBlurLength - 0.01f) > 0.0001f ||
            source.ProjectileLifeExpectancy != -1.0f ||
            Math.Abs(source.MuzzleSize - 0.3f) > 0.0001f ||
            Math.Abs(source.MuzzleLifetime - 0.02f) > 0.0001f ||
            source.MuzzleEmitters is not { Count: 5 } ||
            source.MuzzleEmitters.Any(layer =>
                layer.Position.Count < 3 || layer.BasisRight.Count < 3 ||
                layer.BasisUp.Count < 3 || layer.BasisForward.Count < 3 ||
                layer.Color.Count < 3 ||
                !layer.Update.Equals("Explosion", StringComparison.OrdinalIgnoreCase) ||
                !layer.Render.Equals("Billboard_to_Local_Z", StringComparison.OrdinalIgnoreCase) ||
                !layer.Blend.Equals("Lighten", StringComparison.OrdinalIgnoreCase) ||
                layer.Flags != 0x42 || layer.Size <= 0 || layer.Lifetime <= 0 ||
                !float.IsFinite(layer.Alpha) || layer.Alpha < 0 || layer.Alpha > 1) ||
            source.MuzzleEmitters.Count(layer => layer.TextureResref.Equals(
                "fx_muzflash", StringComparison.OrdinalIgnoreCase)) != 4 ||
            source.MuzzleEmitters.Count(layer => layer.TextureResref.Equals(
                "fx_flare02", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("First-encounter effect contract drifted");
        var laser = LoadOwnedEffectTexture(source.LaserTexture, manifestDirectory);
        var muzzle = LoadOwnedEffectTexture(source.MuzzleTexture, manifestDirectory);
        var flare = LoadOwnedEffectTexture(source.FlareTexture, manifestDirectory);
        GD.Print($"NIKAMI_AURORA_MUZZLE_SOURCE status=validated " +
                 $"model={source.MuzzleModel} layers={source.MuzzleEmitters.Count} " +
                 "textures=fx_muzflash:4,fx_flare02:1 " +
                 $"lifetime={source.MuzzleLifetime:F3} render=Billboard_to_Local_Z");
        return new FirstEncounterEffectTextures(
            laser,
            muzzle,
            flare,
            source.ProjectileSize,
            source.ProjectileBlurLength,
            source.MuzzleSize,
            source.MuzzleLifetime,
            source.MuzzleEmitters);
    }

    private static void ValidateFirstEncounterEffectPresentation(
        FirstEncounterEffectTextures source,
        KotorFirstEncounterPresentationConfiguration presentation)
    {
        var maximumProjectileLength = source.ProjectileSize * 8.0f;
        if (presentation.ProjectileLengthMeters > maximumProjectileLength ||
            presentation.MuzzleFlareScale > 1.0f ||
            presentation.ImpactSizeMeters > source.MuzzleSize ||
            presentation.ImpactLifetimeSeconds > 0.25f)
            throw new InvalidDataException(
                "First-encounter effect presentation exceeds source-size coverage bounds");
        GD.Print($"NIKAMI_AURORA_EFFECT_BOUNDS status=pass " +
                 $"projectile={source.ProjectileSize:F3}x" +
                 $"{presentation.ProjectileLengthMeters:F3} " +
                 $"source_blur={source.ProjectileBlurLength:F3} " +
                 $"muzzle={source.MuzzleSize:F3} " +
                 $"impact={presentation.ImpactSizeMeters:F3} " +
                 "blend=source-lighten signal=ldr-single-pass");
    }

    private static Texture2D LoadOwnedEffectTexture(
        FirstEncounterEffectTexture source,
        string manifestDirectory,
        int atlasColumns = 1,
        int atlasRows = 1,
        bool enhancedParticleFiltering = false)
    {
        var root = Path.GetFullPath(manifestDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            manifestDirectory, source.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Encounter effect path escapes the bundle: {source.Path}");
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (bytes.Length != source.ByteCount ||
            !hash.Equals(source.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Encounter effect payload drifted: {source.Resref}");
        var image = new Godot.Image();
        if (image.LoadPngFromBuffer(bytes) != Error.Ok || image.IsEmpty())
            throw new InvalidDataException($"Encounter effect texture is not playable: {source.Resref}");
        var sourceWidth = image.GetWidth();
        var sourceHeight = image.GetHeight();
        var upscale = 1;
        if (enhancedParticleFiltering)
        {
            if (atlasColumns <= 0 || atlasRows <= 0 ||
                sourceWidth % atlasColumns != 0 || sourceHeight % atlasRows != 0)
                throw new InvalidDataException(
                    $"Effect atlas grid is inconsistent: {source.Resref}");
            var frameWidth = sourceWidth / atlasColumns;
            var frameHeight = sourceHeight / atlasRows;
            var minimumDimension = Math.Min(frameWidth, frameHeight);
            while (upscale < EnhancedParticleMaximumUpscale &&
                   minimumDimension * upscale < EnhancedParticleFrameMinimumPixels)
                upscale *= 2;
            if (upscale > 1)
            {
                var filtered = Godot.Image.CreateEmpty(
                    sourceWidth * upscale,
                    sourceHeight * upscale,
                    false,
                    image.GetFormat());
                for (var row = 0; row < atlasRows; row++)
                    for (var column = 0; column < atlasColumns; column++)
                    {
                        var frame = image.GetRegion(new Rect2I(
                            column * frameWidth,
                            row * frameHeight,
                            frameWidth,
                            frameHeight));
                        frame.Resize(
                            frameWidth * upscale,
                            frameHeight * upscale,
                            Godot.Image.Interpolation.Lanczos);
                        filtered.BlitRect(
                            frame,
                            new Rect2I(
                                Vector2I.Zero,
                                new Vector2I(frame.GetWidth(), frame.GetHeight())),
                            new Vector2I(
                                column * frameWidth * upscale,
                                row * frameHeight * upscale));
                    }
                image = filtered;
            }
        }
        if (!image.HasMipmaps() && image.GenerateMipmaps() != Error.Ok)
            throw new InvalidDataException($"Encounter effect texture is not playable: {source.Resref}");
        var texture = ImageTexture.CreateFromImage(image);
        GD.Print($"NIKAMI_AURORA_EFFECT_TEXTURE status=validated resref={source.Resref} " +
                 $"source_size={sourceWidth}x{sourceHeight} " +
                 $"runtime_size={image.GetWidth()}x{image.GetHeight()} " +
                 $"atlas={atlasColumns}x{atlasRows} upscale={upscale} " +
                 $"mipmaps={image.HasMipmaps()}");
        return texture;
    }

    private static CreatureEffectRig LoadCreatureEffects(
        CreatureRecord creature,
        Node3D actor,
        string manifestDirectory,
        IDictionary<string, Texture2D> textureCache,
        bool enhancedPresentation)
    {
        var source = creature.Effects ?? new CreatureEffectsRecord(
            "nikami-aurora-kotor-actor-effects-v1", [], [], []);
        if (source.Schema != "nikami-aurora-kotor-actor-effects-v1")
            throw new InvalidDataException(
                $"Unsupported creature-effect schema: {creature.Template}");
        var expectedEmitters = (creature.Models ?? []).Sum(model => model.EmitterNodes);
        var expectedLights = (creature.Models ?? []).Sum(model => model.LightNodes);
        if (source.Emitters.Count != expectedEmitters ||
            source.Lights.Count != expectedLights)
            throw new InvalidDataException(
                $"Creature effect-node inventory drifted: {creature.Template}");
        var anchorsByName = FindDescendants<Node3D>(actor)
            .GroupBy(node => node.Name.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var emitters = new Dictionary<string, GpuParticles3D>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var emitter in source.Emitters)
        {
            if (emitter.Schema != "nikami-aurora-kotor-actor-emitter-v1" ||
                emitter.XGrid <= 0 || emitter.YGrid <= 0 ||
                emitter.LifeExpectancy <= 0 ||
                Math.Max(emitter.BirthRate, emitter.RandomBirthRate) <= 0 ||
                emitter.ColorStart.Count < 3 || emitter.ColorMid.Count < 3 ||
                emitter.ColorEnd.Count < 3 ||
                !emitter.Update.Equals("Explosion", StringComparison.OrdinalIgnoreCase) &&
                !emitter.Update.Equals("Fountain", StringComparison.OrdinalIgnoreCase) ||
                !emitter.Render.Equals("Normal", StringComparison.OrdinalIgnoreCase) &&
                !emitter.Render.Equals("Motion_Blur", StringComparison.OrdinalIgnoreCase) ||
                !emitter.Blend.Equals("Normal", StringComparison.OrdinalIgnoreCase) &&
                !emitter.Blend.Equals("Lighten", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Unsupported creature emitter: {creature.Template}/{emitter.AnchorNode}");
            anchorsByName.TryGetValue(emitter.AnchorNode, out var anchors);
            anchors ??= [];
            if (anchors.Length != 1 || !emitters.TryAdd(
                    emitter.AnchorNode,
                    CreateCreatureEmitter(
                        emitter, anchors[0], manifestDirectory, textureCache,
                        enhancedPresentation)))
                throw new InvalidDataException(
                    $"Creature emitter anchor is not unique: " +
                    $"{creature.Template}/{emitter.AnchorNode}");
        }

        var lights = new Dictionary<string, OmniLight3D>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var light in source.Lights)
        {
            if (light.Schema != "nikami-aurora-kotor-actor-light-v1" ||
                light.Color.Count < 3 || light.Multiplier < 0 ||
                !light.Color.Take(3).All(float.IsFinite))
                throw new InvalidDataException(
                    $"Unsupported creature light: {creature.Template}/{light.AnchorNode}");
            anchorsByName.TryGetValue(light.AnchorNode, out var anchors);
            anchors ??= [];
            var runtimeLight = new OmniLight3D
            {
                Name = "SourceActorLight",
                LightColor = ToColor(light.Color),
                LightEnergy = light.Multiplier,
                OmniRange = Math.Max(0.01f, light.Radius),
                ShadowEnabled = false,
                Visible = light.Radius > 0
            };
            if (anchors.Length != 1 || !lights.TryAdd(light.AnchorNode, runtimeLight))
                throw new InvalidDataException(
                    $"Creature light anchor is not unique: " +
                    $"{creature.Template}/{light.AnchorNode}");
            anchors[0].AddChild(runtimeLight);
        }

        var animations = source.Animations.ToDictionary(
            animation => animation.Name,
            StringComparer.OrdinalIgnoreCase);
        var hasExplosionEmitter = emitters.Values.Any(candidate =>
            candidate.GetMeta("source_update").AsString().Equals(
                "Explosion", StringComparison.OrdinalIgnoreCase));
        foreach (var animation in animations.Values)
        {
            if (animation.Length <= 0 ||
                animation.Events.Count > 0 && !hasExplosionEmitter ||
                animation.Events.Any(item => item.Time < 0 ||
                    item.Time > animation.Length ||
                    !item.Name.Equals("detonate", StringComparison.OrdinalIgnoreCase)) ||
                animation.Tracks.Any(track => UnsupportedCreatureEffectTrack(
                    track, emitters, lights, animation.Length)))
                throw new InvalidDataException(
                    $"Unsupported creature effect animation: " +
                    $"{creature.Template}/{animation.Name}");
        }
        return new CreatureEffectRig(emitters, lights, animations);
    }

    private static bool UnsupportedCreatureEffectTrack(
        CreatureEffectTrackRecord track,
        IReadOnlyDictionary<string, GpuParticles3D> emitters,
        IReadOnlyDictionary<string, OmniLight3D> lights,
        float animationLength)
    {
        var validTarget =
            track.Controller.Equals("radius", StringComparison.OrdinalIgnoreCase)
                ? emitters.ContainsKey(track.AnchorNode) ||
                  lights.ContainsKey(track.AnchorNode)
                : track.Controller.Equals("color", StringComparison.OrdinalIgnoreCase) &&
                  lights.ContainsKey(track.AnchorNode);
        var expectedValueCount = track.Controller.Equals(
            "color", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
        return !validTarget || track.Keys.Count == 0 || track.Keys.Any(key =>
            key.Time < 0 || key.Time > animationLength ||
            key.Value.Count < expectedValueCount ||
            !key.Value.All(float.IsFinite));
    }

    private static GpuParticles3D CreateCreatureEmitter(
        CreatureEmitterRecord source,
        Node3D anchor,
        string manifestDirectory,
        IDictionary<string, Texture2D> textureCache,
        bool enhancedPresentation)
    {
        var textureKey = $"actor:{source.Texture.PayloadSha256}:" +
                         $"{source.XGrid}x{source.YGrid}:" +
                         $"enhanced={enhancedPresentation}";
        if (!textureCache.TryGetValue(textureKey, out var texture))
        {
            texture = LoadOwnedEffectTexture(
                source.Texture, manifestDirectory, source.XGrid, source.YGrid,
                enhancedPresentation);
            textureCache[textureKey] = texture;
        }
        var mid = Mathf.Clamp(
            source.PercentMid, source.PercentStart + 0.0001f,
            source.PercentEnd - 0.0001f);
        var gradient = new Gradient
        {
            Offsets = [source.PercentStart, mid, source.PercentEnd],
            Colors =
            [
                new Color(source.ColorStart[0], source.ColorStart[1],
                    source.ColorStart[2], source.AlphaStart),
                new Color(source.ColorMid[0], source.ColorMid[1],
                    source.ColorMid[2], source.AlphaMid),
                new Color(source.ColorEnd[0], source.ColorEnd[1],
                    source.ColorEnd[2], source.AlphaEnd)
            ]
        };
        var scale = new Curve
        {
            MinValue = 0,
            MaxValue = Math.Max(1.0f,
                Math.Max(source.SizeStart,
                    Math.Max(source.SizeMid, source.SizeEnd)))
        };
        scale.AddPoint(new Vector2(source.PercentStart, source.SizeStart));
        scale.AddPoint(new Vector2(mid, source.SizeMid));
        scale.AddPoint(new Vector2(source.PercentEnd, source.SizeEnd));
        var frameCount = source.XGrid * source.YGrid;
        var process = new ParticleProcessMaterial
        {
            Direction = Vector3.Forward,
            Spread = Mathf.RadToDeg(source.SpreadRadians),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(
                Math.Max(0.001f, source.XSize * 0.01f),
                Math.Max(0.001f, source.YSize * 0.01f),
                Math.Max(0.001f, Math.Min(source.XSize, source.YSize) * 0.01f)),
            InitialVelocityMin = source.Velocity,
            InitialVelocityMax = source.Velocity + source.RandomVelocity,
            AngularVelocityMin = Mathf.RadToDeg(source.ParticleRotation),
            AngularVelocityMax = Mathf.RadToDeg(source.ParticleRotation),
            Gravity = Vector3.Up * -source.Gravity,
            ScaleMin = 1,
            ScaleMax = 1,
            ScaleCurve = new CurveTexture { Curve = scale },
            ColorRamp = new GradientTexture1D { Gradient = gradient },
            AnimSpeedMin = source.Fps * source.LifeExpectancy / frameCount,
            AnimSpeedMax = source.Fps * source.LifeExpectancy / frameCount,
            AnimOffsetMin = source.FrameStart / frameCount,
            AnimOffsetMax = source.FrameStart / frameCount
        };
        if ((source.Flags & EmitterCollisionBounceFlag) != 0)
        {
            process.CollisionMode = ParticleProcessMaterial.CollisionModeEnum.Rigid;
            process.CollisionBounce = source.BounceCoefficient;
            process.CollisionUseScale = true;
        }
        var motionBlur = source.Render.Equals(
            "Motion_Blur", StringComparison.OrdinalIgnoreCase);
        var material = new StandardMaterial3D
        {
            AlbedoTexture = texture,
            AlbedoColor = Colors.White,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = source.Blend.Equals(
                "Lighten", StringComparison.OrdinalIgnoreCase)
                ? BaseMaterial3D.BlendModeEnum.Add
                : BaseMaterial3D.BlendModeEnum.Mix,
            CullMode = source.TwoSidedTexture != 0 || motionBlur
                ? BaseMaterial3D.CullModeEnum.Disabled
                : BaseMaterial3D.CullModeEnum.Back,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
            ParticlesAnimHFrames = source.XGrid,
            ParticlesAnimVFrames = source.YGrid,
            ParticlesAnimLoop = true,
            ProximityFadeEnabled = enhancedPresentation,
            ProximityFadeDistance = enhancedPresentation
                ? EnhancedParticleProximityFadeDistance
                : 1.0f
        };
        var particles = new GpuParticles3D
        {
            Name = "SourceActorEmitter",
            Amount = Math.Max(1, (int)Math.Ceiling(
                Math.Max(source.BirthRate,
                    source.BirthRate + source.RandomBirthRate) *
                source.LifeExpectancy)),
            Lifetime = source.LifeExpectancy,
            OneShot = source.Update.Equals(
                "Explosion", StringComparison.OrdinalIgnoreCase),
            Explosiveness = source.Update.Equals(
                "Explosion", StringComparison.OrdinalIgnoreCase) ? 1.0f : 0.0f,
            Randomness = Mathf.Clamp(
                source.RandomBirthRate /
                Math.Max(1.0f, source.BirthRate + source.RandomBirthRate), 0, 1),
            LocalCoords = false,
            FixedFps = 30,
            Interpolate = true,
            ProcessMaterial = process,
            DrawPass1 = new QuadMesh
            {
                Size = motionBlur
                    ? new Vector2(1.0f, Math.Max(1.0f,
                        source.BlurLength / Math.Max(0.001f, source.SizeStart)))
                    : Vector2.One,
                Material = material
            },
            VisibilityAabb = new Aabb(Vector3.One * -8, Vector3.One * 16),
            Emitting = false
        };
        particles.SetMeta("source_update", source.Update);
        particles.SetMeta("source_anchor", source.AnchorNode);
        anchor.AddChild(particles);
        return particles;
    }

    private void PlayActorEffects(string actor, string requested, bool loop)
    {
        if (!actorEffectRigs.TryGetValue(actor, out var rig)) return;
        var generation = ++rig.Generation;
        foreach (var activeTween in rig.ActiveTweens)
            activeTween.Kill();
        rig.ActiveTweens.Clear();
        foreach (var emitter in rig.Emitters.Values)
            emitter.Emitting = false;
        foreach (var light in rig.Lights.Values)
            light.Visible = false;
        if (!rig.Animations.TryGetValue(requested, out var animation)) return;

        var events = animation.Events.OrderBy(item => item.Time).ToArray();
        if (events.Length > 0)
        {
            var tween = CreateTween();
            rig.ActiveTweens.Add(tween);
            if (loop) tween.SetLoops();
            var elapsed = 0.0f;
            foreach (var item in events)
            {
                tween.TweenInterval(Math.Max(0.0f, item.Time - elapsed));
                tween.TweenCallback(Callable.From(() =>
                {
                    if (rig.Generation != generation) return;
                    foreach (var emitter in rig.ExplosionEmitters)
                    {
                        emitter.Restart();
                        emitter.Emitting = true;
                    }
                }));
                elapsed = item.Time;
            }
            tween.TweenInterval(Math.Max(0.001f, animation.Length - elapsed));
        }

        foreach (var track in animation.Tracks)
        {
            var tween = CreateTween();
            rig.ActiveTweens.Add(tween);
            if (loop) tween.SetLoops();
            var keys = track.Keys.OrderBy(item => item.Time).ToArray();
            var elapsed = 0.0f;
            for (var index = 0; index < keys.Length; index++)
            {
                var key = keys[index];
                tween.TweenInterval(Math.Max(0.0f, key.Time - elapsed));
                tween.TweenCallback(Callable.From(() =>
                {
                    if (rig.Generation != generation) return;
                    ApplyCreatureEffectKey(rig, track, key);
                }));
                elapsed = key.Time;
            }
            tween.TweenInterval(Math.Max(0.001f, animation.Length - elapsed));
        }
        GD.Print($"NIKAMI_AURORA_ACTOR_EFFECT status=scheduled actor={actor} " +
                 $"animation={requested} events={animation.Events.Count} " +
                 $"tracks={animation.Tracks.Count} loop={(loop ? 1 : 0)}");
    }

    private static void ApplyCreatureEffectKey(
        CreatureEffectRig rig,
        CreatureEffectTrackRecord track,
        CreatureEffectKeyRecord key)
    {
        if (track.Controller.Equals("radius", StringComparison.OrdinalIgnoreCase) &&
            rig.Emitters.TryGetValue(track.AnchorNode, out var emitter))
        {
            emitter.Emitting = key.Value[0] > 0.0f;
            return;
        }
        if (track.Controller.Equals("radius", StringComparison.OrdinalIgnoreCase) &&
            rig.Lights.TryGetValue(track.AnchorNode, out var radiusLight))
        {
            radiusLight.OmniRange = Math.Max(0.01f, key.Value[0]);
            radiusLight.Visible = key.Value[0] > 0.0f;
            return;
        }
        if (track.Controller.Equals("color", StringComparison.OrdinalIgnoreCase) &&
            key.Value.Count >= 3 &&
            rig.Lights.TryGetValue(track.AnchorNode, out var colorLight))
        {
            colorLight.LightColor = ToColor(key.Value);
            colorLight.Visible = key.Value.Take(3).Any(component => component > 0.0f);
        }
    }

    private static RoomEmitterReport LoadRoomEmitters(
        RoomRecord room,
        Node3D roomRoot,
        string manifestDirectory,
        IDictionary<string, Texture2D> textureCache,
        bool enhancedPresentation,
        Color areaAmbient)
    {
        var total = 0;
        var alpha = 0;
        var additive = 0;
        var single = 0;
        var finiteSingle = 0;
        var oriented = 0;
        var orientedAlpha = 0;
        var normalizedGrid = 0;
        var distributed = 0;
        var tinted = 0;
        var softFade = 0;
        var depthAware = 0;
        var atlasRangeValidated = 0;
        var visualSafetyValidated = 0;
        var maximumSmokeQuadExtent = 0.0f;
        var maximumSparkTrailExtent = 0.0f;
        var smoke = 0;
        var spark = 0;
        var pointToPoint = 0;
        var collisionBounce = 0;
        var bounceCoefficients = new List<float>();
        var damagedEnd = false;
        var emitterSources = room.Emitters ?? [];
        var collisionEmitterCount = emitterSources.Count(source =>
            (source.Flags & EmitterCollisionBounceFlag) != 0);
        var collisionReport = collisionEmitterCount > 0
            ? BuildRoomParticleCollision(room, roomRoot)
            : default;
        foreach (var source in emitterSources)
        {
            var isSmoke = source.Texture.Resref.Equals(
                "fx_Smoke", StringComparison.OrdinalIgnoreCase);
            var isSpark = source.Texture.Resref.Equals(
                "fx_Spark", StringComparison.OrdinalIgnoreCase);
            var isAlpha = source.Blend.Equals(
                "Normal", StringComparison.OrdinalIgnoreCase);
            var isAdditive = source.Blend.Equals(
                "Lighten", StringComparison.OrdinalIgnoreCase);
            var isNormalRender = source.Render.Equals(
                "Normal", StringComparison.OrdinalIgnoreCase);
            var isMotionBlur = source.Render.Equals(
                "Motion_Blur", StringComparison.OrdinalIgnoreCase);
            var isBillboardToLocalZ = source.Render.Equals(
                "Billboard_to_Local_Z", StringComparison.OrdinalIgnoreCase);
            var isBillboardToWorldZ = source.Render.Equals(
                "Billboard_to_World_Z", StringComparison.OrdinalIgnoreCase);
            var isAlignedToParticleDirection = source.Render.Equals(
                "Aligned_to_Particle_Dir", StringComparison.OrdinalIgnoreCase);
            var isOrientedRender = isBillboardToLocalZ || isBillboardToWorldZ ||
                                   isAlignedToParticleDirection;
            var isDistributed = source.SpawnWidthMeters > 0 ||
                                source.SpawnHeightMeters > 0;
            var isTinted = (source.Flags & EmitterTintedFlag) != 0;
            var isCollisionBounce =
                (source.Flags & EmitterCollisionBounceFlag) != 0;
            var sourceTint = isTinted ? areaAmbient : Colors.White;
            var isFountain = source.Update.Equals(
                "Fountain", StringComparison.OrdinalIgnoreCase);
            var isSingle = source.Update.Equals(
                "Single", StringComparison.OrdinalIgnoreCase);
            var isPointToPoint =
                (source.Flags & EmitterPointToPointFlag) != 0;
            var hasPointToPointTarget =
                source.PointToPointTargetPosition is { Count: >= 3 } &&
                source.PointToPointTargetPosition.Take(3).All(float.IsFinite);
            var sourceFrameCount = source.XGrid * source.YGrid;
            var persistentSingleFrames = isSingle && source.LifeExpectancy == -1.0f;
            var minimumFrame = persistentSingleFrames ? 1.0f : 0.0f;
            var maximumFrame = persistentSingleFrames
                ? sourceFrameCount
                : sourceFrameCount - 1.0f;
            var frameRangeValid =
                float.IsFinite(source.FrameStart) && float.IsFinite(source.FrameEnd) &&
                source.FrameStart == MathF.Truncate(source.FrameStart) &&
                source.FrameEnd == MathF.Truncate(source.FrameEnd) &&
                source.FrameStart >= minimumFrame && source.FrameEnd <= maximumFrame &&
                source.FrameStart <= source.FrameEnd &&
                float.IsFinite(source.Fps) && source.Fps >= 0;
            var maximumSize = Math.Max(
                source.SizeStart, Math.Max(source.SizeMid, source.SizeEnd));
            var motionBlurAspect = isMotionBlur
                ? Math.Max(1.0f, source.BlurLength /
                    Math.Max(0.001f, source.SizeStart))
                : 1.0f;
            var maximumQuadExtent = Math.Max(maximumSize, maximumSize * motionBlurAspect);
            if (source.Schema != "nikami-aurora-kotor-room-emitter-v2" ||
                (!isFountain && !isSingle) ||
                source.XGrid <= 0 || source.YGrid <= 0 ||
                source.AuthoredXGrid < 0 || source.AuthoredYGrid < 0 ||
                source.Direction.Count < 3 || source.Position.Count < 3 ||
                source.BasisRight.Count < 3 || source.BasisUp.Count < 3 ||
                source.BasisForward.Count < 3 ||
                !float.IsFinite(source.XSize) || source.XSize < 0 ||
                !float.IsFinite(source.YSize) || source.YSize < 0 ||
                !float.IsFinite(source.SpawnWidthMeters) ||
                source.SpawnWidthMeters < 0 ||
                !float.IsFinite(source.SpawnHeightMeters) ||
                source.SpawnHeightMeters < 0 ||
                Math.Abs(source.SpawnWidthMeters - source.XSize * 0.01f) > 0.0001f ||
                Math.Abs(source.SpawnHeightMeters - source.YSize * 0.01f) > 0.0001f ||
                source.ColorStart.Count < 3 || source.ColorMid.Count < 3 ||
                 source.ColorEnd.Count < 3 ||
                 !float.IsFinite(source.Gravity) ||
                 !float.IsFinite(source.BirthRate) || source.BirthRate < 0 ||
                 !float.IsFinite(source.RandomBirthRate) || source.RandomBirthRate < 0 ||
                 !float.IsFinite(source.Velocity) ||
                 !float.IsFinite(source.RandomVelocity) ||
                 !float.IsFinite(source.Mass) ||
                 !float.IsFinite(source.ParticleRotation) ||
                 !float.IsFinite(source.SpreadRadians) || source.SpreadRadians < 0 ||
                 !float.IsFinite(source.LifeExpectancy) ||
                 !source.ColorStart.Take(3).All(float.IsFinite) ||
                 !source.ColorMid.Take(3).All(float.IsFinite) ||
                 !source.ColorEnd.Take(3).All(float.IsFinite) ||
                 !float.IsFinite(source.AlphaStart) || source.AlphaStart < 0 ||
                 source.AlphaStart > 1 ||
                 !float.IsFinite(source.AlphaMid) || source.AlphaMid < 0 ||
                 source.AlphaMid > 1 ||
                 !float.IsFinite(source.AlphaEnd) || source.AlphaEnd < 0 ||
                 source.AlphaEnd > 1 ||
                 !float.IsFinite(source.SizeStart) || source.SizeStart < 0 ||
                 !float.IsFinite(source.SizeMid) || source.SizeMid < 0 ||
                 !float.IsFinite(source.SizeEnd) || source.SizeEnd < 0 ||
                 !float.IsFinite(source.BlurLength) || source.BlurLength < 0 ||
                 !frameRangeValid ||
                 !float.IsFinite(maximumQuadExtent) || maximumQuadExtent <= 0 ||
                 (enhancedPresentation &&
                  maximumQuadExtent > EnhancedParticleMaximumQuadExtentMeters) ||
                 source.PercentStart < 0 || source.PercentEnd > 1 ||
                source.PercentStart > source.PercentMid ||
                source.PercentMid > source.PercentEnd ||
                source.RenderOrder < Material.RenderPriorityMin ||
                source.RenderOrder > Material.RenderPriorityMax ||
                 (!isAlpha && !isAdditive) ||
                 (!isNormalRender && !isMotionBlur && !isOrientedRender) ||
                 (source.Flags & UnsupportedRoomEmitterFlags) != 0 ||
                 (isCollisionBounce &&
                  (!float.IsFinite(source.BounceCoefficient) ||
                   source.BounceCoefficient < 0 || source.BounceCoefficient > 1)) ||
                 (isPointToPoint &&
                 ((source.Flags & EmitterPointToPointBezierFlag) != 0 ||
                  !hasPointToPointTarget || source.Gravity <= 0)) ||
                (!isPointToPoint && source.PointToPointTargetPosition is not null) ||
                (!string.IsNullOrWhiteSpace(source.DepthTexture) &&
                 !source.DepthTexture.Equals("NULL", StringComparison.OrdinalIgnoreCase)) ||
                source.SpawnType != 0 ||
                source.FrameBlender != 0 ||
                (isFountain &&
                 (source.BirthRate <= 0 || source.LifeExpectancy <= 0)) ||
                (isSingle && !IsSupportedSingleEmitter(source)))
                throw new InvalidDataException(
                    $"Unsupported room emitter: {room.Model}/{source.NodePath}");

            // Cull masks are the destination join that prevents one static
            // target from attracting a neighboring source emitter. Layer 1 is
            // retained for ordinary particles; a room may use layers 2..20 for
            // isolated straight-P2P systems.
            if (isPointToPoint && pointToPoint >= 19)
                throw new InvalidDataException(
                    $"Room exceeds isolated point-to-point layers: {room.Model}");
            var particleLayer = isPointToPoint
                ? 1u << (pointToPoint + 1)
                : 1u;

            var textureCacheKey = $"{source.Texture.PayloadSha256}:" +
                                  $"{source.XGrid}x{source.YGrid}:" +
                                  $"enhanced={enhancedPresentation}";
            if (!textureCache.TryGetValue(textureCacheKey, out var texture))
            {
                texture = LoadOwnedEffectTexture(
                    source.Texture,
                    manifestDirectory,
                    source.XGrid,
                    source.YGrid,
                    enhancedPresentation);
                textureCache[textureCacheKey] = texture;
            }

            var isPersistentSingle = isSingle && source.LifeExpectancy == -1.0f;
            if (isPersistentSingle)
            {
                roomRoot.AddChild(CreateSingleEmitter(source, texture, sourceTint));
                total++;
                alpha++;
                single++;
                oriented += isOrientedRender ? 1 : 0;
                normalizedGrid += source.AuthoredXGrid == 0 || source.AuthoredYGrid == 0
                    ? 1
                    : 0;
                distributed += isDistributed ? 1 : 0;
                tinted += isTinted ? 1 : 0;
                depthAware++;
                atlasRangeValidated++;
                visualSafetyValidated++;
                maximumSmokeQuadExtent = isSmoke
                    ? Math.Max(maximumSmokeQuadExtent, maximumQuadExtent)
                    : maximumSmokeQuadExtent;
                maximumSparkTrailExtent = isSpark
                    ? Math.Max(maximumSparkTrailExtent, maximumQuadExtent)
                    : maximumSparkTrailExtent;
                continue;
            }

            var colorMidOffset = Mathf.Clamp(
                source.PercentMid,
                source.PercentStart + 0.0001f,
                source.PercentEnd - 0.0001f);
            var gradient = new Gradient
            {
                Offsets = [source.PercentStart, colorMidOffset, source.PercentEnd],
                Colors =
                [
                    ToEmitterColor(source.ColorStart, source.AlphaStart, sourceTint),
                    ToEmitterColor(source.ColorMid, source.AlphaMid, sourceTint),
                    ToEmitterColor(source.ColorEnd, source.AlphaEnd, sourceTint)
                ]
            };
            var colorRamp = new GradientTexture1D { Gradient = gradient };
            var scaleCurve = new Curve
            {
                MinValue = 0,
                MaxValue = Math.Max(
                    1.0f, Math.Max(source.SizeStart,
                        Math.Max(source.SizeMid, source.SizeEnd)))
            };
            scaleCurve.AddPoint(new Vector2(source.PercentStart, source.SizeStart));
            scaleCurve.AddPoint(new Vector2(colorMidOffset, source.SizeMid));
            scaleCurve.AddPoint(new Vector2(source.PercentEnd, source.SizeEnd));
            var frameCount = Math.Max(1, source.XGrid * source.YGrid);
            // Godot's particle animation offset is a normalized atlas phase;
            // source frame N therefore starts at N/frameCount (not N/(N-1),
            // which wraps the authored last frame back to the first tile).
            var frameDivisor = Math.Max(1, frameCount);
            var frameStart = Mathf.Clamp(source.FrameStart / frameDivisor, 0, 1);
            var frameEnd = Mathf.Clamp(source.FrameEnd / frameDivisor, 0, 1);
            var animationCycles = source.Fps > 0
                ? source.Fps * source.LifeExpectancy / frameCount
                : 0.0f;
            var emitterBasis = ToEmitterGodotBasis(source);
            var inverseEmitterBasis = emitterBasis.Inverse();
            var processMaterial = new ParticleProcessMaterial
            {
                Direction = (inverseEmitterBasis *
                             ToGodot(source.Direction).Normalized()).Normalized(),
                Spread = Mathf.RadToDeg(source.SpreadRadians),
                // Source random velocity is an added [0, randVel] magnitude,
                // not a symmetric subtraction/addition around base velocity.
                InitialVelocityMin = Math.Min(
                    source.Velocity, source.Velocity + source.RandomVelocity),
                InitialVelocityMax = Math.Max(
                    source.Velocity, source.Velocity + source.RandomVelocity),
                AngularVelocityMin = Mathf.RadToDeg(source.ParticleRotation),
                AngularVelocityMax = Mathf.RadToDeg(source.ParticleRotation),
                // Straight P2P reuses the authored grav controller as constant
                // acceleration towards its child target. It must not also
                // become world-down gravity.
                Gravity = isPointToPoint
                    ? Vector3.Zero
                    : inverseEmitterBasis * (Vector3.Up * -source.Gravity),
                AttractorInteractionEnabled = isPointToPoint,
                EmissionShape = isDistributed
                    ? ParticleProcessMaterial.EmissionShapeEnum.Box
                    : ParticleProcessMaterial.EmissionShapeEnum.Point,
                EmissionBoxExtents = new Vector3(
                    source.SpawnWidthMeters * 0.5f,
                    0.0f,
                    source.SpawnHeightMeters * 0.5f),
                ScaleMin = 1,
                ScaleMax = 1,
                ScaleCurve = new CurveTexture { Curve = scaleCurve },
                ColorRamp = colorRamp,
                AnimSpeedMin = animationCycles,
                AnimSpeedMax = animationCycles,
                AnimOffsetMin = (source.Flags & EmitterRandomPlaybackFlag) != 0
                    ? Math.Min(frameStart, frameEnd)
                    : frameStart,
                AnimOffsetMax = (source.Flags & EmitterRandomPlaybackFlag) != 0
                    ? Math.Max(frameStart, frameEnd)
                    : frameStart,
                ParticleFlagAlignY = isAlignedToParticleDirection
            };
            if (isCollisionBounce)
            {
                processMaterial.CollisionMode =
                    ParticleProcessMaterial.CollisionModeEnum.Rigid;
                processMaterial.CollisionBounce = source.BounceCoefficient;
                processMaterial.CollisionFriction = 0.0f;
                processMaterial.CollisionUseScale = true;
            }
            Material material;
            Mesh drawPass;
            if (isOrientedRender)
            {
                material = CreateOrientedEmitterMaterial(
                    source, texture, isAdditive);
                drawPass = CreateOrientedEmitterQuad(
                    source, material, emitterBasis);
            }
            else
            {
                var standard = new StandardMaterial3D
                {
                    AlbedoTexture = texture,
                    AlbedoColor = Colors.White,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = isAdditive
                        ? BaseMaterial3D.BlendModeEnum.Add
                        : BaseMaterial3D.BlendModeEnum.Mix,
                    CullMode = source.TwoSidedTexture != 0 || isMotionBlur
                        ? BaseMaterial3D.CullModeEnum.Disabled
                        : BaseMaterial3D.CullModeEnum.Back,
                    DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                    VertexColorUseAsAlbedo = true,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
                    TextureRepeat = false,
                    ParticlesAnimHFrames = source.XGrid,
                    ParticlesAnimVFrames = source.YGrid,
                    ParticlesAnimLoop = true,
                    ProximityFadeEnabled = enhancedPresentation,
                    ProximityFadeDistance = enhancedPresentation
                        ? EnhancedParticleProximityFadeDistance
                        : 1.0f
                };
                material = standard;
                drawPass = new QuadMesh
                {
                    Size = isMotionBlur
                        ? new Vector2(
                            1.0f,
                            Math.Max(1.0f, source.BlurLength /
                                Math.Max(0.001f, source.SizeStart)))
                        : Vector2.One,
                    Material = material
                };
            }
            var travel = source.Velocity * source.LifeExpectancy + source.SizeEnd * 2;
            var pointToPointRadius = 0.0f;
            if (isPointToPoint)
            {
                var target = ToGodot(source.PointToPointTargetPosition!);
                var origin = ToGodot(source.Position);
                var initialMaximum = Math.Max(
                    0.0f, source.Velocity + Math.Abs(source.RandomVelocity));
                var spawnRadius = 0.5f * MathF.Sqrt(
                    source.SpawnWidthMeters * source.SpawnWidthMeters +
                    source.SpawnHeightMeters * source.SpawnHeightMeters);
                var accelerationTravel = 0.5f * source.Gravity *
                                         source.LifeExpectancy * source.LifeExpectancy;
                pointToPointRadius = target.DistanceTo(origin) +
                                     initialMaximum * source.LifeExpectancy +
                                     accelerationTravel + spawnRadius +
                                     2.0f * Math.Max(source.SizeStart,
                                         Math.Max(source.SizeMid, source.SizeEnd)) + 1.0f;
            }
            var boundsExtent = isPointToPoint
                ? Math.Max(8.0f, pointToPointRadius)
                : Math.Max(8.0f, Math.Min(64.0f, travel));
            var particles = new GpuParticles3D
            {
                Name = "Emitter_" + source.NodePath.Replace('/', '_'),
                Position = ToGodot(source.Position),
                Basis = emitterBasis,
                Amount = isSingle
                    ? 1
                    : Math.Max(1, (int)Math.Ceiling(
                        source.BirthRate * source.LifeExpectancy)),
                Lifetime = source.LifeExpectancy,
                OneShot = isSingle && source.Loop == 0,
                Explosiveness = isSingle ? 1.0f : 0.0f,
                Preprocess = isSingle ? 0.0f : Math.Min(source.LifeExpectancy, 6.0f),
                Randomness = Mathf.Clamp(
                    source.RandomBirthRate / Math.Max(1.0f, source.BirthRate), 0, 1),
                FixedFps = 30,
                Interpolate = true,
                LocalCoords = false,
                DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth,
                Layers = particleLayer,
                ProcessMaterial = processMaterial,
                DrawPass1 = drawPass,
                VisibilityAabb = new Aabb(
                    Vector3.One * -boundsExtent,
                    Vector3.One * boundsExtent * 2),
                Emitting = true
            };
            roomRoot.AddChild(particles);
            if (isPointToPoint)
            {
                // Godot's zero-attenuation sphere applies unit falloff inside
                // the conservative lifetime radius. Combined with isolated
                // layers this is constant-magnitude acceleration towards the
                // exact resolved source child target.
                var attractor = new GpuParticlesAttractorSphere3D
                {
                    Name = "P2PTarget_" + source.NodePath.Replace('/', '_'),
                    Position = ToGodot(source.PointToPointTargetPosition!),
                    Radius = pointToPointRadius,
                    Strength = source.Gravity,
                    Attenuation = 0.0f,
                    Directionality = 0.0f,
                    CullMask = particleLayer
                };
                roomRoot.AddChild(attractor);
                particles.AddToGroup("kotor_p2p_emitters");
                particles.SetMeta("source_target_global", attractor.GlobalPosition);
                particles.SetMeta("source_quad_max_meters", Math.Max(
                    source.SizeStart, Math.Max(source.SizeMid, source.SizeEnd)));
                pointToPoint++;
            }
            total++;
            alpha += isAlpha ? 1 : 0;
            additive += isAdditive ? 1 : 0;
            single += isSingle ? 1 : 0;
            finiteSingle += isSingle ? 1 : 0;
            oriented += isOrientedRender ? 1 : 0;
            orientedAlpha += isOrientedRender && isAlpha ? 1 : 0;
            normalizedGrid += source.AuthoredXGrid == 0 || source.AuthoredYGrid == 0
                ? 1
                : 0;
            distributed += isDistributed ? 1 : 0;
            tinted += isTinted ? 1 : 0;
            softFade += enhancedPresentation && !isOrientedRender ? 1 : 0;
            depthAware++;
            atlasRangeValidated++;
            visualSafetyValidated++;
            maximumSmokeQuadExtent = isSmoke
                ? Math.Max(maximumSmokeQuadExtent, maximumQuadExtent)
                : maximumSmokeQuadExtent;
            maximumSparkTrailExtent = isSpark
                ? Math.Max(maximumSparkTrailExtent, maximumQuadExtent)
                : maximumSparkTrailExtent;
            smoke += isSmoke ? 1 : 0;
            spark += isSpark ? 1 : 0;
            collisionBounce += isCollisionBounce ? 1 : 0;
            if (isCollisionBounce)
                bounceCoefficients.Add(source.BounceCoefficient);
            damagedEnd |= room.Model.Equals(
                              "M01aa_03a", StringComparison.OrdinalIgnoreCase) &&
                          source.NodePath.EndsWith(
                              "Object107/smoke044", StringComparison.OrdinalIgnoreCase) &&
                          Math.Abs(source.BirthRate - 40.0f) < 0.0001f &&
                          Math.Abs(source.LifeExpectancy - 6.0f) < 0.0001f;
        }
        return new RoomEmitterReport(
            total, alpha, additive, single, finiteSingle, oriented, orientedAlpha,
            normalizedGrid, distributed, tinted, softFade, depthAware,
            atlasRangeValidated, visualSafetyValidated,
            maximumSmokeQuadExtent, maximumSparkTrailExtent, smoke, spark,
            pointToPoint, collisionBounce, collisionReport.Rooms,
            collisionReport.WalkmeshTriangles, bounceCoefficients, damagedEnd);
    }

    private static ParticleCollisionReport BuildRoomParticleCollision(
        RoomRecord room,
        Node3D roomRoot)
    {
        if (room.WalkmeshTriangles is not { Count: > 0 })
            throw new InvalidDataException(
                $"Collision-bounce emitters require source walkmesh: {room.Model}");

        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        var triangleCount = 0;
        foreach (var triangle in room.WalkmeshTriangles)
        {
            if (triangle.Count != 3 || triangle.Any(vertex => vertex.Count < 3))
                throw new InvalidDataException(
                    $"Collision-bounce walkmesh triangle is malformed: {room.Model}");
            // The collision destination consumes the same source vertices as
            // profile movement. It does not infer a floor plane or rebuild the
            // room render mesh as collision geometry.
            surface.AddVertex(ToGodot(triangle[0]));
            surface.AddVertex(ToGodot(triangle[1]));
            surface.AddVertex(ToGodot(triangle[2]));
            triangleCount++;
        }
        surface.GenerateNormals();
        if (surface.Commit() is not ArrayMesh walkmesh ||
            walkmesh.GetSurfaceCount() != 1 || triangleCount <= 0)
            throw new InvalidDataException(
                $"Collision-bounce walkmesh could not be materialized: {room.Model}");
        walkmesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            ResourceName = "SourceWalkmeshParticleCollision",
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        });

        var sourceMesh = new MeshInstance3D
        {
            Name = "ParticleCollisionSource_" + room.Model,
            Mesh = walkmesh,
            Layers = ParticleCollisionSourceVisualLayer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        sourceMesh.SetMeta("source", "room-walkmesh");
        sourceMesh.SetMeta("source_triangles", triangleCount);
        roomRoot.AddChild(sourceMesh);

        var bounds = walkmesh.GetAabb();
        if (!bounds.Position.IsFinite() || !bounds.Size.IsFinite())
            throw new InvalidDataException(
                $"Collision-bounce walkmesh bounds are invalid: {room.Model}");
        const float capturePadding = 0.05f;
        var captureSize = new Vector3(
            Math.Max(0.1f, bounds.Size.X + 2.0f * capturePadding),
            Math.Max(0.1f, bounds.Size.Y + 2.0f * capturePadding),
            Math.Max(0.1f, bounds.Size.Z + 2.0f * capturePadding));
        var collider = new GpuParticlesCollisionHeightField3D
        {
            Name = "ParticleCollisionWalkmesh_" + room.Model,
            Position = bounds.GetCenter(),
            Size = captureSize,
            Resolution = GpuParticlesCollisionHeightField3D.ResolutionEnum.Resolution512,
            UpdateMode = GpuParticlesCollisionHeightField3D.UpdateModeEnum.WhenMoved,
            FollowCameraEnabled = false,
            HeightfieldMask = ParticleCollisionSourceVisualLayer,
            CullMask = 1u
        };
        collider.SetMeta("source", "room-walkmesh");
        collider.SetMeta("source_triangles", triangleCount);
        roomRoot.AddChild(collider);
        return new ParticleCollisionReport(1, triangleCount);
    }

    private static bool IsSupportedSingleEmitter(RoomEmitterRecord source)
    {
        var frameCount = source.XGrid * source.YGrid;
        if (source.LifeExpectancy > 0) return true;
        var persistentRender =
            source.Render.Equals("Normal", StringComparison.OrdinalIgnoreCase) ||
            source.Render.Equals(
                "Billboard_to_Local_Z", StringComparison.OrdinalIgnoreCase);
        return persistentRender &&
               source.Blend.Equals("Normal", StringComparison.OrdinalIgnoreCase) &&
               source.LifeExpectancy == -1.0f && source.BirthRate == 1.0f &&
               source.Velocity == 0.0f && source.Gravity == 0.0f &&
               source.SizeStart > 0 &&
               source.SizeStart == source.SizeMid &&
               source.SizeMid == source.SizeEnd && source.FrameStart >= 1 &&
               source.FrameEnd <= frameCount && source.FrameStart <= source.FrameEnd;
    }

    private static ShaderMaterial CreateOrientedEmitterMaterial(
        RoomEmitterRecord source,
        Texture2D texture,
        bool additive)
    {
        var twoSided = source.TwoSidedTexture != 0;
        var shader = (additive, twoSided) switch
        {
            (false, false) => OdysseyOrientedParticleMixShader,
            (false, true) => OdysseyOrientedParticleMixTwoSidedShader,
            (true, false) => OdysseyOrientedParticleAddShader,
            _ => OdysseyOrientedParticleAddTwoSidedShader
        };
        var material = new ShaderMaterial
        {
            Shader = shader,
            RenderPriority = source.RenderOrder
        };
        material.SetShaderParameter("particle_texture", texture);
        material.SetShaderParameter(
            "atlas_grid", new Vector2(source.XGrid, source.YGrid));
        // Additive blending already contributes the source RGB to the target.
        // Do not amplify it a second time in the material shader.
        material.SetShaderParameter("exposure", 1.0f);
        return material;
    }

    private static ArrayMesh CreateOrientedEmitterQuad(
        RoomEmitterRecord source,
        Material material,
        Basis emitterBasis)
    {
        Vector3 right;
        Vector3 up;
        if (source.Render.Equals(
                "Billboard_to_Local_Z", StringComparison.OrdinalIgnoreCase))
        {
            // Odyssey's local-Z mode uses emitter-up as particle-right and
            // emitter-right as particle-up.
            right = ToGodot(source.BasisUp);
            up = ToGodot(source.BasisRight);
        }
        else if (source.Render.Equals(
                     "Billboard_to_World_Z", StringComparison.OrdinalIgnoreCase))
        {
            // Odyssey is Z-up. After conversion this is a Godot XZ plane.
            right = new Vector3(0, 0, -1);
            up = Vector3.Right;
        }
        else if (source.Render.Equals(
                     "Aligned_to_Particle_Dir", StringComparison.OrdinalIgnoreCase))
        {
            // ParticleProcessMaterial rotates local Y onto velocity. Keep the
            // draw plane canonical so velocity, rather than the room basis,
            // owns the per-particle alignment.
            right = Vector3.Right;
            up = Vector3.Up;
        }
        else
        {
            throw new InvalidDataException(
                $"Emitter does not own an oriented render basis: {source.NodePath}");
        }

        // Draw-pass vertices are local to the particle system. Convert the
        // desired world presentation axes back through the imported emitter
        // basis; the node basis then publishes both the source spawn rectangle
        // and the render plane without double rotation.
        var inverseEmitterBasis = emitterBasis.Inverse();
        right = inverseEmitterBasis * right;
        up = inverseEmitterBasis * up;

        if (right.LengthSquared() <= 0.999f || up.LengthSquared() <= 0.999f)
            throw new InvalidDataException(
                $"Emitter render basis is degenerate: {source.NodePath}");
        right = right.Normalized();
        up = (up - right * up.Dot(right)).Normalized();
        if (up.LengthSquared() <= 0.999f)
            throw new InvalidDataException(
                $"Emitter render basis is collinear: {source.NodePath}");

        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        surface.SetMaterial(material);
        var lowerLeft = (-right - up) * 0.5f;
        var lowerRight = (right - up) * 0.5f;
        var upperLeft = (-right + up) * 0.5f;
        var upperRight = (right + up) * 0.5f;
        AddEmitterVertex(surface, lowerLeft, new Vector2(0, 1));
        AddEmitterVertex(surface, lowerRight, new Vector2(1, 1));
        AddEmitterVertex(surface, upperRight, new Vector2(1, 0));
        AddEmitterVertex(surface, lowerLeft, new Vector2(0, 1));
        AddEmitterVertex(surface, upperRight, new Vector2(1, 0));
        AddEmitterVertex(surface, upperLeft, new Vector2(0, 0));
        return surface.Commit();
    }

    private static void AddEmitterVertex(
        SurfaceTool surface,
        Vector3 vertex,
        Vector2 uv)
    {
        surface.SetUV(uv);
        surface.AddVertex(vertex);
    }

    private static AnimatedSprite3D CreateSingleEmitter(
        RoomEmitterRecord source,
        Texture2D texture,
        Color sourceTint)
    {
        var frameWidth = texture.GetWidth() / source.XGrid;
        var frameHeight = texture.GetHeight() / source.YGrid;
        if (frameWidth <= 0 || frameHeight <= 0 ||
            frameWidth * source.XGrid != texture.GetWidth() ||
            frameHeight * source.YGrid != texture.GetHeight())
            throw new InvalidDataException(
                $"Single emitter atlas is inconsistent: {source.NodePath}");

        const string animationName = "source";
        var frames = new SpriteFrames();
        frames.AddAnimation(animationName);
        frames.SetAnimationLoopMode(animationName, SpriteFrames.LoopMode.Linear);
        frames.SetAnimationSpeed(animationName, Math.Max(1.0, source.Fps));
        var lastFrame = source.Fps > 0 ? (int)source.FrameEnd : (int)source.FrameStart;
        for (var sourceFrame = (int)source.FrameStart;
             sourceFrame <= lastFrame;
             sourceFrame++)
        {
            var index = sourceFrame - 1;
            frames.AddFrame(animationName, new AtlasTexture
            {
                Atlas = texture,
                Region = new Rect2(
                    index % source.XGrid * frameWidth,
                    index / source.XGrid * frameHeight,
                    frameWidth,
                    frameHeight)
            });
        }

        var emitterBasis = ToEmitterGodotBasis(source);
        var stableOffset = StableEmitterOffset(source.NodePath);
        var localSpawnOffset = new Vector3(
            stableOffset.X * source.SpawnWidthMeters,
            0,
            stableOffset.Y * source.SpawnHeightMeters);
        var sprite = new AnimatedSprite3D
        {
            Name = "SingleEmitter_" + source.NodePath.Replace('/', '_'),
            Position = ToGodot(source.Position) + emitterBasis * localSpawnOffset,
            SpriteFrames = frames,
            Animation = animationName,
            Autoplay = animationName,
            PixelSize = source.SizeStart / frameWidth,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Shaded = false,
            NoDepthTest = false,
            AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
            Modulate = ToEmitterColor(
                source.ColorStart, source.AlphaStart, sourceTint),
            RenderPriority = source.RenderOrder
        };
        if (source.Render.Equals(
                "Billboard_to_Local_Z", StringComparison.OrdinalIgnoreCase))
        {
            var right = ToGodot(source.BasisUp).Normalized();
            var up = ToGodot(source.BasisRight).Normalized();
            up = (up - right * up.Dot(right)).Normalized();
            var forward = right.Cross(up).Normalized();
            if (right.LengthSquared() <= 0.999f || up.LengthSquared() <= 0.999f ||
                forward.LengthSquared() <= 0.999f)
                throw new InvalidDataException(
                    $"Single emitter render basis is degenerate: {source.NodePath}");
            sprite.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
            sprite.Basis = new Basis(right, up, forward);
        }
        sprite.Play(animationName);
        return sprite;
    }

    private static Basis ToEmitterGodotBasis(RoomEmitterRecord source)
        => ToKotorGodotBasis(
            source.BasisRight, source.BasisUp, source.BasisForward,
            $"Emitter source basis is degenerate: {source.NodePath}");

    private static Basis ToKotorGodotBasis(
        IReadOnlyList<float> basisRight,
        IReadOnlyList<float> basisUp,
        IReadOnlyList<float> basisForward,
        string error = "KOTOR source basis is degenerate")
    {
        var x = ToGodot(basisRight).Normalized();
        var y = ToGodot(basisForward).Normalized();
        var z = -ToGodot(basisUp).Normalized();
        y = (y - x * y.Dot(x)).Normalized();
        z = x.Cross(y).Normalized();
        var basis = new Basis(x, y, z);
        if (x.LengthSquared() <= 0.999f || y.LengthSquared() <= 0.999f ||
            z.LengthSquared() <= 0.999f || basis.Determinant() <= 0.99f)
            throw new InvalidDataException(error);
        return basis;
    }

    private static Vector2 StableEmitterOffset(string nodePath)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(nodePath));
        var x = BitConverter.ToUInt32(bytes, 0) / (float)uint.MaxValue - 0.5f;
        var y = BitConverter.ToUInt32(bytes, 4) / (float)uint.MaxValue - 0.5f;
        return new Vector2(x, y);
    }

    private static Color ToEmitterColor(
        IReadOnlyList<float> source,
        float alpha,
        Color tint) =>
        new(source[0] * tint.R, source[1] * tint.G, source[2] * tint.B, alpha);

    private static AudioStream LoadOwnedAudio(
        FirstEncounterAudioSource source,
        string manifestDirectory)
    {
        var root = Path.GetFullPath(manifestDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            manifestDirectory, source.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Encounter audio path escapes the bundle: {source.Path}");
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (bytes.Length != source.ByteCount ||
            !hash.Equals(source.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Encounter audio payload drifted: {source.Resref}");
        AudioStream stream = source.Format.ToLowerInvariant() switch
        {
            "wav" => AudioStreamWav.LoadFromBuffer(bytes, new Godot.Collections.Dictionary()),
            "mp3" => AudioStreamMP3.LoadFromBuffer(bytes),
            _ => throw new InvalidDataException(
                $"Unsupported encounter audio format: {source.Format}")
        };
        if (stream.GetLength() <= 0.0)
            throw new InvalidDataException(
                $"Encounter audio decoded with no playable duration: {source.Resref}");
        GD.Print($"NIKAMI_AURORA_AUDIO status=validated resref={source.Resref} " +
                 $"source={source.SourceEncoding} payload={source.PayloadEncoding} " +
                 $"duration={stream.GetLength():F3}");
        return stream;
    }

    private void PlaySpatialOneShot(
        AudioStream stream,
        Vector3 position,
        float volumeDb = -3.0f)
    {
        var player = new AudioStreamPlayer3D
        {
            Stream = stream,
            VolumeDb = volumeDb,
            Position = position,
            MaxDistance = 40.0f,
            UnitSize = 1.0f,
            PanningStrength = 1.0f
        };
        player.Finished += player.QueueFree;
        AddChild(player);
        player.Play();
    }

    private void FireEncounterBlaster(string attackerTag, string targetTag)
    {
        var audio = firstEncounterAudio;
        var effects = firstEncounterEffectTextures;
        if (audio is null || effects is null ||
            !actorModels.TryGetValue(attackerTag, out var attacker) ||
            !actorModels.TryGetValue(targetTag, out var target))
            return;
        var presentation = runtimeConfiguration.Presentation.FirstEncounter;
        var muzzleHook = FindDescendantBySuffix<Node3D>(attacker, "bullethook")
            ?? throw new InvalidDataException(
                $"Encounter attacker has no source bullethook: {attackerTag}");
        var targetHook = FindDescendantBySuffix<Node3D>(target, "talkdummy")
            ?? throw new InvalidDataException(
                $"Encounter target has no source talkdummy: {targetTag}");
        var muzzle = muzzleHook.GlobalPosition;
        var destination = targetHook.GlobalPosition;
        if (!IsFinite(muzzle) || !IsFinite(destination) ||
            muzzle.DistanceSquaredTo(destination) <= 0.000001f)
            throw new InvalidDataException(
                $"Encounter source hook join is invalid: {attackerTag}->{targetTag}");
        encounterSourceHookJoinCount++;
        SpawnMuzzleFlash(muzzle, destination);
        var bolt = new MeshInstance3D
        {
            Name = $"AuthoredMotionBlurTrail_{encounterProjectileCount:D3}",
            Mesh = new BoxMesh
            {
                Size = new Vector3(
                    effects.ProjectileSize, effects.ProjectileSize,
                    presentation.ProjectileLengthMeters)
            },
            MaterialOverride = CreateEffectMaterial(
                RuntimeColor(presentation.ProjectileColor), effects.Laser, false)
        };
        AddChild(bolt);
        bolt.GlobalPosition = muzzle;
        bolt.LookAt(destination, Vector3.Up);
        var core = new MeshInstance3D
        {
            Name = "SourceProjectileCore",
            Mesh = new BoxMesh
            {
                Size = Vector3.One * effects.ProjectileSize
            },
            MaterialOverride = CreateEffectMaterial(
                RuntimeColor(presentation.ProjectileColor), effects.Laser, false)
        };
        bolt.AddChild(core);
        encounterProjectileCount++;
        encounterProjectileTrailCount++;
        PlaySpatialOneShot(audio.BlasterShot, muzzle, presentation.ShotVolumeDb);
        encounterAttackSoundCount++;
        var distance = muzzle.DistanceTo(destination);
        var duration = Math.Max(
            presentation.MinimumProjectileTravelSeconds,
            distance / presentation.ProjectileSpeedMetersPerSecond);
        var tween = CreateTween();
        tween.TweenProperty(bolt, "global_position", destination, duration);
        tween.TweenCallback(Callable.From(() =>
        {
            bolt.QueueFree();
            SpawnImpactFlash(destination);
            PlaySpatialOneShot(
                audio.BlasterImpact, destination, presentation.ImpactVolumeDb);
            encounterImpactSoundCount++;
            GD.Print($"NIKAMI_AURORA_IMPACT_SYNC status=pass " +
                     $"target={targetTag} position={destination} " +
                     $"tof={duration:F3} audio=spatial light=enhanced");
        }));
        GD.Print($"NIKAMI_AURORA_PROJECTILE status=fired attacker={attackerTag} " +
                 $"target={targetTag} origin=source-bullethook " +
                 $"destination=source-talkdummy from={muzzle} to={destination} " +
                 $"distance={distance:F3} speed=" +
                 $"{presentation.ProjectileSpeedMetersPerSecond:F3} " +
                 $"duration={duration:F3} trail={presentation.ProjectileLengthMeters:F3} " +
                 $"source_blur={effects.ProjectileBlurLength:F3}");
    }

    private static StandardMaterial3D CreateEffectMaterial(
        Color color, Texture2D? texture, bool billboard) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = color,
            AlbedoTexture = texture,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            BillboardMode = billboard
            ? BaseMaterial3D.BillboardModeEnum.Enabled
            : BaseMaterial3D.BillboardModeEnum.Disabled,
            BillboardKeepScale = billboard
        };

    private void SpawnMuzzleFlash(Vector3 position, Vector3 destination)
    {
        var effects = firstEncounterEffectTextures;
        if (effects is null) return;
        var presentation = runtimeConfiguration.Presentation.FirstEncounter;
        var flash = new Node3D
        {
            Name = $"MuzzleFlash_{encounterMuzzleFlashCount:D3}"
        };
        AddChild(flash);
        flash.GlobalPosition = position;
        flash.LookAt(destination, Vector3.Up);
        foreach (var layer in effects.MuzzleEmitters)
        {
            var flareLayer = layer.TextureResref.Equals(
                "fx_flare02", StringComparison.OrdinalIgnoreCase);
            var texture = flareLayer ? effects.Flare : effects.Muzzle;
            var tint = RuntimeColor(flareLayer
                ? presentation.MuzzleFlareColor
                : presentation.MuzzleColor);
            var sizeScale = flareLayer ? presentation.MuzzleFlareScale : 1.0f;
            var sourceColor = new Color(
                layer.Color[0] * tint.R,
                layer.Color[1] * tint.G,
                layer.Color[2] * tint.B,
                layer.Alpha);
            var authoredLayer = new MeshInstance3D
            {
                Name = "SourceMuzzleLayer_" + layer.Node,
                Position = ToGodot(layer.Position),
                Basis = ToKotorGodotBasis(
                    layer.BasisRight, layer.BasisUp, layer.BasisForward),
                Mesh = new QuadMesh
                {
                    Size = Vector2.One * layer.Size * sizeScale
                },
                MaterialOverride = CreateEffectMaterial(sourceColor, texture, false)
            };
            flash.AddChild(authoredLayer);
            encounterMuzzleLayerCount++;
        }
        var light = CreateTimedEffectLight(
            flash,
            "EnhancedMuzzleLight",
            RuntimeColor(presentation.MuzzleFlareColor),
            effects.MuzzleSize * 5.0f,
            effects.MuzzleLifetime);
        light.Position = Vector3.Zero;
        encounterMuzzleLightCount++;
        encounterMuzzleFlashCount++;
        var tween = CreateTween();
        tween.TweenProperty(
            flash, "scale", Vector3.Zero, effects.MuzzleLifetime);
        tween.TweenCallback(Callable.From(flash.QueueFree));
    }

    private void SpawnImpactFlash(Vector3 position)
    {
        var effects = firstEncounterEffectTextures;
        if (effects is null) return;
        var presentation = runtimeConfiguration.Presentation.FirstEncounter;
        var impact = new MeshInstance3D
        {
            Name = $"ImpactFlash_{encounterImpactCount:D3}",
            Mesh = new QuadMesh
            {
                Size = new Vector2(
                    presentation.ImpactSizeMeters,
                    presentation.ImpactSizeMeters)
            },
            MaterialOverride = CreateEffectMaterial(
                RuntimeColor(presentation.ImpactColor), effects.Flare, true)
        };
        AddChild(impact);
        impact.GlobalPosition = position;
        CreateTimedEffectLight(
            impact,
            "EnhancedImpactLight",
            RuntimeColor(presentation.ImpactColor),
            presentation.ImpactSizeMeters * 6.0f,
            presentation.ImpactLifetimeSeconds);
        encounterImpactCount++;
        encounterImpactLightCount++;
        var tween = CreateTween();
        tween.TweenProperty(
            impact, "scale", Vector3.Zero,
            presentation.ImpactLifetimeSeconds);
        tween.TweenCallback(Callable.From(impact.QueueFree));
    }

    private static OmniLight3D CreateTimedEffectLight(
        Node parent,
        string name,
        Color color,
        float range,
        float lifetime)
    {
        var light = new OmniLight3D
        {
            Name = name,
            LightColor = color,
            LightEnergy = 1.6f,
            OmniRange = Math.Max(0.1f, range),
            ShadowEnabled = false
        };
        parent.AddChild(light);
        var tween = parent.CreateTween();
        tween.TweenProperty(light, "light_energy", 0.0f, lifetime);
        return light;
    }

    private void SwitchAreaMusic(AudioStream stream, string resref)
    {
        areaMusic.Stop();
        areaMusic.Stream = stream;
        areaMusic.Play();
        currentMusicResref = resref;
        GD.Print($"NIKAMI_AURORA_MUSIC status=playing resref={resref}");
    }
}
