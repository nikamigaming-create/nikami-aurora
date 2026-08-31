using Godot;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

/// <summary>
/// Converts DAO's linear RGB radiance records into Godot's source-color plus
/// scalar-energy representation. Godot linearizes Light3D/Environment colors
/// internally, so assigning DAO's linear chromaticity directly would apply the
/// transfer function twice.
/// </summary>
internal static class DaoLightEncoding
{
    internal const string Contract = "linear-radiance-to-srgb-chromaticity";

    internal static EncodedLight Encode(float red, float green, float blue)
    {
        red = Math.Max(0, red);
        green = Math.Max(0, green);
        blue = Math.Max(0, blue);
        var energy = Math.Max(red, Math.Max(green, blue));
        if (energy <= 0.000001f) return new EncodedLight(Colors.White, 0);
        var linearChromaticity = new Color(red / energy, green / energy, blue / energy, 1);
        return new EncodedLight(linearChromaticity.LinearToSrgb(), energy);
    }
}

internal readonly record struct EncodedLight(Color Color, float Energy);
