using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.Acceptance;

internal static partial class Program
{
    private static void DragonAgeGenericEffectGraphDecoderIsSourceBound()
    {
        var mmh = SyntheticEffectMmh("fxe_synthetic_p");
        var inventoryReady = DragonAgeOriginsEffectGraphDecoder.TryInspectEmitterCount(
            "models/fxe_synthetic_p.mmh", mmh, out var sourceEmitters,
            out var inventoryFailure);
        Expect(inventoryReady && sourceEmitters == 1 && inventoryFailure.Length == 0,
            "synthetic DAO source emitter inventory did not decode");
        var mao = Encoding.UTF8.GetBytes(
            "<MaterialObject Name=\"synthetic\"><Material Name=\"VFX.mat\"/>" +
            "<DefaultSemantic Name=\"Addv\"/>" +
            "<Texture Name=\"mml_tDiffuse\" ResName=\"synthetic.dds\"/>" +
            "</MaterialObject>");
        var dds = new byte[] { 0x44, 0x44, 0x53, 0x20, 1, 2, 3, 4 };
        var ready = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "models/fxe_synthetic_p.mmh", mmh,
            member => member == "synthetic.mao" ? mao : null,
            member => member == "synthetic.dds" ? dds : null,
            out var definition, out var failure);
        Expect(ready && failure.Length == 0,
            "synthetic DAO effect source contract did not decode: " + failure);
        Expect(definition.ResRef == "fxe_synthetic_p" &&
               definition.ModelHierarchySha256.Length == 64 &&
               definition.PresimulateSeconds == .25f &&
               definition.Emitters.Count == 1 &&
               definition.UnsupportedDistortionEmitters == 0 &&
               definition.UnsupportedEmitterSemantics?.Count == 0,
            "decoded DAO effect definition identity/count drifted");
        var emitter = definition.Emitters[0];
        Expect(emitter.Name == "SyntheticEmitter" &&
               emitter.MaterialObject == "synthetic.mao" &&
               emitter.Texture == "synthetic.dds" &&
               emitter.MaterialSha256.Length == 64 && emitter.TextureSha256.Length == 64 &&
               emitter.Blend == DragonAgeEffectBlend.Additive &&
               emitter.Orientation == DragonAgeEffectOrientation.CameraBillboard &&
               emitter.Columns == 1 && emitter.Rows == 1 &&
               emitter.AgeMap?.Count == 3 &&
               emitter.AngularVelocityDegrees == 30 &&
               emitter.AngularVelocityRangeDegrees == 5 &&
               emitter.InitialRotationDegrees == 10 &&
               emitter.InitialRotationRangeDegrees == 2 &&
               emitter.ScaleAspect == new Vector2(1, .5f) &&
               !emitter.AccelerationInObjectSpace,
            "decoded DAO emitter did not retain its neutral MMH/MAO contract");

        var variableAspect = SyntheticEffectMmh(
            "fxe_synthetic_variable_aspect_p", variableScaleAspect: true);
        var variableReady = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "fxe_synthetic_variable_aspect_p", variableAspect,
            member => member == "synthetic.mao" ? mao : null,
            member => member == "synthetic.dds" ? dds : null,
            out var variableDefinition, out var variableFailure);
        var variableEmitter = variableDefinition.Emitters.SingleOrDefault();
        Expect(variableReady && variableFailure.Length == 0 &&
               variableEmitter is not null && variableEmitter.IndependentScaleAxes &&
               variableEmitter.ScaleAspect is null && variableEmitter.AgeMap?.Count == 3 &&
               variableEmitter.AgeMap[1].Scale == new Vector2(.8f, .2f),
            "variable DAO sprite aspect did not retain independent X/Y curves");

        var zeroCrossing = SyntheticEffectMmh(
            "fxe_synthetic_zero_crossing_p", zeroCrossingScale: true);
        var zeroReady = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "fxe_synthetic_zero_crossing_p", zeroCrossing,
            member => member == "synthetic.mao" ? mao : null,
            member => member == "synthetic.dds" ? dds : null,
            out _, out var zeroFailure);
        Expect(!zeroReady && zeroFailure.Contains(
                   "age-map-scale-zero-crossing-unsupported",
                   StringComparison.Ordinal),
            "zero-crossing DAO scale curve did not fail closed");

        var malformedScale = SyntheticEffectMmh(
            "fxe_synthetic_malformed_scale_p", malformedScale: true);
        var malformedReady = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "fxe_synthetic_malformed_scale_p", malformedScale,
            member => member == "synthetic.mao" ? mao : null,
            member => member == "synthetic.dds" ? dds : null,
            out _, out var malformedFailure);
        Expect(!malformedReady && malformedFailure.Contains(
                   "age-map-value-invalid", StringComparison.Ordinal),
            "malformed DAO scale curve did not fail closed");

        var distortionMao = Encoding.UTF8.GetBytes(
            "<MaterialObject Name=\"distortion\"><Material Name=\"DADistortionMask.mat\"/>" +
            "<DefaultSemantic Name=\"Particle_CS\"/>" +
            "<Texture Name=\"mml_tDistortion\" ResName=\"normal.dds\"/>" +
            "<Texture Name=\"mml_tDistortionModifiers\" ResName=\"mask.dds\"/>" +
            "</MaterialObject>");
        var distortionReady = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "fxe_synthetic_p", mmh,
            member => member == "synthetic.mao" ? distortionMao : null,
            _ => throw new InvalidOperationException(
                "distortion contract must not resolve a diffuse texture"),
            out _, out var distortionFailure);
        Expect(!distortionReady && distortionFailure == "distortion-only-graph",
            "DADistortionMask Particle_CS semantic did not remain fail-closed distortion");

        var contactSheetMao = Encoding.UTF8.GetBytes(
            "<MaterialObject Name=\"static-sheet\"><Material Name=\"VFX.mat\"/>" +
            "<DefaultSemantic Name=\"ContactSheetBlend\"/>" +
            "<Texture Name=\"mml_tDiffuse\" ResName=\"synthetic.dds\"/>" +
            "</MaterialObject>");
        var staticSheetReady = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "fxe_synthetic_p", mmh,
            member => member == "synthetic.mao" ? contactSheetMao : null,
            member => member == "synthetic.dds" ? dds : null,
            out var staticSheetDefinition, out var staticSheetFailure);
        var staticSheetEmitter = staticSheetDefinition.Emitters.SingleOrDefault();
        Expect(staticSheetReady && staticSheetFailure.Length == 0 &&
               staticSheetEmitter is not null && staticSheetEmitter.Columns == 1 &&
               staticSheetEmitter.Rows == 1 && staticSheetEmitter.FramesPerSecond == 0,
            "explicit zero/zero/zero DAO contact sheet did not remain a static texture");

        var malformedSheet = SyntheticEffectMmh(
            "fxe_synthetic_malformed_sheet_p", flipbookColumns: 1);
        var malformedSheetReady = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "fxe_synthetic_malformed_sheet_p", malformedSheet,
            member => member == "synthetic.mao" ? contactSheetMao : null,
            member => member == "synthetic.dds" ? dds : null,
            out _, out var malformedSheetFailure);
        Expect(!malformedSheetReady && malformedSheetFailure.Contains(
                   "flipbook-contract-invalid", StringComparison.Ordinal),
            "mixed zero/nonzero DAO contact-sheet dimensions did not fail closed");

        var animatedZeroSheet = SyntheticEffectMmh(
            "fxe_synthetic_animated_zero_sheet_p", framesPerSecond: 1);
        var animatedZeroSheetReady = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "fxe_synthetic_animated_zero_sheet_p", animatedZeroSheet,
            member => member == "synthetic.mao" ? contactSheetMao : null,
            member => member == "synthetic.dds" ? dds : null,
            out _, out var animatedZeroSheetFailure);
        Expect(!animatedZeroSheetReady && animatedZeroSheetFailure.Contains(
                   "flipbook-contract-invalid", StringComparison.Ordinal),
            "animated zero-cell DAO contact sheet did not fail closed");
    }

    private static void DragonAgeEffectReadabilityGateFailsClosed()
    {
        var mao = Encoding.UTF8.GetBytes(
            "<MaterialObject Name=\"synthetic\"><Material Name=\"VFX.mat\"/>" +
            "<DefaultSemantic Name=\"Addv\"/>" +
            "<Texture Name=\"mml_tDiffuse\" ResName=\"synthetic.dds\"/>" +
            "</MaterialObject>");
        var ready = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "fxe_synthetic_readability_p", SyntheticEffectMmh(
                "fxe_synthetic_readability_p"),
            member => member == "synthetic.mao" ? mao : null,
            member => member == "synthetic.dds" ? [1] : null,
            out var constantDefinition, out var constantFailure);
        var constant = constantDefinition.Emitters.Single();
        var contract = DragonAgeOriginsEffectPresentationPolicy.Evaluate(
            constant, 256, 128, presentationScale: 1,
            enhancedPresentation: true);
        Expect(ready && constantFailure.Length == 0 &&
               !contract.IndependentScaleAxes &&
               contract.MeshAspect == new Vector2(1, .5f) &&
               contract.MaximumAnimatedScale == .8f &&
               contract.MaximumCardWidthMeters == .8f &&
               contract.MaximumCardHeightMeters == .4f &&
               contract.AtlasFrames == 1 &&
               contract.ProximityFadeDistanceMeters == .4f,
            "DAO constant-aspect readability contract drifted");

        var variableReady = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "fxe_synthetic_readability_variable_p", SyntheticEffectMmh(
                "fxe_synthetic_readability_variable_p", variableScaleAspect: true),
            member => member == "synthetic.mao" ? mao : null,
            member => member == "synthetic.dds" ? [1] : null,
            out var variableDefinition, out var variableFailure);
        var variable = variableDefinition.Emitters.Single();
        var variableContract = DragonAgeOriginsEffectPresentationPolicy.Evaluate(
            variable, 256, 128, presentationScale: 1,
            enhancedPresentation: true);
        Expect(variableReady && variableFailure.Length == 0 &&
               variableContract.IndependentScaleAxes &&
               variableContract.MeshAspect == Vector2.One &&
               variableContract.ProximityFadeDistanceMeters is null,
            "DAO independent-axis readability contract invented a constant aspect or fade");

        ExpectThrows<InvalidDataException>(
            () => DragonAgeOriginsEffectPresentationPolicy.Evaluate(
                variable with { ScaleAspect = new Vector2(1, .5f) },
                256, 128, 1, true),
            "DAO independent-axis emitter accepted a contradictory constant aspect");
        ExpectThrows<InvalidDataException>(
            () => DragonAgeOriginsEffectPresentationPolicy.Evaluate(
                constant with
                {
                    AgeMap =
                    [
                        new DragonAgeEffectAgeKey(0, new Vector2(129), Vector4.One),
                        new DragonAgeEffectAgeKey(1, new Vector2(129), Vector4.One)
                    ],
                    ScaleAspect = Vector2.One
                }, 256, 128, 1, true),
            "DAO readability gate accepted a giant particle card");
        ExpectThrows<InvalidDataException>(
            () => DragonAgeOriginsEffectPresentationPolicy.Evaluate(
                constant with { Columns = 3 }, 256, 128, 1, true),
            "DAO readability gate accepted a non-divisible atlas grid");
        ExpectThrows<InvalidDataException>(
            () => DragonAgeOriginsEffectPresentationPolicy.Evaluate(
                constant with { FramesPerSecond = 1001 }, 256, 128, 1, true),
            "DAO readability gate accepted an unbounded atlas frame rate");
    }

    private static void DragonAgeGenericEffectGraphDecoderFailsClosed()
    {
        var mmh = SyntheticEffectMmh("fxe_synthetic_bad_p");
        var inventoryReady = DragonAgeOriginsEffectGraphDecoder.TryInspectEmitterCount(
            "fxe_synthetic_bad_p", mmh, out var sourceEmitters, out var inventoryFailure);
        Expect(inventoryReady && sourceEmitters == 1 && inventoryFailure.Length == 0,
            "unsupported synthetic emitter was hidden from the source inventory");
        var unknownMao = Encoding.UTF8.GetBytes(
            "<MaterialObject Name=\"synthetic\"><Material Name=\"VFX.mat\"/>" +
            "<DefaultSemantic Name=\"UnknownSemantic\"/>" +
            "<Texture Name=\"mml_tDiffuse\" ResName=\"synthetic.dds\"/>" +
            "</MaterialObject>");
        var ready = DragonAgeOriginsEffectGraphDecoder.TryDecode(
            "fxe_synthetic_bad_p", mmh,
            member => member == "synthetic.mao" ? unknownMao : null,
            _ => new byte[] { 1 }, out _, out var failure);
        Expect(!ready && failure.Contains("graph-has-no-supported-emitter",
                   StringComparison.Ordinal) &&
               failure.Contains("material-semantic-unsupported",
                   StringComparison.Ordinal),
            "unknown DAO MAO semantic did not fail closed at emitter scope");
    }

    private static byte[] SyntheticEffectMmh(string resRef,
        bool variableScaleAspect = false,
        bool zeroCrossingScale = false,
        bool malformedScale = false,
        byte flipbookColumns = 0,
        byte flipbookRows = 0,
        float framesPerSecond = 0)
    {
        const ushort listReference = 0xa000;
        var specs = new[]
        {
            new TestStruct("mdlh", 12,
                F(6000, 14, 0), F(6333, 8, 4), F(6999, ushort.MaxValue, 8, listReference)),
            new TestStruct("node", 8,
                F(6000, 14, 0), F(6999, ushort.MaxValue, 4, listReference)),
            new TestStruct("nemt", 180,
                F(6000, 14, 0), F(6001, 14, 4),
                F(6011, 8, 8), F(6012, 8, 12), F(6013, 8, 16), F(6014, 8, 20),
                F(6015, 8, 24), F(6016, 8, 28), F(6017, 8, 32), F(6018, 8, 36),
                F(6019, 8, 40), F(6020, 8, 44), F(6021, 8, 48), F(6022, 8, 52),
                F(6023, 8, 56), F(6024, 8, 60), F(6025, 8, 64), F(6026, 8, 68),
                F(6028, 0, 72), F(6030, 0, 73), F(6031, 8, 76),
                F(6032, 0, 80), F(6033, 0, 81), F(6035, 0, 82), F(6036, 0, 83),
                F(6037, 4, 84), F(6180, 8, 88), F(6181, 0, 92), F(6182, 0, 93),
                F(6184, 14, 96), F(6185, 8, 100), F(6186, 8, 104),
                F(6187, 0, 108), F(6188, 0, 109), F(6234, 0, 110),
                F(6239, 0, 111), F(6243, 0, 112), F(6284, 14, 116),
                F(6294, 10, 120), F(6298, 0, 132), F(6299, 8, 136),
                F(6300, 8, 140), F(6321, 0, 144),
                F(6999, ushort.MaxValue, 148, listReference)),
            new TestStruct("trsl", 16, F(6047, 12, 0)),
            new TestStruct("rota", 16, F(6048, 13, 0)),
            new TestStruct("spnv", 48,
                F(6046, 0, 0), F(6285, 0, 1), F(6286, 8, 4),
                F(6289, 10, 8), F(6290, 10, 20), F(6291, 0, 32)),
            new TestStruct("amap", 8,
                F(6039, 4, 0), F(6999, ushort.MaxValue, 4, listReference)),
            new TestStruct("amel", 32,
                F(6040, 8, 0), F(6041, 8, 4), F(6042, 8, 8), F(6043, 15, 12))
        };
        var fieldBytes = specs.Sum(spec => spec.Fields.Length) * 12;
        var dataStart = 28 + specs.Length * 16 + fieldBytes;
        var data = new List<byte>();
        int Allocate(int type)
        {
            var result = data.Count;
            data.AddRange(new byte[specs[type].Size]);
            return result;
        }
        var root = Allocate(0);
        var node = Allocate(1);
        var emitter = Allocate(2);
        var translation = Allocate(3);
        var rotation = Allocate(4);
        var spawn = Allocate(5);
        var ageMap = Allocate(6);
        var age0 = Allocate(7);
        var age1 = Allocate(7);
        var age2 = Allocate(7);

        void WriteAt(int offset, ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index < bytes.Length; index++) data[offset + index] = bytes[index];
        }
        void U32(int at, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            WriteAt(at, bytes);
        }
        void Float(int at, float value) => U32(at, unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        void String(int owner, int field, string value)
        {
            var relative = data.Count;
            var bytes = Encoding.Unicode.GetBytes(value);
            var payload = new byte[4 + bytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)value.Length));
            bytes.CopyTo(payload, 4);
            data.AddRange(payload);
            U32(owner + field, checked((uint)relative));
        }
        void Children(int owner, int field, params (int Type, int Offset)[] children)
        {
            var relative = data.Count;
            var payload = new byte[4 + children.Length * 8];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)children.Length));
            for (var index = 0; index < children.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4 + index * 8, 4),
                    checked((uint)children[index].Type | 0x40000000));
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8 + index * 8, 4),
                    checked((uint)children[index].Offset));
            }
            data.AddRange(payload);
            U32(owner + field, checked((uint)relative));
        }
        void Vector(int at, params float[] values)
        {
            for (var index = 0; index < values.Length; index++) Float(at + index * 4, values[index]);
        }

        String(root, 0, resRef + ".mmh");
        Float(root + 4, .25f);
        Children(root, 8, (1, node));
        String(node, 0, "SyntheticNode");
        Children(node, 4, (2, emitter));
        String(emitter, 0, "SyntheticEmitter");
        String(emitter, 4, "synthetic");
        Float(emitter + 8, 8); Float(emitter + 12, 2);
        Float(emitter + 16, 2); Float(emitter + 20, .5f);
        Float(emitter + 24, .2f); Float(emitter + 28, 1.5f);
        Float(emitter + 32, .25f); Float(emitter + 36, -.1f);
        Float(emitter + 40, 30); Float(emitter + 44, 5);
        Float(emitter + 48, 0); Float(emitter + 52, 0);
        Float(emitter + 56, 12); Float(emitter + 60, 12);
        Float(emitter + 64, 0); Float(emitter + 68, 0);
        Float(emitter + 76, .4f); U32(emitter + 84, 3);
        Float(emitter + 88, framesPerSecond);
        data[emitter + 92] = flipbookRows;
        data[emitter + 93] = flipbookColumns;
        U32(emitter + 96, uint.MaxValue); Float(emitter + 100, 0); Float(emitter + 104, 0);
        U32(emitter + 116, uint.MaxValue);
        Vector(emitter + 120, 0, 0, 1);
        data[emitter + 132] = 0;
        Float(emitter + 136, 10); Float(emitter + 140, 2);
        Children(emitter, 148, (3, translation), (4, rotation), (5, spawn), (6, ageMap));
        Vector(translation, 1, 2, 3, 1);
        Vector(rotation, 0, 0, 0, 1);
        data[spawn + 1] = 0;
        Vector(spawn + 8, 0, 0, 0); Vector(spawn + 20, 0, 0, 0);
        U32(ageMap, 3);
        Children(ageMap, 4, (7, age0), (7, age1), (7, age2));
        foreach (var (at, time, scale, alpha) in new[]
                 { (age0, 0f, .2f, 0f), (age1, .5f, .8f, 1f), (age2, 1f, .1f, 0f) })
        {
            var aspect = variableScaleAspect && time == .5f ? .25f : .5f;
            var scaleX = malformedScale && time == .5f ? float.NaN : scale;
            var scaleY = zeroCrossingScale && time == .5f ? 0 : scale * aspect;
            Float(at, time); Float(at + 4, scaleX); Float(at + 8, scaleY);
            Vector(at + 12, 1, .5f, .25f, alpha);
        }

        var output = new byte[dataStart + data.Count];
        Encoding.ASCII.GetBytes("GFF V4.0PC  MMH V0.1").CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(20, 4), checked((uint)specs.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24, 4), checked((uint)dataStart));
        var fieldAt = 28 + specs.Length * 16;
        for (var index = 0; index < specs.Length; index++)
        {
            var structAt = 28 + index * 16;
            Encoding.ASCII.GetBytes(specs[index].Kind).CopyTo(output, structAt);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(structAt + 4, 4),
                checked((uint)specs[index].Fields.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(structAt + 8, 4),
                checked((uint)fieldAt));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(structAt + 12, 4),
                checked((uint)specs[index].Size));
            foreach (var field in specs[index].Fields)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(fieldAt, 4), field.Label);
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(fieldAt + 4, 2), field.Type);
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(fieldAt + 6, 2), field.Flags);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(fieldAt + 8, 4),
                    checked((uint)field.Offset));
                fieldAt += 12;
            }
        }
        data.ToArray().CopyTo(output, dataStart);
        return output;
    }

    private static TestField F(uint label, ushort type, int offset, ushort flags = 0) =>
        new(label, type, flags, offset);

    private sealed record TestField(uint Label, ushort Type, ushort Flags, int Offset);
    private sealed record TestStruct(string Kind, int Size, params TestField[] Fields);
}
