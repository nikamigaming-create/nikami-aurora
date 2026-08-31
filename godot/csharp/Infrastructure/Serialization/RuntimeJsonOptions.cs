using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Serialization;

internal static class RuntimeJsonOptions
{
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true
    };

    public static JsonSerializerOptions IndentedCamelCase { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static JsonSerializerOptions StrictCaseInsensitive { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
