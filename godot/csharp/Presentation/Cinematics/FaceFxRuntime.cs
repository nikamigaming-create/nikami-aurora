using System.Text.Json;
using Godot;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

namespace Nikami.Aurora.GodotRuntime.Presentation.Cinematics;

/// <summary>
/// Evaluates the installed FaceFX 1.7 data directly. The source assets are decoded once into
/// immutable records; frame updates perform no JSON traversal and allocate no per-node objects.
/// </summary>
internal sealed class FaceFxRuntime
{
    private const float PositionScale = 0.01f;
    private const float Epsilon = 1.1920929e-7f;
    private const float ImportedBasisMinRotationAgreement = 0.999f;
    private const string SharedCurvesPath = "res://assets/generated/cutscenes/arl100cs_sunset/facefx-curves.json";
    private const string SharedActorsPath = "res://assets/generated/cutscenes/arl100cs_sunset/facefx-actors.json";
    private const string ProofPath = "res://assets/generated/cutscenes/arl100cs_sunset/facefx-graph-proof.json";
    private const string ShaderPath = "res://shaders/dao_facefx_material.gdshader";
    private const string EnhancedShaderPath = "res://shaders/dao_facefx_material_enhanced.gdshader";
    private const string EmotionRoot = "res://assets/generated/cutscenes/arl100cs_sunset/emotions/";

    private readonly Dictionary<string, FaceAnimation> animations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FaceActor> actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SpeakerBinding> speakers = new(StringComparer.OrdinalIgnoreCase);
    private ActiveLine? active;

    internal string FailureReason { get; private set; } = string.Empty;
    internal int AnimationCount => animations.Count;
    internal int SpeakerCount => speakers.Count;
    internal int OracleNodeChecks { get; private set; }
    internal int OracleBoneChecks { get; private set; }
    internal bool IsRunning => active is not null;

    internal bool Load(string curvesPath = SharedCurvesPath, string actorsPath = SharedActorsPath)
    {
        try
        {
            animations.Clear();
            actors.Clear();
            speakers.Clear();
            LoadCurveSet(SharedCurvesPath);
            if (!curvesPath.Equals(SharedCurvesPath, StringComparison.OrdinalIgnoreCase))
                LoadCurveSet(curvesPath);

            using var actorDocument = JsonDocument.Parse(File.ReadAllText(Global(actorsPath)));
            var actorRoot = actorDocument.RootElement;
            Require(actorRoot.GetProperty("schema").GetString() == "opendao-facefx-actors-v1",
                "facefx-actors-invalid");
            foreach (var value in actorRoot.GetProperty("actors").EnumerateArray())
            {
                var actor = ParseActor(value);
                Require(actors.TryAdd(actor.Name, actor), "facefx-actor-duplicate");
            }

            Require(animations.Count > 0 && actors.Count > 0, "facefx-data-empty");
            OracleNodeChecks = ValidateOracle();
            FailureReason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            FailureReason = exception is InvalidDataException ? exception.Message : "facefx-load:" + exception.Message;
            return false;
        }
    }

    private void LoadCurveSet(string path)
    {
        using var curveDocument = JsonDocument.Parse(File.ReadAllText(Global(path)));
        var curveRoot = curveDocument.RootElement;
        Require(curveRoot.GetProperty("schema").GetString() == "opendao-facefx-set-v1",
            "facefx-curves-invalid");
        Require(curveRoot.GetProperty("curveEvaluation").GetString() ==
                "cubic-hermite-fixed-tangent-length", "facefx-curve-evaluation-invalid");
        foreach (var value in curveRoot.GetProperty("animations").EnumerateArray())
        {
            var animation = ParseAnimation(value);
            Require(animations.TryAdd(animation.Name, animation), "facefx-animation-duplicate");
        }
    }

    private int ValidateOracle()
    {
        using var proofDocument = JsonDocument.Parse(File.ReadAllText(Global(ProofPath)));
        var root = proofDocument.RootElement;
        Require(root.GetProperty("schema").GetString() == "opendao-facefx-graph-proof-v1",
            "facefx-proof-invalid");
        var checks = 0;
        foreach (var sample in root.GetProperty("samples").EnumerateArray())
        {
            var actorName = sample.GetProperty("actor").GetString() ?? string.Empty;
            var animationName = sample.GetProperty("animation").GetString() ?? string.Empty;
            if (!actors.TryGetValue(actorName, out var actor) ||
                !animations.TryGetValue(animationName, out var animation))
                throw Invalid("facefx-proof-source-missing");
            var actual = new float[actor.Nodes.Length];
            EvaluateGraph(animation, actor, sample.GetProperty("time").GetDouble(), actual);
            var expected = sample.GetProperty("nodeValues").EnumerateArray().ToArray();
            Require(expected.Length == actual.Length, "facefx-proof-node-count");
            for (var index = 0; index < actual.Length; index++)
            {
                Require(Math.Abs(actual[index] - expected[index].GetDouble()) <= 0.00001,
                    $"facefx-proof-node-mismatch:{animationName}:{index}");
                checks++;
            }
        }
        return checks;
    }

    internal bool BindSpeaker(string resref, Node root, string actorName = "humanmale")
    {
        if (!actors.TryGetValue(actorName, out var actor))
            return Fail("facefx-actor-graph-missing:" + actorName);
        var skeleton = FindDescendant<Skeleton3D>(root);
        if (skeleton is null) return Fail("facefx-speaker-skeleton-missing:" + resref);
        var material = InstallEmotionMaterial(root);
        if (material is null) return Fail("facefx-emotion-material-missing:" + resref);

        var boneIndices = new int[actor.Bones.Length];
        for (var index = 0; index < actor.Bones.Length; index++)
        {
            boneIndices[index] = skeleton.FindBone(actor.Bones[index].Name);
            if (boneIndices[index] < 0)
                return Fail("facefx-speaker-bone-missing:" + actor.Bones[index].Name);
        }
        if (!ValidateImportedBasis(resref, skeleton, actor, boneIndices))
            return Fail("facefx-speaker-basis-mismatch:" + resref);

        speakers[resref] = new SpeakerBinding(skeleton, material, actor, boneIndices);
        return true;
    }

    private static bool ValidateImportedBasis(string resref, Skeleton3D skeleton,
        FaceActor actor, int[] boneIndices)
    {
        var checks = 0;
        var maximumPositionError = 0f;
        var minimumRotationAgreement = 1f;
        var passed = true;
        foreach (var name in new[] { "mouthBase", "eye_Left", "eye_Right" })
        {
            var index = Array.FindIndex(actor.Bones, bone => bone.Name == name);
            if (index < 0) return false;
            var source = actor.Bones[index].Rest;
            var target = skeleton.GetBoneRest(boneIndices[index]);
            var positionError = target.Origin.DistanceTo(ConvertPosition(source.Position));
            var targetRotation = target.Basis.GetRotationQuaternion().Normalized();
            var rotationAgreement = MathF.Abs(targetRotation.Dot(ConvertRotation(source.Rotation)));
            maximumPositionError = MathF.Max(maximumPositionError, positionError);
            minimumRotationAgreement = MathF.Min(minimumRotationAgreement, rotationAgreement);
            // Actor morphs deliberately change facial-bone rest positions. FaceFX
            // deltas are applied relative to the imported actor's own base pose, so
            // absolute rest-position drift is useful telemetry but not a basis test.
            passed &= target.Origin.IsFinite() && targetRotation.IsFinite() &&
                      rotationAgreement >= ImportedBasisMinRotationAgreement;
            checks++;
        }
        GD.Print($"OPENDAO_FACEFX_BASIS status={(passed ? "ready" : "fail")} " +
                 $"speaker={resref} actor={actor.Name} mapping=XZY-reflected checks={checks} " +
                 $"max_morph_rest_offset={maximumPositionError:0.####} " +
                 $"min_rotation_agreement={minimumRotationAgreement:0.######} " +
                 $"rotation_limit={ImportedBasisMinRotationAgreement:0.######}");
        return passed;
    }

    internal bool StartLine(string stringRef, string speakerResref)
    {
        Stop();
        if (!animations.TryGetValue(stringRef + "_m", out var animation))
            return Fail("facefx-animation-missing:" + stringRef + "_m");
        if (!speakers.TryGetValue(speakerResref, out var speaker))
            return Fail("facefx-speaker-unbound:" + speakerResref);

        var basePoses = new BonePose[speaker.Actor.Bones.Length];
        for (var index = 0; index < basePoses.Length; index++)
        {
            var bone = speaker.BoneIndices[index];
            basePoses[index] = new BonePose(speaker.Skeleton.GetBonePosePosition(bone),
                speaker.Skeleton.GetBonePoseRotation(bone), speaker.Skeleton.GetBonePoseScale(bone));
        }

        active = new ActiveLine(animation, speaker, basePoses, new float[speaker.Actor.Nodes.Length]);
        speaker.Material.SetShaderParameter("mml_fEmotionsMapWeight0", Vector4.Zero);
        speaker.Material.SetShaderParameter("mml_fEmotionsMapWeight1", Vector4.Zero);
        FailureReason = string.Empty;
        return true;
    }

    internal bool ValidateBoundPoses()
    {
        var lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["376570_m"] = "arl101cr_cutscene_militia_1",
            ["376571_m"] = "arl101cr_cutscene_militia_2",
            ["376572_m"] = "arl101cr_cutscene_militia_3"
        };
        var proofBones = new HashSet<string>(["LipCorner_left", "jawBone", "brow_left", "Head"],
            StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Global(ProofPath)));
            OracleBoneChecks = 0;
            foreach (var (animationName, speakerResref) in lines)
            {
                Require(StartLine(animationName[..^2], speakerResref), FailureReason);
                var line = active ?? throw Invalid("facefx-oracle-line-not-active");
                var boneByName = line.Speaker.Actor.Bones
                    .Select((bone, index) => (bone, index))
                    .ToDictionary(value => value.bone.Name, value => value, StringComparer.Ordinal);
                foreach (var sample in document.RootElement.GetProperty("samples").EnumerateArray())
                {
                    if (sample.GetProperty("actor").GetString() != "humanmale" ||
                        sample.GetProperty("animation").GetString() != animationName) continue;
                    Require(Advance(sample.GetProperty("time").GetDouble()), FailureReason);
                    foreach (var expected in sample.GetProperty("bones").EnumerateArray())
                    {
                        var name = expected.GetProperty("name").GetString() ?? string.Empty;
                        if (!proofBones.Contains(name)) continue;
                        Require(boneByName.TryGetValue(name, out var indexed),
                            "facefx-proof-bone-missing:" + name);
                        var skeletonIndex = line.Speaker.BoneIndices[indexed.index];
                        var basis = line.BasePoses[indexed.index];
                        var expectedPosition = basis.Position + ConvertPosition(
                            ReadVector(expected.GetProperty("position")) - indexed.bone.Rest.Position);
                        var expectedFinalRotation = ReadQuaternion(expected.GetProperty("rotationWXYZ"));
                        var expectedRotation = (basis.Rotation * ConvertRotation(
                            indexed.bone.Rest.Rotation.Inverse() * expectedFinalRotation)).Normalized();
                        var expectedScale = basis.Scale + ConvertScale(
                            ReadVector(expected.GetProperty("scale")) - indexed.bone.Rest.Scale);
                        var actualRotation = line.Speaker.Skeleton.GetBonePoseRotation(skeletonIndex);
                        Require(line.Speaker.Skeleton.GetBonePosePosition(skeletonIndex)
                                    .DistanceTo(expectedPosition) < 0.00001f &&
                                MathF.Abs(MathF.Abs(actualRotation.Dot(expectedRotation)) - 1) < 0.00001f &&
                                line.Speaker.Skeleton.GetBonePoseScale(skeletonIndex)
                                    .DistanceTo(expectedScale) < 0.00001f,
                            $"facefx-proof-bone-mismatch:{animationName}:{name}");
                        OracleBoneChecks++;
                    }
                }
                Stop();
            }
            Require(OracleBoneChecks == 40, "facefx-proof-bone-check-count");
            return true;
        }
        catch (Exception exception)
        {
            Stop();
            FailureReason = exception is InvalidDataException ? exception.Message :
                "facefx-proof-bones:" + exception.Message;
            return false;
        }
    }

    internal bool Advance(double localTime)
    {
        if (active is null) return true;
        try
        {
            EvaluateGraph(active.Animation, active.Speaker.Actor, localTime, active.NodeValues);
            ApplyBones(active);
            ApplyMaterials(active);
            return true;
        }
        catch (Exception exception)
        {
            var reason = exception is InvalidDataException ? exception.Message : "facefx-advance:" + exception.Message;
            Stop();
            return Fail(reason);
        }
    }

    internal void Stop()
    {
        if (active is null) return;
        var speaker = active.Speaker;
        if (GodotObject.IsInstanceValid(speaker.Skeleton))
            for (var index = 0; index < active.BasePoses.Length; index++)
            {
                var bone = speaker.BoneIndices[index];
                var pose = active.BasePoses[index];
                speaker.Skeleton.SetBonePosePosition(bone, pose.Position);
                speaker.Skeleton.SetBonePoseRotation(bone, pose.Rotation);
                speaker.Skeleton.SetBonePoseScale(bone, pose.Scale);
            }
        if (GodotObject.IsInstanceValid(speaker.Material))
        {
            speaker.Material.SetShaderParameter("mml_fEmotionsMapWeight0", Vector4.Zero);
            speaker.Material.SetShaderParameter("mml_fEmotionsMapWeight1", Vector4.Zero);
        }
        active = null;
    }

    private static void EvaluateGraph(FaceAnimation animation, FaceActor actor, double time, float[] values)
    {
        Array.Clear(values);
        foreach (var curve in animation.Curves)
            if (actor.NodeByName.TryGetValue(curve.Name, out var index))
                values[index] += Sample(curve, time);

        for (var index = 0; index < actor.Nodes.Length; index++)
        {
            var node = actor.Nodes[index];
            var value = node.Links.Length == 0 ? 0f : node.Operation switch
            {
                "sum" => 0f,
                "multiply" => 1f,
                "max" => float.NegativeInfinity,
                "min" => float.PositiveInfinity,
                _ => throw Invalid("facefx-input-operation-invalid:" + node.Operation)
            };
            var correction = 0f;
            foreach (var link in node.Links)
            {
                Require(link.SourceNode < index, "facefx-graph-not-topological");
                var source = values[link.SourceNode];
                if (link.Function == "corrective")
                {
                    Require(link.Parameters.Length > 0, "facefx-corrective-parameter-missing");
                    var sourceNode = actor.Nodes[link.SourceNode];
                    correction = Math.Min(1, correction + Math.Max(source * sourceNode.InverseMaximum,
                        -source * sourceNode.InverseMinimum) * link.Parameters[0]);
                    continue;
                }

                var linked = EvaluateLink(link, source);
                value = node.Operation switch
                {
                    "sum" => value + linked,
                    "multiply" => value * linked,
                    "max" => Math.Max(value, linked),
                    "min" => Math.Min(value, linked),
                    _ => value
                };
            }

            // FaceFX suppresses the combined linked/raw-track value before final node clamping.
            values[index] = Math.Clamp((value + values[index]) * (1 - correction),
                node.Minimum, node.Maximum);
        }
    }

    private static float EvaluateLink(GraphLink link, float value) => link.Function switch
    {
        "null" => 0,
        "linear" when link.Parameters.Length == 2 => value * link.Parameters[0] + link.Parameters[1],
        "linear" when link.Parameters.Length == 1 => value * link.Parameters[0],
        "linear" => value,
        "negate" => -value,
        "constant" => link.Parameters.Length > 0 ? link.Parameters[0] : 1,
        "quadratic" => MathF.CopySign(value * value, value) * ParameterOrOne(link),
        "cubic" => value * value * value * ParameterOrOne(link),
        "sqrt" => value == 0 ? 0 : MathF.CopySign(MathF.Sqrt(MathF.Abs(value)), value) * ParameterOrOne(link),
        "inverse" => MathF.Abs(value) < Epsilon ? 0 : 1 / value,
        "one-clamp" => value <= 1 ? 1 : 1 / value,
        "clamped-linear" when link.Parameters.Length == 4 => EvaluateClampedLinear(link.Parameters, value),
        "clamped-linear" when link.Parameters.Length == 1 => value > 0 ? value * link.Parameters[0] : 0,
        "clamped-linear" => value,
        _ => throw Invalid("facefx-link-function-unsupported:" + link.Function)
    };

    private static float EvaluateClampedLinear(float[] parameters, float value)
    {
        var slope = parameters[0];
        var clampX = parameters[1];
        var clampY = parameters[2];
        var direction = parameters[3];
        return (direction > 0 && value > clampX) || (direction <= 0 && value < clampX)
            ? value * slope - slope * clampX + clampY
            : clampY;
    }

    private static float ParameterOrOne(GraphLink link) =>
        link.Parameters.Length == 1 ? link.Parameters[0] : 1;

    private static float Sample(FaceCurve curve, double time)
    {
        if (curve.Keys.Length == 0) return 0;
        if (time <= curve.Keys[0].Time) return curve.Keys[0].Value;
        for (var index = 1; index < curve.Keys.Length; index++)
        {
            var right = curve.Keys[index];
            if (time > right.Time) continue;
            var left = curve.Keys[index - 1];
            var duration = right.Time - left.Time;
            if (duration <= 0) return right.Value;
            var amount = (float)((time - left.Time) / duration);
            var tangentOut = left.SlopeOut * (float)duration;
            var tangentIn = right.SlopeIn * (float)duration;
            return amount * (amount * (amount * (2 * left.Value - 2 * right.Value + tangentOut + tangentIn)
                + (-3 * left.Value + 3 * right.Value - 2 * tangentOut - tangentIn)) + tangentOut) + left.Value;
        }
        return curve.Keys[^1].Value;
    }

    private static void ApplyBones(ActiveLine line)
    {
        var actor = line.Speaker.Actor;
        for (var index = 0; index < actor.Bones.Length; index++)
        {
            var bone = actor.Bones[index];
            var position = bone.Rest.Position;
            var scale = bone.Rest.Scale;
            var rotation = bone.Rest.Rotation;
            var blendFrom = rotation;
            foreach (var link in bone.Links)
            {
                var amount = line.NodeValues[link.NodeIndex];
                if (MathF.Abs(amount) < Epsilon) continue;
                position += link.Target.Position * amount;
                scale += link.Target.Scale * amount;
                var interpolated = FaceFxSlerp(blendFrom, link.Target.Rotation, amount);
                blendFrom = interpolated.AlignedSource;
                rotation *= bone.AuxiliaryRotation * interpolated.Result;
            }
            rotation = rotation.Normalized();
            var basis = line.BasePoses[index];
            var rotationDelta = bone.Rest.Rotation.Inverse() * rotation;
            var skeletonIndex = line.Speaker.BoneIndices[index];
            line.Speaker.Skeleton.SetBonePosePosition(skeletonIndex,
                basis.Position + ConvertPosition(position - bone.Rest.Position));
            line.Speaker.Skeleton.SetBonePoseRotation(skeletonIndex,
                (basis.Rotation * ConvertRotation(rotationDelta)).Normalized());
            line.Speaker.Skeleton.SetBonePoseScale(skeletonIndex,
                basis.Scale + ConvertScale(scale - bone.Rest.Scale));
        }
    }

    private static void ApplyMaterials(ActiveLine line)
    {
        Span<float> weights = stackalloc float[8];
        foreach (var output in line.Speaker.Actor.MaterialOutputs)
            weights[output.ParameterIndex] = line.NodeValues[output.NodeIndex];
        line.Speaker.Material.SetShaderParameter("mml_fEmotionsMapWeight0",
            new Vector4(weights[0], weights[1], weights[2], weights[3]));
        line.Speaker.Material.SetShaderParameter("mml_fEmotionsMapWeight1",
            new Vector4(weights[4], weights[5], weights[6], weights[7]));
    }

    private static SlerpResult FaceFxSlerp(Quaternion source, Quaternion target, float amount)
    {
        var difference = SquaredDistance(source, target);
        var addition = SquaredDistance(source, new Quaternion(-target.X, -target.Y, -target.Z, -target.W));
        var aligned = difference > addition ? new Quaternion(-source.X, -source.Y, -source.Z, -source.W) : source;
        var cosine = aligned.Dot(target);
        Quaternion result;
        if (cosine > 0.55f)
            result = new Quaternion((1 - amount) * aligned.X + amount * target.X,
                (1 - amount) * aligned.Y + amount * target.Y,
                (1 - amount) * aligned.Z + amount * target.Z,
                (1 - amount) * aligned.W + amount * target.W);
        else
        {
            var theta = MathF.Acos(Math.Clamp(cosine, -1, 1));
            var inverseSine = 1 / MathF.Sin(theta);
            var left = MathF.Sin((1 - amount) * theta) * inverseSine;
            var right = MathF.Sin(amount * theta) * inverseSine;
            result = new Quaternion(left * aligned.X + right * target.X,
                left * aligned.Y + right * target.Y, left * aligned.Z + right * target.Z,
                left * aligned.W + right * target.W);
        }
        return new SlerpResult(aligned, result);
    }

    private static float SquaredDistance(Quaternion left, Quaternion right) =>
        (left.X - right.X) * (left.X - right.X) + (left.Y - right.Y) * (left.Y - right.Y) +
        (left.Z - right.Z) * (left.Z - right.Z) + (left.W - right.W) * (left.W - right.W);

    private static ShaderMaterial? InstallEmotionMaterial(Node root)
    {
        var shader = GD.Load<Shader>(ShaderPath);
        var mask0 = DaoRuntimePaths.LoadTexture(EmotionRoot + "uh_hed_mlw.png");
        var mask1 = DaoRuntimePaths.LoadTexture(EmotionRoot + "uh_hed_mup.png");
        var normal = DaoRuntimePaths.LoadTexture(EmotionRoot + "uh_hed_emo_0n.png");
        if (shader is null || mask0 is null || mask1 is null || normal is null) return null;
        foreach (var node in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D mesh || mesh.Mesh is null || !mesh.Name.ToString().EndsWith("FaceM1"))
                continue;
            if (mesh.Mesh.GetSurfaceCount() != 1)
                return null;

            if (mesh.GetActiveMaterial(0) is ShaderMaterial existing &&
                IsFaceFxShader(existing.Shader?.ResourcePath))
            {
                BindEmotionMaps(existing, mask0, mask1, normal);
                GD.Print("OPENDAO_FACE_MATERIAL status=ready source=retail-base-material " +
                         $"mesh={mesh.Name} reuse=1 tier=" +
                         (existing.Shader?.ResourcePath == EnhancedShaderPath ? "enhanced" : "source"));
                return existing;
            }
            if (mesh.GetActiveMaterial(0) is not BaseMaterial3D source || source.AlbedoTexture is null)
                return null;

            var material = new ShaderMaterial { Shader = shader };
            // FaceFX changes only the facial bone poses and the authored emotion-normal
            // weights.  It must not replace the actor's imported BioWare surface inputs.
            // In particular, losing the diffuse map here produced the white, mask-like
            // faces that were visible in the city-elf opening.
            material.SetShaderParameter("albedo", source.AlbedoColor);
            material.SetShaderParameter("texture_albedo", source.AlbedoTexture);
            material.SetShaderParameter("roughness", source.Roughness);
            material.SetShaderParameter("specular", source.MetallicSpecular);
            material.SetShaderParameter("metallic", source.Metallic);
            material.SetShaderParameter("normal_strength", source.NormalEnabled ? source.NormalScale : 0.0f);
            material.SetShaderParameter("use_normal_texture", source.NormalEnabled && source.NormalTexture is not null);
            if (source.NormalTexture is not null)
                material.SetShaderParameter("texture_normal", source.NormalTexture);
            material.SetShaderParameter("use_roughness_texture", source.RoughnessTexture is not null);
            if (source.RoughnessTexture is not null)
                material.SetShaderParameter("texture_roughness", source.RoughnessTexture);
            material.SetShaderParameter("use_metallic_texture", source.MetallicTexture is not null);
            if (source.MetallicTexture is not null)
                material.SetShaderParameter("texture_metallic", source.MetallicTexture);
            material.SetShaderParameter("uv1_scale", source.Uv1Scale);
            material.SetShaderParameter("uv1_offset", source.Uv1Offset);
            BindEmotionMaps(material, mask0, mask1, normal);
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
                mesh.SetSurfaceOverrideMaterial(surface, material);
            GD.Print("OPENDAO_FACE_MATERIAL status=ready source=retail-base-material " +
                     $"mesh={mesh.Name} albedo=1 normal={(source.NormalTexture is null ? 0 : 1)} " +
                     $"roughness={source.Roughness:0.###} specular={source.MetallicSpecular:0.###}");
            return material;
        }
        return null;
    }

    private static bool IsFaceFxShader(string? resourcePath) =>
        resourcePath is ShaderPath or EnhancedShaderPath;

    private static void BindEmotionMaps(ShaderMaterial material, Texture2D mask0,
        Texture2D mask1, Texture2D normal)
    {
        material.SetShaderParameter("use_facefx_emotions", true);
        material.SetShaderParameter("texture_emotions_mask0", mask0);
        material.SetShaderParameter("texture_emotions_mask1", mask1);
        material.SetShaderParameter("texture_emotions_normal", normal);
        material.SetShaderParameter("mml_fEmotionsMapWeight0", Vector4.Zero);
        material.SetShaderParameter("mml_fEmotionsMapWeight1", Vector4.Zero);
    }

    private static FaceAnimation ParseAnimation(JsonElement value) => new(
        value.GetProperty("animation").GetString() ?? string.Empty,
        value.GetProperty("curves").EnumerateArray().Select(curve => new FaceCurve(
            curve.GetProperty("name").GetString() ?? string.Empty,
            curve.GetProperty("keys").EnumerateArray().Select(key => new CurveKey(
                key.GetProperty("time").GetDouble(), key.GetProperty("value").GetSingle(),
                key.GetProperty("slopeIn").GetSingle(), key.GetProperty("slopeOut").GetSingle())).ToArray())).ToArray());

    private static FaceActor ParseActor(JsonElement value)
    {
        var nodes = value.GetProperty("nodes").EnumerateArray().Select(ParseNode).ToArray();
        for (var index = 0; index < nodes.Length; index++)
            Require(nodes[index].Index == index, "facefx-graph-not-canonical");
        var bones = value.GetProperty("bones").EnumerateArray().Select(ParseBone).ToArray();
        var outputs = nodes.Where(node => node.Type == "material-parameter")
            .Select(node => new MaterialOutput(node.Index, MaterialIndex(value, node.Index))).ToArray();
        Require(outputs.Length == 12 && outputs.All(output => output.ParameterIndex is >= 0 and < 8),
            "facefx-material-output-invalid");
        return new FaceActor(value.GetProperty("actor").GetString() ?? string.Empty, nodes, bones,
            nodes.ToDictionary(node => node.Name, node => node.Index, StringComparer.Ordinal), outputs);
    }

    private static GraphNode ParseNode(JsonElement value) => new(value.GetProperty("index").GetInt32(),
        value.GetProperty("type").GetString() ?? string.Empty, value.GetProperty("name").GetString() ?? string.Empty,
        value.GetProperty("minimum").GetSingle(), value.GetProperty("maximum").GetSingle(),
        value.GetProperty("inverseMinimum").GetSingle(), value.GetProperty("inverseMaximum").GetSingle(),
        value.GetProperty("inputOperation").GetString() ?? string.Empty,
        value.GetProperty("inputLinks").EnumerateArray().Select(link => new GraphLink(
            link.GetProperty("sourceNode").GetInt32(), link.GetProperty("function").GetString() ?? string.Empty,
            link.GetProperty("parameters").EnumerateArray().Select(parameter => parameter.GetSingle()).ToArray())).ToArray());

    private static FaceBone ParseBone(JsonElement value)
    {
        var rest = ParseTransform(value.GetProperty("rest"));
        var auxiliary = ReadQuaternion(value.GetProperty("auxiliaryQuaternion"));
        var links = value.GetProperty("links").EnumerateArray().Select(link => new BoneLink(
            link.GetProperty("firstIndex").GetInt32(), ParseTransform(link.GetProperty("target")))).ToArray();
        return new FaceBone(value.GetProperty("name").GetString() ?? string.Empty, rest, auxiliary, links);
    }

    private static int MaterialIndex(JsonElement actor, int nodeIndex)
    {
        var node = actor.GetProperty("nodes")[nodeIndex];
        foreach (var property in node.GetProperty("userProperties").EnumerateArray())
            if (property.GetProperty("name").GetString() == "Material parameter index")
                return property.GetProperty("integer").GetInt32();
        return -1;
    }

    private static FaceTransform ParseTransform(JsonElement value) => new(ReadVector(value.GetProperty("position")),
        ReadQuaternion(value.GetProperty("rotation")), ReadVector(value.GetProperty("scale")));
    private static Vector3 ReadVector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
    private static Quaternion ReadQuaternion(JsonElement value) =>
        new(value[1].GetSingle(), value[2].GetSingle(), value[3].GetSingle(), value[0].GetSingle());
    // FaceFX returns parent-space transforms in the source actor basis.  The
    // Blender GLB export reflects that basis by exchanging Y and Z; it does
    // not apply the world-placement Y inversion used by CUT cameras.  The
    // previous X,Z,-Y mapping pushed lip bones through the face and mapped the
    // mouthBase quarter-turn to the opposite handedness.
    private static Vector3 ConvertPosition(Vector3 value) =>
        new Vector3(value.X, value.Z, value.Y) * PositionScale;
    private static Vector3 ConvertScale(Vector3 value) => new(value.X, value.Z, value.Y);
    private static Quaternion ConvertRotation(Quaternion value) =>
        new Quaternion(-value.X, -value.Z, -value.Y, value.W).Normalized();
    private static string Global(string path) => DaoRuntimePaths.ResolveSourcePath(path);
    private static T? FindDescendant<T>(Node root) where T : Node =>
        root is T match ? match : root.FindChildren("*", typeof(T).Name, true, false).OfType<T>().FirstOrDefault();
    private bool Fail(string reason) { FailureReason = reason; return false; }
    private static void Require(bool condition, string reason) { if (!condition) throw Invalid(reason); }
    private static InvalidDataException Invalid(string reason) => new(reason);

    private sealed record FaceAnimation(string Name, FaceCurve[] Curves);
    private sealed record FaceCurve(string Name, CurveKey[] Keys);
    private readonly record struct CurveKey(double Time, float Value, float SlopeIn, float SlopeOut);
    private sealed record FaceActor(string Name, GraphNode[] Nodes, FaceBone[] Bones,
        Dictionary<string, int> NodeByName, MaterialOutput[] MaterialOutputs);
    private sealed record GraphNode(int Index, string Type, string Name, float Minimum, float Maximum,
        float InverseMinimum, float InverseMaximum, string Operation, GraphLink[] Links);
    private sealed record GraphLink(int SourceNode, string Function, float[] Parameters);
    private sealed record FaceBone(string Name, FaceTransform Rest, Quaternion AuxiliaryRotation, BoneLink[] Links);
    private sealed record BoneLink(int NodeIndex, FaceTransform Target);
    private readonly record struct FaceTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale);
    private readonly record struct BonePose(Vector3 Position, Quaternion Rotation, Vector3 Scale);
    private readonly record struct MaterialOutput(int NodeIndex, int ParameterIndex);
    private readonly record struct SlerpResult(Quaternion AlignedSource, Quaternion Result);
    private sealed record SpeakerBinding(Skeleton3D Skeleton, ShaderMaterial Material,
        FaceActor Actor, int[] BoneIndices);
    private sealed record ActiveLine(FaceAnimation Animation, SpeakerBinding Speaker,
        BonePose[] BasePoses, float[] NodeValues);
}
