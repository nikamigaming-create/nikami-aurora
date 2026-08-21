using System.Text.Json;
using System.Text.Json.Serialization;
using Nikami.Aurora.Core;
using Nikami.Aurora.Profiles.DragonAgeOrigins;
using Nikami.Aurora.Profiles.Kotor;

namespace Nikami.Aurora.Cli;

internal static class Program
{
    private static readonly GameProfileRegistry Registry = new(new IGameProfile[]
    {
        new KotorGameProfile(),
        new DragonAgeOriginsGameProfile()
    });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                return Usage("A command is required.");

            return args[0].ToLowerInvariant() switch
            {
                "list-profiles" => ListProfiles(),
                "probe" => Probe(args[1..]),
                "help" or "--help" or "-h" => Usage(),
                _ => Usage($"Unknown command: {args[0]}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"nikami-aurora: {exception.Message}");
            return 1;
        }
    }

    private static int ListProfiles()
    {
        var descriptors = Registry.All.Select(profile => profile.Descriptor).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(descriptors, JsonOptions));
        return 0;
    }

    private static int Probe(string[] args)
    {
        string? profileId = null;
        string? root = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--profile" when index + 1 < args.Length:
                    profileId = args[++index];
                    break;
                case "--root" when index + 1 < args.Length:
                    root = args[++index];
                    break;
                default:
                    return Usage($"Unknown or incomplete probe option: {args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(root))
            return Usage("probe requires --profile and --root.");

        var result = GameInstallProber.Probe(Registry.Get(profileId), root);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return result.IsValid ? 0 : 2;
    }

    private static int Usage(string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
            Console.Error.WriteLine(error);
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  nikami-aurora list-profiles");
        Console.Error.WriteLine("  nikami-aurora probe --profile <id> --root <game-directory>");
        return string.IsNullOrWhiteSpace(error) ? 0 : 64;
    }
}
