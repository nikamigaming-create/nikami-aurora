using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

/// <summary>
/// Decodes the layout-neutral subset of DAO's installed MMH particle graphs.
/// The decoder accepts only emitter behavior that has an explicit runtime
/// equivalent. An emitter with unknown controllers, mesh particles, physics,
/// target tracking, collision, or material semantics is omitted explicitly;
/// a graph with no supported emitter fails closed. Distortion materials are
/// counted but deliberately not rendered.
/// </summary>
public static class DragonAgeOriginsEffectGraphDecoder
{
    private const uint Children = 6999;

    public static bool TryInspectEmitterSemantics(
        string modelPathOrResRef,
        byte[] modelHierarchy,
        out IReadOnlyList<DragonAgeEffectEmitterSemanticEvidence> emitters,
        out string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPathOrResRef);
        ArgumentNullException.ThrowIfNull(modelHierarchy);
        emitters = [];
        failure = string.Empty;
        try
        {
            var resRef = Path.GetFileNameWithoutExtension(
                modelPathOrResRef.Replace('\\', '/')).ToLowerInvariant();
            var document = new Gff4Document(modelHierarchy);
            if (!document.FileType.Equals("MMH ", StringComparison.Ordinal) ||
                !document.Version.Equals("V0.1", StringComparison.Ordinal))
                throw new UnsupportedGraphException("mmh-version-unsupported");
            if (!Path.GetFileNameWithoutExtension(document.Root.String(6000))
                    .Equals(resRef, StringComparison.OrdinalIgnoreCase))
                throw new UnsupportedGraphException("mmh-resref-mismatch");
            var evidence = new List<DragonAgeEffectEmitterSemanticEvidence>();
            CollectEmitterSemanticEvidence(document, document.Root, evidence, 0);
            emitters = evidence;
            return true;
        }
        catch (UnsupportedGraphException error)
        {
            failure = error.Message;
            return false;
        }
        catch (Exception error) when (error is InvalidDataException or
                                           OverflowException or
                                           FormatException or
                                           DecoderFallbackException)
        {
            failure = "source-graph-malformed:" + error.Message;
            return false;
        }
    }

    public static bool TryInspectEmitterCount(
        string modelPathOrResRef,
        byte[] modelHierarchy,
        out int emitterCount,
        out string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPathOrResRef);
        ArgumentNullException.ThrowIfNull(modelHierarchy);
        emitterCount = 0;
        failure = string.Empty;
        try
        {
            var resRef = Path.GetFileNameWithoutExtension(
                modelPathOrResRef.Replace('\\', '/')).ToLowerInvariant();
            var document = new Gff4Document(modelHierarchy);
            if (!document.FileType.Equals("MMH ", StringComparison.Ordinal) ||
                !document.Version.Equals("V0.1", StringComparison.Ordinal))
                throw new UnsupportedGraphException("mmh-version-unsupported");
            if (!Path.GetFileNameWithoutExtension(document.Root.String(6000))
                    .Equals(resRef, StringComparison.OrdinalIgnoreCase))
                throw new UnsupportedGraphException("mmh-resref-mismatch");
            emitterCount = CountEmitters(document, document.Root, 0);
            return true;
        }
        catch (UnsupportedGraphException error)
        {
            failure = error.Message;
            return false;
        }
        catch (Exception error) when (error is InvalidDataException or
                                           OverflowException or
                                           FormatException or
                                           DecoderFallbackException)
        {
            failure = "source-graph-malformed:" + error.Message;
            return false;
        }
    }

    public static bool TryDecode(
        string modelPathOrResRef,
        byte[] modelHierarchy,
        Func<string, byte[]?> materialResolver,
        Func<string, byte[]?> textureResolver,
        out DragonAgeEffectDefinition definition,
        out string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPathOrResRef);
        ArgumentNullException.ThrowIfNull(modelHierarchy);
        ArgumentNullException.ThrowIfNull(materialResolver);
        ArgumentNullException.ThrowIfNull(textureResolver);
        definition = null!;
        failure = string.Empty;
        try
        {
            var resRef = Path.GetFileNameWithoutExtension(
                modelPathOrResRef.Replace('\\', '/')).ToLowerInvariant();
            var document = new Gff4Document(modelHierarchy);
            if (!document.FileType.Equals("MMH ", StringComparison.Ordinal) ||
                !document.Version.Equals("V0.1", StringComparison.Ordinal))
                throw new UnsupportedGraphException("mmh-version-unsupported");

            var root = document.Root;
            var sourceName = root.String(6000);
            if (!Path.GetFileNameWithoutExtension(sourceName)
                    .Equals(resRef, StringComparison.OrdinalIgnoreCase))
                throw new UnsupportedGraphException("mmh-resref-mismatch");
            var presimulate = root.OptionalSingle(6333);
            RequireFiniteNonNegative(presimulate, "presimulate-invalid");

            var emitters = new List<DragonAgeEffectEmitter>();
            var distortion = 0;
            var unsupportedEmitters = new List<string>();
            var namedTransforms = new Dictionary<string, List<Matrix4x4>>(
                StringComparer.OrdinalIgnoreCase);
            CollectNamedTransforms(document, root, Matrix4x4.Identity,
                namedTransforms, 0);
            Walk(document, root, Matrix4x4.Identity, materialResolver, textureResolver,
                namedTransforms, emitters, ref distortion, unsupportedEmitters, 0);
            if (emitters.Count == 0)
                throw new UnsupportedGraphException(unsupportedEmitters.Count > 0
                    ? "graph-has-no-supported-emitter:" + string.Join(',',
                        unsupportedEmitters.Distinct(StringComparer.Ordinal).Order())
                    : distortion > 0 ? "distortion-only-graph" : "graph-has-no-supported-emitter");
            definition = new DragonAgeEffectDefinition(
                resRef,
                Hex(SHA256.HashData(modelHierarchy)),
                presimulate,
                emitters,
                distortion,
                unsupportedEmitters);
            return true;
        }
        catch (UnsupportedGraphException error)
        {
            failure = error.Message;
            return false;
        }
        catch (Exception error) when (error is InvalidDataException or
                                           OverflowException or
                                           FormatException or
                                           DecoderFallbackException or
                                           System.Xml.XmlException)
        {
            failure = "source-graph-malformed:" + error.Message;
            return false;
        }
    }

    private static void Walk(
        Gff4Document document,
        Gff4Document.Gff4Struct node,
        Matrix4x4 parentTransform,
        Func<string, byte[]?> materialResolver,
        Func<string, byte[]?> textureResolver,
        IReadOnlyDictionary<string, List<Matrix4x4>> namedTransforms,
        ICollection<DragonAgeEffectEmitter> emitters,
        ref int distortion,
        ICollection<string> unsupportedEmitters,
        int depth)
    {
        if (depth > 64)
            throw new InvalidDataException("MMH child graph is too deep");
        var children = document.Children(node);
        var local = LocalTransform(children);
        var transform = local * parentTransform;
        foreach (var child in children)
        {
            if (child.Kind is "trsl" or "rota" or "scal") continue;
            if (child.Kind == "nemt")
            {
                DragonAgeEffectEmitter? decoded;
                bool isDistortion;
                try
                {
                    decoded = DecodeEmitter(document, child, transform,
                        materialResolver, textureResolver, namedTransforms,
                        out isDistortion);
                }
                catch (UnsupportedGraphException error)
                {
                    unsupportedEmitters.Add(error.Message);
                    continue;
                }
                if (isDistortion)
                {
                    distortion++;
                    continue;
                }
                emitters.Add(decoded!);
                continue;
            }
            Walk(document, child, transform, materialResolver, textureResolver,
                namedTransforms, emitters, ref distortion, unsupportedEmitters, depth + 1);
        }
    }

    private static int CountEmitters(Gff4Document document,
        Gff4Document.Gff4Struct node, int depth)
    {
        if (depth > 64)
            throw new InvalidDataException("MMH child graph is too deep");
        var count = 0;
        foreach (var child in document.Children(node))
        {
            if (child.Kind == "nemt")
            {
                count = checked(count + 1);
                continue;
            }
            count = checked(count + CountEmitters(document, child, depth + 1));
        }
        return count;
    }

    private static void CollectEmitterSemanticEvidence(
        Gff4Document document,
        Gff4Document.Gff4Struct node,
        ICollection<DragonAgeEffectEmitterSemanticEvidence> evidence,
        int depth)
    {
        if (depth > 64)
            throw new InvalidDataException("MMH child graph is too deep");
        foreach (var child in document.Children(node))
        {
            if (child.Kind == "nemt")
            {
                var spawn = document.Children(child).SingleOrDefault(value =>
                    value.Kind == "spnv");
                var movementDelay = child.Single(6022);
                var movementX = child.Single(6025);
                var movementY = child.Single(6026);
                var spawnX = child.Single(6023);
                var spawnY = child.Single(6024);
                var framesPerSecond = child.Single(6180);
                var radius = spawn?.Single(6286) ?? 0;
                var minimum = spawn?.Vector3(6289) ?? Vector3.Zero;
                var maximum = spawn?.Vector3(6290) ?? Vector3.Zero;
                evidence.Add(new DragonAgeEffectEmitterSemanticEvidence(
                    child.String(6000),
                    NormalizeExtension(child.String(6001), ".mao"),
                    FiniteOrNull(movementDelay), FiniteOrNull(movementX),
                    FiniteOrNull(movementY),
                    FiniteOrNull(spawnX), FiniteOrNull(spawnY),
                    FiniteOrNull(framesPerSecond), child.Byte(6182), child.Byte(6181),
                    checked((int)child.UInt32(6037)),
                    spawn is null ? null : spawn.Byte(6285),
                    spawn?.Byte(6291) == 1,
                    FiniteOrNull(radius), FormatVector(minimum), FormatVector(maximum),
                    float.IsFinite(movementDelay) && float.IsFinite(movementX) &&
                    float.IsFinite(movementY) && float.IsFinite(spawnX) &&
                    float.IsFinite(spawnY) && float.IsFinite(framesPerSecond) &&
                    float.IsFinite(radius) &&
                    Finite(minimum) && Finite(maximum)));
                continue;
            }
            CollectEmitterSemanticEvidence(document, child, evidence, depth + 1);
        }
    }

    private static float? FiniteOrNull(float value) =>
        float.IsFinite(value) ? value : null;

    private static string FormatVector(Vector3 value) =>
        string.Join(',', value.X.ToString("R", CultureInfo.InvariantCulture),
            value.Y.ToString("R", CultureInfo.InvariantCulture),
            value.Z.ToString("R", CultureInfo.InvariantCulture));

    private static void CollectNamedTransforms(Gff4Document document,
        Gff4Document.Gff4Struct node, Matrix4x4 parentTransform,
        IDictionary<string, List<Matrix4x4>> namedTransforms, int depth)
    {
        if (depth > 64)
            throw new InvalidDataException("MMH child graph is too deep");
        var children = document.Children(node);
        var transform = LocalTransform(children) * parentTransform;
        var name = node.OptionalString(6000);
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (!namedTransforms.TryGetValue(name, out var matches))
            {
                matches = [];
                namedTransforms[name] = matches;
            }
            matches.Add(transform);
        }
        foreach (var child in children)
        {
            if (child.Kind is "trsl" or "rota" or "scal") continue;
            CollectNamedTransforms(document, child, transform, namedTransforms, depth + 1);
        }
    }

    private static Matrix4x4 LocalTransform(IReadOnlyList<Gff4Document.Gff4Struct> children)
    {
        var translation = Vector3.Zero;
        var rotation = Quaternion.Identity;
        var scale = 1f;
        foreach (var child in children)
        {
            switch (child.Kind)
            {
                case "trsl":
                    translation = child.Vector3(6047);
                    break;
                case "rota":
                    rotation = child.Quaternion(6048);
                    break;
                case "scal":
                    scale = child.Single(6278);
                    if (!float.IsFinite(scale) || Math.Abs(scale - 1f) > .0001f)
                        throw new UnsupportedGraphException("node-scale-controller-unsupported");
                    break;
            }
        }
        return Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(translation);
    }

    private static DragonAgeEffectEmitter? DecodeEmitter(
        Gff4Document document,
        Gff4Document.Gff4Struct emitter,
        Matrix4x4 parentTransform,
        Func<string, byte[]?> materialResolver,
        Func<string, byte[]?> textureResolver,
        IReadOnlyDictionary<string, List<Matrix4x4>> namedTransforms,
        out bool isDistortion)
    {
        isDistortion = false;
        RequireZero(emitter.Byte(6028), "distance-birthrate-unsupported");
        RequireZero(emitter.Byte(6030), "wind-particles-unsupported");
        RequireZero(emitter.Byte(6032), "path-following-unsupported");
        RequireZero(emitter.Byte(6033), "linked-particles-unsupported");
        RequireZero(emitter.Byte(6035), "particle-collision-unsupported");
        RequireZero(emitter.Byte(6036), "velocity-inheritance-unsupported");
        RequireZero(emitter.Byte(6188), "target-kill-unsupported");
        RequireZero(emitter.Byte(6239), "physics-emitter-unsupported");
        RequireZero(emitter.Byte(6243), "physics-object-emitter-unsupported");
        RequireZero(emitter.Byte(6321), "particle-splat-unsupported");
        if (emitter.Byte(6298) > 1)
            throw new UnsupportedGraphException("acceleration-space-invalid");
        if (emitter.Byte(6234) != 0)
            throw new UnsupportedGraphException("mesh-emitter-type-unsupported");
        if (!string.IsNullOrWhiteSpace(emitter.OptionalString(6284)))
            throw new UnsupportedGraphException("mesh-particle-unsupported");
        RequireZero(emitter.Single(6022), "movement-spread-update-unsupported");
        RequireZero(emitter.Single(6021), "particle-rotation-acceleration-unsupported");
        RequireZero(emitter.Single(6025), "movement-spread-x-unsupported");
        RequireZero(emitter.Single(6026), "movement-spread-y-unsupported");

        var materialObject = NormalizeExtension(emitter.String(6001), ".mao");
        var materialBytes = materialResolver(materialObject) ??
                            throw new UnsupportedGraphException("material-object-absent");
        var material = DecodeMaterial(materialObject, materialBytes);
        if (material.Distortion)
        {
            isDistortion = true;
            return null;
        }
        var textureBytes = textureResolver(material.Texture) ??
                           throw new UnsupportedGraphException("diffuse-texture-absent");

        var orientation = emitter.UInt32(6037) switch
        {
            0 or 3 => DragonAgeEffectOrientation.CameraBillboard,
            1 => DragonAgeEffectOrientation.HorizontalPlane,
            _ => throw new UnsupportedGraphException("orientation-behavior-unsupported")
        };
        var sourceChildren = document.Children(emitter);
        var emitterTransform = LocalTransform(sourceChildren) * parentTransform;
        if (!Matrix4x4.Decompose(emitterTransform, out var sourceScale,
                out var sourceRotation, out var sourceTranslation) ||
            Vector3.Distance(sourceScale, Vector3.One) > .0001f)
            throw new UnsupportedGraphException("emitter-transform-unsupported");
        var sourceDirection = DecodeSourceDirection(emitter, emitterTransform,
            sourceRotation, namedTransforms);

        var spawn = sourceChildren.SingleOrDefault(child => child.Kind == "spnv");
        var (volume, extents) = DecodeSpawnVolume(spawn);
        var ageMap = DecodeAgeMap(document, sourceChildren);
        var (scaleAspect, independentScaleAxes) = DecodeScaleContract(ageMap);
        var middleIndex = ageMap.Count / 2;
        var start = ageMap[0];
        var middle = ageMap[middleIndex];
        var end = ageMap[^1];
        var spreadX = emitter.Single(6023);
        var spreadY = emitter.Single(6024);
        RequireFiniteNonNegative(spreadX, "spawn-spread-invalid");
        RequireFiniteNonNegative(spreadY, "spawn-spread-invalid");
        if (Math.Abs(spreadX - spreadY) > .0001f)
            throw new UnsupportedGraphException("asymmetric-spawn-spread-unsupported");

        var birthRate = emitter.Single(6011);
        var birthRateRange = emitter.Single(6012);
        var lifetime = emitter.Single(6013);
        var lifetimeRange = emitter.Single(6014);
        var velocity = emitter.Single(6016);
        var velocityRange = emitter.Single(6017);
        var acceleration = emitter.Single(6018);
        var gravity = emitter.Single(6031);
        var worldAcceleration = emitter.Vector3(6294);
        var rotations = new[]
        {
            emitter.Single(6299), emitter.Single(6300), emitter.Single(6019),
            emitter.Single(6020), emitter.Single(6021)
        };
        foreach (var (value, reason) in new[]
                 {
                     (birthRate, "birth-rate-invalid"),
                     (birthRateRange, "birth-rate-range-invalid"),
                     (lifetime, "lifetime-invalid"),
                     (lifetimeRange, "lifetime-range-invalid"),
                     (velocity, "velocity-invalid"),
                     (velocityRange, "velocity-range-invalid")
                 })
            RequireFiniteNonNegative(value, reason);
        if (birthRate <= 0 || lifetime <= 0)
            throw new UnsupportedGraphException("emitter-timing-empty");
        if (!float.IsFinite(acceleration) || !float.IsFinite(gravity) ||
            !Finite(worldAcceleration) || rotations.Any(value => !float.IsFinite(value)))
            throw new UnsupportedGraphException("emitter-acceleration-invalid");
        var columns = emitter.Byte(6182);
        var rows = emitter.Byte(6181);
        var framesPerSecond = emitter.Single(6180);
        var explicitlyStaticContactSheet = material.ContactSheet &&
            columns == 0 && rows == 0 && framesPerSecond == 0;
        if ((!material.ContactSheet && columns == 0 && rows == 0) ||
            explicitlyStaticContactSheet)
            columns = rows = 1;
        if (columns == 0 || rows == 0 || !float.IsFinite(framesPerSecond) ||
            framesPerSecond < 0 || material.ContactSheet && columns * rows <= 1 &&
            !explicitlyStaticContactSheet)
            throw new UnsupportedGraphException("flipbook-contract-invalid");

        return new DragonAgeEffectEmitter(
            emitter.String(6000), materialObject, Hex(SHA256.HashData(materialBytes)),
            material.Texture, Hex(SHA256.HashData(textureBytes)), material.Blend,
            orientation, birthRate, birthRateRange, lifetime, lifetimeRange,
            columns, rows, framesPerSecond, sourceTranslation, velocity,
            velocityRange, acceleration, gravity, worldAcceleration, spreadX,
            volume, extents,
            MaximumScale(start), MaximumScale(middle), MaximumScale(end),
            start.Color, middle.Color, end.Color, middle.Time, sourceDirection,
            Quaternion.Normalize(sourceRotation), ageMap, emitter.Single(6015),
            emitter.Single(6299), emitter.Single(6300), emitter.Single(6019),
            emitter.Single(6020), emitter.Single(6021), emitter.Byte(6298) == 1,
            scaleAspect, independentScaleAxes);
    }

    private static Vector3 DecodeSourceDirection(Gff4Document.Gff4Struct emitter,
        Matrix4x4 emitterTransform, Quaternion emitterRotation,
        IReadOnlyDictionary<string, List<Matrix4x4>> namedTransforms)
    {
        var targetName = emitter.OptionalString(6184) ?? string.Empty;
        var tracksTarget = emitter.Byte(6187);
        var attraction = emitter.Single(6185);
        var radius = emitter.Single(6186);
        if (!float.IsFinite(attraction) || !float.IsFinite(radius) || radius < 0)
            throw new UnsupportedGraphException("emitter-target-invalid");
        if (Math.Abs(attraction) > .0001f || radius > .0001f)
            throw new UnsupportedGraphException("emitter-target-attraction-unsupported");
        if (tracksTarget == 0)
            return Vector3.UnitZ;
        if (tracksTarget != 1 || string.IsNullOrWhiteSpace(targetName) ||
            !namedTransforms.TryGetValue(targetName, out var matches) || matches.Count != 1)
            throw new UnsupportedGraphException("target-tracking-contract-unavailable");
        var sourcePosition = emitterTransform.Translation;
        var directionInRoot = matches[0].Translation - sourcePosition;
        if (!Finite(directionInRoot) || directionInRoot.LengthSquared() < .000001f)
            throw new UnsupportedGraphException("target-direction-invalid");
        var inverseRotation = Matrix4x4.CreateFromQuaternion(
            Quaternion.Inverse(Quaternion.Normalize(emitterRotation)));
        var local = Vector3.TransformNormal(directionInRoot, inverseRotation);
        return Vector3.Normalize(local);
    }

    private static IReadOnlyList<DragonAgeEffectAgeKey> DecodeAgeMap(
        Gff4Document document, IReadOnlyList<Gff4Document.Gff4Struct> children)
    {
        var map = children.SingleOrDefault(child => child.Kind == "amap") ??
                  throw new UnsupportedGraphException("age-map-absent");
        var elements = document.Children(map)
            .Where(child => child.Kind == "amel")
            .Select(child => new DragonAgeEffectAgeKey(
                child.Single(6040),
                new Vector2(child.Single(6041), child.Single(6042)),
                child.Vector4(6043)))
            .ToArray();
        if (elements.Length < 2 || elements.Length != map.UInt32(6039))
            throw new UnsupportedGraphException("age-map-count-invalid");
        var previous = -1f;
        foreach (var element in elements)
        {
            if (!float.IsFinite(element.Time) || element.Time < 0 || element.Time > 1 ||
                element.Time < previous || !Finite(element.Scale) ||
                element.Scale.X < 0 || element.Scale.Y < 0 || !Finite(element.Color))
                throw new UnsupportedGraphException("age-map-value-invalid");
            previous = element.Time;
        }
        return elements;
    }

    private static (Vector2? ConstantAspect, bool IndependentAxes) DecodeScaleContract(
        IReadOnlyList<DragonAgeEffectAgeKey> ageMap)
    {
        Vector2? aspect = null;
        var independent = false;
        var xHasZero = false;
        var xHasPositive = false;
        var yHasZero = false;
        var yHasPositive = false;
        foreach (var key in ageMap)
        {
            var maximum = MaximumScale(key);
            if (maximum <= .000001f)
                throw new UnsupportedGraphException("age-map-scale-empty-unsupported");
            xHasZero |= key.Scale.X <= .000001f;
            xHasPositive |= key.Scale.X > .000001f;
            yHasZero |= key.Scale.Y <= .000001f;
            yHasPositive |= key.Scale.Y > .000001f;
            var normalized = key.Scale / maximum;
            if (aspect is null)
            {
                aspect = normalized;
                continue;
            }
            if (Vector2.Distance(aspect.Value, normalized) > .0001f)
                independent = true;
        }
        if (xHasZero && xHasPositive || yHasZero && yHasPositive)
            throw new UnsupportedGraphException(
                "age-map-scale-zero-crossing-unsupported");
        if (!xHasPositive || !yHasPositive)
            throw new UnsupportedGraphException("age-map-scale-axis-empty-unsupported");
        return independent ? (null, true) : (aspect, false);
    }

    private static (DragonAgeEffectVolume Volume, Vector3 Extents) DecodeSpawnVolume(
        Gff4Document.Gff4Struct? spawn)
    {
        if (spawn is null) return (DragonAgeEffectVolume.Point, Vector3.Zero);
        RequireZero(spawn.Byte(6046), "inverted-spawn-volume-unsupported");
        RequireZero(spawn.Byte(6291), "normal-directed-spawn-unsupported");
        return spawn.Byte(6285) switch
        {
            0 => (DragonAgeEffectVolume.Point, Vector3.Zero),
            2 => (DragonAgeEffectVolume.Sphere,
                Vector3.One * RequirePositive(spawn.Single(6286), "sphere-radius-invalid")),
            3 => (DragonAgeEffectVolume.Box,
                BoxExtents(spawn.Vector3(6289), spawn.Vector3(6290))),
            _ => throw new UnsupportedGraphException("spawn-volume-unsupported")
        };
    }

    private static Vector3 BoxExtents(Vector3 minimum, Vector3 maximum)
    {
        if (!Finite(minimum) || !Finite(maximum) ||
            minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z)
            throw new UnsupportedGraphException("spawn-box-invalid");
        return Vector3.Max(Vector3.Abs(minimum), Vector3.Abs(maximum));
    }

    private static MaterialContract DecodeMaterial(string member, byte[] payload)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(payload),
            LoadOptions.None);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "MaterialObject")
            throw new UnsupportedGraphException("material-object-format-unsupported");
        var semantic = root.Elements().SingleOrDefault(element =>
            element.Name.LocalName == "DefaultSemantic")?
            .Attribute("Name")?.Value;
        if (semantic is null)
            throw new UnsupportedGraphException("material-semantic-absent");
        var material = root.Elements().SingleOrDefault(element =>
            element.Name.LocalName == "Material")?
            .Attribute("Name")?.Value;
        if (material == "DADistortionMask.mat")
        {
            if (semantic is not ("Particle" or "Particle_CS"))
                throw new UnsupportedGraphException(
                    "distortion-material-semantic-unsupported");
            var distortionTextures = root.Elements().Where(element =>
                    element.Name.LocalName == "Texture").ToArray();
            var distortion = distortionTextures.Where(element =>
                element.Attribute("Name")?.Value == "mml_tDistortion").ToArray();
            var modifiers = distortionTextures.Where(element =>
                element.Attribute("Name")?.Value == "mml_tDistortionModifiers").ToArray();
            if (distortionTextures.Length != 2 || distortion.Length != 1 ||
                modifiers.Length != 1 ||
                string.IsNullOrWhiteSpace(distortion[0].Attribute("ResName")?.Value) ||
                string.IsNullOrWhiteSpace(modifiers[0].Attribute("ResName")?.Value))
                throw new UnsupportedGraphException(
                    "distortion-texture-contract-unsupported");
            return new MaterialContract(string.Empty, DragonAgeEffectBlend.Alpha,
                true, semantic == "Particle_CS");
        }
        if (semantic is "Distortion" or "Distortionv")
            return new MaterialContract(string.Empty, DragonAgeEffectBlend.Alpha, true, false);
        var contactSheet = semantic.StartsWith("ContactSheet", StringComparison.Ordinal);
        var blend = semantic switch
        {
            "Add" or "Addv" or "ContactSheetAdd" or "ContactSheetAddv" =>
                DragonAgeEffectBlend.Additive,
            "VolTexAdd" or "VolTexAddv" => DragonAgeEffectBlend.Additive,
            "Blend" or "Blendv" or "ContactSheetBlend" or "ContactSheetBlendv" =>
                DragonAgeEffectBlend.Alpha,
            "VolTexBlend" or "VolTexBlendv" => DragonAgeEffectBlend.Alpha,
            _ => throw new UnsupportedGraphException(
                "material-semantic-unsupported:" + semantic.ToLowerInvariant())
        };
        var textures = root.Elements().Where(element =>
            element.Name.LocalName == "Texture" &&
            element.Attribute("Name")?.Value == "mml_tDiffuse").ToArray();
        if (textures.Length != 1)
            throw new UnsupportedGraphException("diffuse-texture-contract-unsupported");
        var texture = textures[0].Attribute("ResName")?.Value;
        if (string.IsNullOrWhiteSpace(texture))
            throw new UnsupportedGraphException("diffuse-texture-absent");
        return new MaterialContract(NormalizeExtension(texture, ".dds"), blend, false,
            contactSheet);
    }

    private static float MaximumScale(DragonAgeEffectAgeKey key) =>
        Math.Max(key.Scale.X, key.Scale.Y);

    private static string NormalizeExtension(string value, string extension)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? trimmed.ToLowerInvariant()
            : (trimmed + extension).ToLowerInvariant();
    }

    private static void RequireZero(byte value, string reason)
    {
        if (value != 0) throw new UnsupportedGraphException(reason);
    }

    private static void RequireZero(float value, string reason)
    {
        if (!float.IsFinite(value) || Math.Abs(value) > .0001f)
            throw new UnsupportedGraphException(reason);
    }

    private static void RequireFiniteNonNegative(float value, string reason)
    {
        if (!float.IsFinite(value) || value < 0)
            throw new UnsupportedGraphException(reason);
    }

    private static float RequirePositive(float value, string reason)
    {
        if (!float.IsFinite(value) || value <= 0)
            throw new UnsupportedGraphException(reason);
        return value;
    }

    private static bool Finite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    private sealed record MaterialContract(
        string Texture, DragonAgeEffectBlend Blend, bool Distortion, bool ContactSheet);

    private sealed class UnsupportedGraphException(string message) : Exception(message);

    private sealed class Gff4Document
    {
        private const ushort ListFlag = 0x8000;
        private const ushort ReferenceFlag = 0x2000;
        private const ushort StructFlag = 0x4000;
        private readonly byte[] data;
        private readonly int dataStart;
        private readonly Gff4StructDefinition[] structures;

        public Gff4Document(byte[] data)
        {
            this.data = data;
            if (data.Length < 28 || Encoding.ASCII.GetString(data, 0, 12) != "GFF V4.0PC  ")
                throw new InvalidDataException("PC GFF V4.0 header required");
            FileType = Encoding.ASCII.GetString(data, 12, 4);
            Version = Encoding.ASCII.GetString(data, 16, 4);
            var count = UInt32(20);
            dataStart = checked((int)UInt32(24));
            if (count == 0 || count > 4096 || dataStart < 28 + count * 16 ||
                dataStart > data.Length)
                throw new InvalidDataException("GFF4 structure table is invalid");
            structures = new Gff4StructDefinition[count];
            for (var index = 0; index < count; index++)
            {
                var at = checked(28 + index * 16);
                var kind = Encoding.ASCII.GetString(data, at, 4).ToLowerInvariant();
                var fieldCount = UInt32(at + 4);
                var fieldOffset = UInt32(at + 8);
                var size = UInt32(at + 12);
                if (fieldCount > 4096 || fieldOffset > data.Length ||
                    fieldCount * 12L > data.Length - fieldOffset || size > data.Length)
                    throw new InvalidDataException("GFF4 structure definition is invalid");
                var fields = new Dictionary<uint, Gff4Field>();
                for (var ordinal = 0; ordinal < fieldCount; ordinal++)
                {
                    var fieldAt = checked((int)fieldOffset + ordinal * 12);
                    var field = new Gff4Field(UInt32(fieldAt), UInt16(fieldAt + 4),
                        UInt16(fieldAt + 6), checked((int)UInt32(fieldAt + 8)));
                    if (!fields.TryAdd(field.Label, field))
                        throw new InvalidDataException("GFF4 duplicate field label");
                }
                structures[index] = new Gff4StructDefinition(kind, checked((int)size), fields);
            }
        }

        public string FileType { get; }
        public string Version { get; }
        public Gff4Struct Root => new(this, 0, 0);

        public IReadOnlyList<Gff4Struct> Children(Gff4Struct owner)
        {
            if (!owner.Definition.Fields.TryGetValue(
                    DragonAgeOriginsEffectGraphDecoder.Children, out var field)) return [];
            if ((field.Flags & ListFlag) == 0 || (field.Flags & ReferenceFlag) == 0 ||
                field.Type != ushort.MaxValue)
                throw new InvalidDataException("GFF4 heterogeneous child list is invalid");
            var relative = owner.Int32At(field.Offset);
            if (relative < 0) return [];
            var at = DataOffset(relative);
            var count = UInt32(at);
            if (count > 1_000_000 || 4L + count * 8L > data.Length - at)
                throw new InvalidDataException("GFF4 child list is invalid");
            var result = new List<Gff4Struct>(checked((int)count));
            for (var index = 0; index < count; index++)
            {
                var itemAt = checked(at + 4 + index * 8);
                var typeAndFlags = UInt32(itemAt);
                var type = checked((ushort)(typeAndFlags & 0xffff));
                var flags = checked((ushort)(typeAndFlags >> 16));
                var itemRelative = checked((int)UInt32(itemAt + 4));
                if ((flags & StructFlag) == 0 || type >= structures.Length)
                    throw new InvalidDataException("GFF4 child is not a structure reference");
                _ = DataOffset(itemRelative, structures[type].Size);
                result.Add(new Gff4Struct(this, type, itemRelative));
            }
            return result;
        }

        private int DataOffset(int relative, int length = 4)
        {
            var at = checked(dataStart + relative);
            if (relative < 0 || at < dataStart || length < 0 || at > data.Length - length)
                throw new InvalidDataException("GFF4 data reference is outside the payload");
            return at;
        }

        private ushort UInt16(int offset)
        {
            if (offset < 0 || offset > data.Length - 2)
                throw new InvalidDataException("GFF4 uint16 is outside the payload");
            return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        }

        private uint UInt32(int offset)
        {
            if (offset < 0 || offset > data.Length - 4)
                throw new InvalidDataException("GFF4 uint32 is outside the payload");
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        }

        internal sealed record Gff4Field(uint Label, ushort Type, ushort Flags, int Offset);
        internal sealed record Gff4StructDefinition(
            string Kind, int Size, IReadOnlyDictionary<uint, Gff4Field> Fields);

        public sealed class Gff4Struct
        {
            private readonly Gff4Document document;
            private readonly int baseOffset;

            internal Gff4Struct(Gff4Document document, int type, int baseOffset)
            {
                this.document = document;
                Type = type;
                this.baseOffset = baseOffset;
            }

            private int Type { get; }
            internal Gff4StructDefinition Definition => document.structures[Type];
            public string Kind => Definition.Kind;

            public byte Byte(uint label) => ReadScalar(label, 0, 1)[0];
            public uint UInt32(uint label) =>
                BinaryPrimitives.ReadUInt32LittleEndian(ReadScalar(label, 4, 4));
            public float Single(uint label) =>
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                    ReadScalar(label, 8, 4)));
            public float OptionalSingle(uint label, float fallback = 0)
            {
                if (!Definition.Fields.ContainsKey(label)) return fallback;
                return Single(label);
            }
            public Vector3 Vector3(uint label)
            {
                var value = ReadScalar(label, null, 12);
                return new Vector3(ReadSingle(value, 0), ReadSingle(value, 4),
                    ReadSingle(value, 8));
            }
            public Vector4 Vector4(uint label)
            {
                var value = ReadScalar(label, null, 16);
                return new Vector4(ReadSingle(value, 0), ReadSingle(value, 4),
                    ReadSingle(value, 8), ReadSingle(value, 12));
            }
            public Quaternion Quaternion(uint label)
            {
                var value = Vector4(label);
                var result = new Quaternion(value.X, value.Y, value.Z, value.W);
                if (!Finite(value) || result.LengthSquared() < .000001f)
                    throw new InvalidDataException("GFF4 quaternion is invalid");
                return System.Numerics.Quaternion.Normalize(result);
            }
            public string String(uint label) => OptionalString(label) ??
                throw new InvalidDataException($"GFF4 string field {label} is absent");
            public string? OptionalString(uint label)
            {
                if (!Definition.Fields.TryGetValue(label, out var field)) return null;
                if (field.Type != 14 || field.Flags != 0)
                    throw new InvalidDataException($"GFF4 field {label} is not an ECString");
                var relative = Int32At(field.Offset);
                if (relative < 0) return string.Empty;
                var at = document.DataOffset(relative);
                var characters = document.UInt32(at);
                if (characters > 1_000_000 || characters * 2L > document.data.Length - at - 4)
                    throw new InvalidDataException("GFF4 string is outside the payload");
                return Encoding.Unicode.GetString(document.data, at + 4,
                    checked((int)characters * 2)).TrimEnd('\0');
            }

            internal int Int32At(int fieldOffset)
            {
                var at = document.DataOffset(checked(baseOffset + fieldOffset));
                return BinaryPrimitives.ReadInt32LittleEndian(document.data.AsSpan(at, 4));
            }

            private ReadOnlySpan<byte> ReadScalar(uint label, ushort? expectedType, int size)
            {
                if (!Definition.Fields.TryGetValue(label, out var field) || field.Flags != 0 ||
                    expectedType.HasValue && field.Type != expectedType.Value)
                    throw new InvalidDataException($"GFF4 scalar field {label} is absent or typed differently");
                var at = document.DataOffset(checked(baseOffset + field.Offset), size);
                return document.data.AsSpan(at, size);
            }

            private static float ReadSingle(ReadOnlySpan<byte> data, int offset) =>
                BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)));
        }
    }
}
