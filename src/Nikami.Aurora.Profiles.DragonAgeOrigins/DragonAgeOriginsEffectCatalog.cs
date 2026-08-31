using System.Numerics;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public enum DragonAgeEffectBlend
{
    Alpha,
    Additive
}

public enum DragonAgeEffectOrientation
{
    CameraBillboard,
    HorizontalPlane
}

public enum DragonAgeEffectVolume
{
    Point,
    Sphere,
    Box
}

public sealed record DragonAgeEffectAgeKey(
    float Time,
    Vector2 Scale,
    Vector4 Color);

public sealed record DragonAgeEffectEmitterSemanticEvidence(
    string Name,
    string MaterialObject,
    float? MovementSpreadUpdateDelaySeconds,
    float? MovementSpreadXDegrees,
    float? MovementSpreadYDegrees,
    float? SpawnSpreadXDegrees,
    float? SpawnSpreadYDegrees,
    float? FramesPerSecond,
    int FlipbookColumns,
    int FlipbookRows,
    int OrientationCode,
    int? SpawnVolumeCode,
    bool UsesSpawnNormalForVelocity,
    float? SpawnRadius,
    string SpawnMinimum,
    string SpawnMaximum,
    bool NumericFieldsFinite);

public sealed record DragonAgeEffectEmitter(
    string Name,
    string MaterialObject,
    string MaterialSha256,
    string Texture,
    string TextureSha256,
    DragonAgeEffectBlend Blend,
    DragonAgeEffectOrientation Orientation,
    float BirthRate,
    float BirthRateRange,
    float Lifetime,
    float LifetimeRange,
    int Columns,
    int Rows,
    float FramesPerSecond,
    Vector3 Translation,
    float Velocity,
    float VelocityRange,
    float Acceleration,
    float Gravity,
    Vector3 WorldAcceleration,
    float SpreadDegrees,
    DragonAgeEffectVolume Volume,
    Vector3 VolumeExtents,
    float SizeStart,
    float SizeMiddle,
    float SizeEnd,
    Vector4 ColorStart,
    Vector4 ColorMiddle,
    Vector4 ColorEnd,
    float MiddleTime,
    Vector3 SourceDirection,
    Quaternion LocalRotation,
    IReadOnlyList<DragonAgeEffectAgeKey>? AgeMap = null,
    float ScaleRange = 0,
    float InitialRotationDegrees = 0,
    float InitialRotationRangeDegrees = 0,
    float AngularVelocityDegrees = 0,
    float AngularVelocityRangeDegrees = 0,
    float AngularAccelerationDegrees = 0,
    bool AccelerationInObjectSpace = true,
    Vector2? ScaleAspect = null,
    bool IndependentScaleAxes = false);

public sealed record DragonAgeEffectDefinition(
    string ResRef,
    string ModelHierarchySha256,
    float PresimulateSeconds,
    IReadOnlyList<DragonAgeEffectEmitter> Emitters,
    int UnsupportedDistortionEmitters,
    IReadOnlyList<string>? UnsupportedEmitterSemantics = null);

/// <summary>
/// Source-bound subset of installed DAO particle graphs with recovered emitter
/// semantics. The contracts are layout-neutral: any imported area may resolve
/// them. Unknown graphs must remain absent until an equivalent source contract
/// is recovered. Archive IO and rendering remain runtime responsibilities.
/// </summary>
public static class DragonAgeOriginsEffectCatalog
{
    private static readonly Vector4 White = Vector4.One;
    private static readonly Vector4 Clear = new(1, 1, 1, 0);

    private static readonly IReadOnlyDictionary<string, DragonAgeEffectDefinition> Definitions =
        new Dictionary<string, DragonAgeEffectDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["fxe_fire_cnd_p"] = Definition(
                "fxe_fire_cnd_p", "73f8cbb0c0aca810a5f150969baae75af65688fabe412ec6ba77bde1ebdac2c6",
                0.033333f, 0,
                Emitter("Flame01", "dotblur_add.mao", "24b709dc673674dd1619a8c4f8a09ab703f796d7be2d75cd865cfd9f138b6b98",
                    "dot_blur.dds", "ff971c7375f5e9ce2e41c9718fcf0e181b365d3562cf01ffcaa1b958ec53b182",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    5, 0, 1, 0, 1, 1, 0, Vector3.Zero, .1f, .01f, 0, 0, Vector3.Zero, 0,
                    DragonAgeEffectVolume.Point, Vector3.Zero, .08f, .07f, .05f,
                    new(.8f, .3f, .1f, 0), new(.8f, .3f, .1f, 1), new(.8f, .3f, .1f, 0)),
                Emitter("Flame02", "dotblur_add.mao", "24b709dc673674dd1619a8c4f8a09ab703f796d7be2d75cd865cfd9f138b6b98",
                    "dot_blur.dds", "ff971c7375f5e9ce2e41c9718fcf0e181b365d3562cf01ffcaa1b958ec53b182",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    4, 0, 1, 0, 1, 1, 0, Vector3.Zero, .1f, .01f, 0, 0, Vector3.Zero, 0,
                    DragonAgeEffectVolume.Point, Vector3.Zero, .08f, .07f, .05f,
                    new(.8f, .3f, .1f, 0), new(.8f, .3f, .1f, 1), new(.8f, .3f, .1f, 0)),
                Emitter("Glow", "dotblur_add.mao", "24b709dc673674dd1619a8c4f8a09ab703f796d7be2d75cd865cfd9f138b6b98",
                    "dot_blur.dds", "ff971c7375f5e9ce2e41c9718fcf0e181b365d3562cf01ffcaa1b958ec53b182",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    10, 0, .4f, 0, 1, 1, 0, new(0, 0, .054999f), 0, 0, 0, 0,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Point, Vector3.Zero, .12f, .15f, .12f,
                    Clear, new(1, .45f, .12f, .8f), Clear)) with
            {
                UnsupportedEmitterSemantics =
                    ["curated-source-emitter-contract-unavailable:1"]
            },

            ["fxe_fire_m_ns_p"] = Definition(
                "fxe_fire_m_ns_p", "ee2b0c017a989e5a8e87a71092e33238bcc0547ff592e0e152de4cb0e7fc894e",
                0.033333f, 1,
                Emitter("FireBase", "fx_fireflamefb03_add.mao", "0896c033c2a7a1075ca13ca201bebc04a79228b90b4997140b55bb7937c3f647",
                    "fx_flamefb03.dds", "a60c3037323b2a6576298df84333065ef3b12ced5232658feacf9c3823813ec6",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    12, 0, 1.5f, 0, 4, 8, 24, new(-.075f, 0, .452f), .02f, .5f, 0, 0,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Sphere, new(.75f), .8f, 1.1f, 1.1f,
                    Clear, White, Clear),
                Emitter("FireTall", "fx_firetorchfb_add.mao", "571d2c775900e4fc94a78ccfacf4f8cd6974149a7fba1baa180026966f838ca8",
                    "fx_firetorchfb.dds", "56f821e5609067f53fb8ed52784ef405b92c10d31a14672e4a7c2945bd1a058c",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    3, 0, .5f, 0, 4, 8, 18, new(.127692f, 0, .72482f), .5f, .25f, 0, 0,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Sphere, new(.4f), 2, 2, 2,
                    Clear, White, Clear, localRotation: new(0, 0, 1, .000001f)),
                Emitter("Embers", "dotblur_add.mao", "24b709dc673674dd1619a8c4f8a09ab703f796d7be2d75cd865cfd9f138b6b98",
                    "dot_blur.dds", "ff971c7375f5e9ce2e41c9718fcf0e181b365d3562cf01ffcaa1b958ec53b182",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    15, 14, 1, .5f, 1, 1, 0, new(-.017849f, 0, .461515f), 3, 0, -1, 0, new(.5f, 0, 1), 10,
                    DragonAgeEffectVolume.Sphere, new(.25f), .04f, .04f, .02f,
                    Clear, new(1, .45f, .1f, 1), Clear,
                    localRotation: new(0, .237686f, 0, .971342f)),
                Emitter("Glow", "dotblur_add.mao", "24b709dc673674dd1619a8c4f8a09ab703f796d7be2d75cd865cfd9f138b6b98",
                    "dot_blur.dds", "ff971c7375f5e9ce2e41c9718fcf0e181b365d3562cf01ffcaa1b958ec53b182",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    3, 0, 1.2f, .2f, 1, 1, 0, new(-.078159f, 0, .206714f), 1, 0, 0, 0,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Point, Vector3.Zero, 2.2f, 3, 2.2f,
                    Clear, new(1, .4f, .1f, .65f), Clear,
                    localRotation: new(0, .105425f, 0, .994427f)),
                Emitter("FireMeat", "fx_fireringfb_add.mao", "d3c1d729140dea5baffbee8e19642a52af3db75e7b5e7d9cdc1ada8893842d2c",
                    "fx_fireringfb.dds", "00f128faf47dda78c5a947f1963fa49b6466606e65cc6b362f19f3d348131895",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    5, 0, 1, 0, 4, 4, 30, new(-.147957f, .000001f, .242667f), .5f, .2f, 0, 0, Vector3.Zero, 0,
                    DragonAgeEffectVolume.Sphere, new(.25f), 1.5f, 1.5f, 1.5f,
                    Clear, White, Clear)),

            ["fxe_fire_small_p"] = Definition(
                "fxe_fire_small_p", "9db78bdcf090d28e16b15e6acaf76bf082d577ca029e42a33bd5b9d15a375a6c",
                0.033333f, 1,
                Emitter("Glow", "dotblur_add.mao", "24b709dc673674dd1619a8c4f8a09ab703f796d7be2d75cd865cfd9f138b6b98",
                    "dot_blur.dds", "ff971c7375f5e9ce2e41c9718fcf0e181b365d3562cf01ffcaa1b958ec53b182",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    7, 0, .4f, .1f, 1, 1, 0, new(0, 0, .313837f), 0, 0, 0, 0,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Point, Vector3.Zero, .8f, 1, .8f,
                    Clear, new(1, .4f, .1f, .7f), Clear),
                Emitter("FireMeat", "fx_fireringfb_add.mao", "d3c1d729140dea5baffbee8e19642a52af3db75e7b5e7d9cdc1ada8893842d2c",
                    "fx_fireringfb.dds", "00f128faf47dda78c5a947f1963fa49b6466606e65cc6b362f19f3d348131895",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    5, 0, 1, 0, 4, 4, 24, new(0, 0, -.018f), .5f, .1f, 0, 0,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Point, Vector3.Zero, .2f, .3f, .2f,
                    Clear, White, Clear),
                Emitter("Flame", "fx_firetorchfb_add.mao", "571d2c775900e4fc94a78ccfacf4f8cd6974149a7fba1baa180026966f838ca8",
                    "fx_firetorchfb.dds", "56f821e5609067f53fb8ed52784ef405b92c10d31a14672e4a7c2945bd1a058c",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    7, 0, 1, 0, 4, 8, 24, new(0, 0, -.0268f), 1.2f, .1f, -.6f, 0,
                    Vector3.Zero, 2, DragonAgeEffectVolume.Point, Vector3.Zero, .5f, 1, .5f,
                    Clear, White, Clear)),

            ["fxe_tree_beam_blur"] = Definition(
                "fxe_tree_beam_blur", "0d1fcfd99199ab6c9c0e4172239a73e4d648e1f2175da1f6fa3eeb637150088f",
                0, 0,
                Emitter("Beam", "fxe_day_tree_beam.mao", "514bbafab2c0b4289a46072126e8475a75d0374aa253bceca131fa862f78997c",
                    "fxe_day_beams_blured.dds", "3acdc585af318185af495c27188569c5ab0aab8a35b0f942f445c7c4f7602683",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    3, 0, 7, 1, 1, 1, 0, new(0, 0, 10.3085f), .1f, .05f, 0, 0,
                    Vector3.Zero, 3, DragonAgeEffectVolume.Box, new(5, 5, 0), 8, 20, 8,
                    new(.469f, .469f, .469f, 0), new(.469f, .469f, .469f, .2f),
                    new(.469f, .469f, .469f, 0), .5f)),

            ["fxe_water_ripples"] = Definition(
                "fxe_water_ripples", "f8a98f8cb5f638a8da891c101cbee223385ca7c9a9b8b44334906892fc4f330f",
                0, 1,
                Emitter("DownSmoke", "fx_smoke_vol_add.mao", "c90ef11002640d06d2be67de46024876e28132c8f2c81058aa4ab822dc0629f7",
                    "fx_smoke_vol.dds", "9117b4f3506ba068381210c0735dde667c1b8971303e0c72df1af984bcb6f695",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    16, 0, 1.2f, .2f, 8, 1, 5, new(2.112442f, -.048039f, .381342f), .3f, .1f, 1, 1,
                    new(0, 0, 6), 0, DragonAgeEffectVolume.Sphere, Vector3.One, 1.35f, 3.7f, 3.7f,
                    Clear, new(.3f, .45f, .65f, .29f), Clear,
                    localRotation: new(-.001635f, .405178f, .00369f, .914229f)),
                Emitter("DownWater", "color_of_water.mao", "4d40e28f0b3440843c94b425357a507752e42604bb2d2fdc069cd247b5522481",
                    "fxe_water_color.dds", "ff7b0af3224419d5552f133a3fe76877a70757f86c089b6ba6848b77fbd69bb3",
                    DragonAgeEffectBlend.Alpha, DragonAgeEffectOrientation.HorizontalPlane,
                    20, 0, 2, 0, 1, 1, 0, new(2.104898f, -.000002f, .52621f), .01f, 0, 0, 0,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Box, new(1, 1, .3f), 3.15f, 3.93f, 3.93f,
                    Clear, new(.35f, .42f, .24f, .14f), Clear,
                    localRotation: new(0, 0, .737277f, .67559f)),
                Emitter("Circle", "fxe_ripple_add.mao", "39cd623a15aa93940c460ddbfeeed74837d36932a3358a97d21668ed32b9372f",
                    "fxe_rippleout.dds", "88a2f98981322c847a3b44ab6a81c0e37acc0cc37c367e1ea8bdf0bc4c2af7ff",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.HorizontalPlane,
                    7, 0, 1.5f, .3f, 2, 4, 4, new(2.074023f, -.063754f, .628736f), .01f, 0, 0, 0,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Box, new(.01f), 1.05f, 4.28f, 4.28f,
                    Clear, new(.7f, .55f, .3f, .76f), Clear)),

            ["fxe_dirtywater_p"] = Definition(
                "fxe_dirtywater_p", "111050926d2f3d55782239fc796ab9ed6008ef80d5fa55cb1c3ad4375564bc81",
                0, 3,
                Emitter("ColorOfWater", "color_of_water.mao", "4d40e28f0b3440843c94b425357a507752e42604bb2d2fdc069cd247b5522481",
                    "fxe_water_color.dds", "ff7b0af3224419d5552f133a3fe76877a70757f86c089b6ba6848b77fbd69bb3",
                    DragonAgeEffectBlend.Alpha, DragonAgeEffectOrientation.CameraBillboard,
                    10, 0, 3, 0, 1, 1, 0, new(-2.880304f, -.048043f, 3.664234f), 2, 0, 0, .15f,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Box, new(1, .05f, .05f), 1, 2, 1,
                    Clear, new(.28f, .34f, .18f, .3f), Clear,
                    localRotation: new(0, .599668f, 0, .800249f)),
                Emitter("WaterSmoke16", "smoke_addmore.mao", "99c3e7364241c34ee48e33fc0bc0c94060f204ec8d6c989d2e1dbeeca5edb6fe",
                    "fxe_water_smokes_add.dds", "ae62664a8648d63da242b854c065580de6eb8df0fb5546d47e5ce5fd9ea7bfeb",
                    DragonAgeEffectBlend.Additive, DragonAgeEffectOrientation.CameraBillboard,
                    6, 0, 1.5f, .3f, 2, 4, 0, new(-1.620811f, -.048041f, 3.425844f), 1, .3f, 1, .6f,
                    new(0, 0, 2), 0, DragonAgeEffectVolume.Point, Vector3.Zero, .6f, 1.4f, 1.4f,
                    Clear, new(.35f, .45f, .55f, .35f), Clear,
                    localRotation: new(0, .405183f, 0, .914236f)),
                Emitter("WaterfallTexture", "water_pan2_blend.mao", "a723a60fb441216ebbb8f2037c866e37eef97c073a387f368a57f34cba8e3898",
                    "fxe_water_pan2.dds", "ea6e8aa23209da8f33ae078adc3494c59f825baaeea4032ec121d3ce585afa85",
                    DragonAgeEffectBlend.Alpha, DragonAgeEffectOrientation.HorizontalPlane,
                    5, 0, 1, 0, 4, 4, 50, new(.732819f, -.129566f, 2.69151f), .1f, .1f, 0, .1f,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Point, Vector3.Zero, 1, 1.4f, 1,
                    Clear, new(.7f, .75f, .6f, .45f), Clear,
                    localRotation: new(0, .454205f, 0, .890897f)),
                Emitter("WaterfallTexture2", "water_pan2_blend.mao", "a723a60fb441216ebbb8f2037c866e37eef97c073a387f368a57f34cba8e3898",
                    "fxe_water_pan2.dds", "ea6e8aa23209da8f33ae078adc3494c59f825baaeea4032ec121d3ce585afa85",
                    DragonAgeEffectBlend.Alpha, DragonAgeEffectOrientation.HorizontalPlane,
                    3, 0, 1, 0, 4, 4, 50, new(-1.942255f, -.129564f, 3.776173f), .1f, .1f, 0, .1f,
                    Vector3.Zero, 0, DragonAgeEffectVolume.Point, Vector3.Zero, 1, 1.4f, 1,
                    Clear, new(.7f, .75f, .6f, .45f), Clear,
                    localRotation: new(0, .065101f, 0, .997879f))) with
            {
                UnsupportedEmitterSemantics =
                    [
                        "curated-source-emitter-contract-unavailable:1",
                        "curated-source-emitter-contract-unavailable:2",
                        "curated-source-emitter-contract-unavailable:3",
                        "curated-source-emitter-contract-unavailable:4",
                        "curated-source-emitter-contract-unavailable:5"
                    ]
            }
        };

    public static IEnumerable<DragonAgeEffectDefinition> SupportedDefinitions =>
        Definitions.Values;

    public static bool TryResolve(string modelPathOrResRef, out DragonAgeEffectDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPathOrResRef);
        var normalized = Path.GetFileNameWithoutExtension(modelPathOrResRef.Replace('\\', '/'));
        return Definitions.TryGetValue(normalized, out definition!);
    }

    private static DragonAgeEffectDefinition Definition(string resRef, string hash,
        float presimulate, int unsupportedDistortion, params DragonAgeEffectEmitter[] emitters) =>
        new(resRef, hash, presimulate, emitters, unsupportedDistortion);

    private static DragonAgeEffectEmitter Emitter(string name, string mao, string maoHash,
        string texture, string textureHash, DragonAgeEffectBlend blend,
        DragonAgeEffectOrientation orientation, float birthRate, float birthRateRange,
        float lifetime, float lifetimeRange, int columns, int rows, float fps,
        Vector3 translation, float velocity, float velocityRange, float acceleration,
        float gravity, Vector3 worldAcceleration, float spreadDegrees,
        DragonAgeEffectVolume volume, Vector3 volumeExtents, float sizeStart,
        float sizeMiddle, float sizeEnd, Vector4 colorStart, Vector4 colorMiddle,
        Vector4 colorEnd, float middleTime = .25f, Vector3? sourceDirection = null,
        Quaternion? localRotation = null) =>
        new(name, mao, maoHash, texture, textureHash, blend, orientation, birthRate,
            birthRateRange, lifetime, lifetimeRange, columns, rows, fps, translation,
            velocity, velocityRange, acceleration, gravity, worldAcceleration,
            spreadDegrees, volume, volumeExtents, sizeStart, sizeMiddle, sizeEnd,
            colorStart, colorMiddle, colorEnd, middleTime,
            sourceDirection ?? Vector3.UnitZ, localRotation ?? Quaternion.Identity);
}

public static class DragonAgeOriginsCoordinateSystem
{
    private static readonly Matrix4x4 SourceToRuntime = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    public static Vector3 Convert(Vector3 source) =>
        Vector3.Transform(source, SourceToRuntime);

    public static Quaternion Convert(Quaternion source)
    {
        if (source.LengthSquared() < .000001f)
            throw new ArgumentException("Source quaternion is degenerate.", nameof(source));
        Matrix4x4.Invert(SourceToRuntime, out var inverse);
        var converted = inverse * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(source)) *
                        SourceToRuntime;
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(converted));
    }
}
