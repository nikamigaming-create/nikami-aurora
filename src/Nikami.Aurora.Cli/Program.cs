using System.Text.Json;
using System.Text.Json.Serialization;
using Nikami.Aurora.Core;
using Nikami.Aurora.Profiles.DragonAgeOrigins;
using Nikami.Aurora.Profiles.Kotor;
using Nikami.Aurora.Profiles.Kotor2;

namespace Nikami.Aurora.Cli;

internal static class Program
{
    private static readonly GameProfileRegistry Registry = new(new IGameProfile[]
    {
        new KotorGameProfile(),
        new Kotor2GameProfile(),
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
                "dao-effect-audit" => DaoEffectAudit(args[1..]),
                "dao-navigation-audit" => DaoNavigationAudit(args[1..]),
                "dao-character-import-audit" => DaoCharacterImportAudit(args[1..]),
                "dao-character-msh-audit" => DaoCharacterMshAudit(args[1..]),
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

    private static int DaoEffectAudit(string[] args)
    {
        string? root = null;
        string? effects = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root" when index + 1 < args.Length:
                    root = args[++index];
                    break;
                case "--effects" when index + 1 < args.Length:
                    effects = args[++index];
                    break;
                default:
                    return Usage($"Unknown or incomplete DAO effect-audit option: {args[index]}");
            }
        }
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(effects))
            return Usage("dao-effect-audit requires --root and --effects.");
        var result = DaoEffectAuditCommand.Run(root, effects.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return result.UnsupportedDefinitions == 0 ? 0 : 2;
    }

    private static int DaoCharacterImportAudit(string[] args)
    {
        string? root = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root" when index + 1 < args.Length:
                    root = args[++index];
                    break;
                default:
                    return Usage(
                        $"Unknown or incomplete DAO character-import-audit option: {args[index]}");
            }
        }
        if (string.IsNullOrWhiteSpace(root))
            return Usage("dao-character-import-audit requires --root.");
        var result = DaoCharacterImportAuditCommand.Run(root);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return result.FreshImportReady == result.Selections ? 0 : 2;
    }

    private static int DaoNavigationAudit(string[] args)
    {
        string? root = null;
        string? layouts = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root" when index + 1 < args.Length:
                    root = args[++index];
                    break;
                case "--layouts" when index + 1 < args.Length:
                    layouts = args[++index];
                    break;
                default:
                    return Usage(
                        $"Unknown or incomplete DAO navigation-audit option: {args[index]}");
            }
        }
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(layouts))
            return Usage("dao-navigation-audit requires --root and --layouts.");
        var result = DaoNavigationAuditCommand.Run(root, layouts.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return result.Unsupported == 0 ? 0 : 2;
    }

    private static int DaoCharacterMshAudit(string[] args)
    {
        string? root = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root" when index + 1 < args.Length:
                    root = args[++index];
                    break;
                default:
                    return Usage(
                        $"Unknown or incomplete DAO character-MSH-audit option: {args[index]}");
            }
        }
        if (string.IsNullOrWhiteSpace(root))
            return Usage("dao-character-msh-audit requires --root.");
        var result = DaoCharacterMshAuditCommand.Run(root);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return result.MeshesFailed == 0 ? 0 : 2;
    }

    private static int Usage(string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
            Console.Error.WriteLine(error);
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  nikami-aurora list-profiles");
        Console.Error.WriteLine("  nikami-aurora probe --profile <id> --root <game-directory>");
        Console.Error.WriteLine("  nikami-aurora dao-effect-audit --root <game-directory> --effects <csv>");
        Console.Error.WriteLine("  nikami-aurora dao-navigation-audit --root <game-directory> --layouts <csv>");
        Console.Error.WriteLine("  nikami-aurora dao-character-import-audit --root <game-directory>");
        Console.Error.WriteLine("  nikami-aurora dao-character-msh-audit --root <game-directory>");
        return string.IsNullOrWhiteSpace(error) ? 0 : 64;
    }
}
