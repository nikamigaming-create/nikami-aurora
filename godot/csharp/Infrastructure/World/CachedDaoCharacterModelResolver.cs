using Godot;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Application.Characters;
using Nikami.Aurora.GodotRuntime.Domain.Characters;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

public sealed class CachedDaoCharacterModelResolver : ICharacterModelResolver
{
    public string Resolve(CharacterProfile character)
    {
        var resolution = CachedDaoCharacterAppearanceCatalog.Resolve(
            character.Race, character.Gender, character.Appearance);
        if (resolution.IsReady && resolution.Appearance is { } authored)
        {
            GD.Print($"OPENDAO_CHARACTER_APPEARANCE status=ready " +
                     $"selection={authored.SelectionKey} morph={authored.MorphResource} " +
                     $"morph_sha256={authored.MorphSha256} provenance={resolution.Provenance} " +
                     $"fresh_import={(resolution.Availability == DaoCharacterAppearanceAvailability.FreshImport ? 1 : 0)} " +
                     "runtime_ready=1 " +
                     $"release_ready={(resolution.Availability == DaoCharacterAppearanceAvailability.FreshImport ? 1 : 0)} " +
                     $"parity_claim=none path={resolution.StandingPath}");
            return resolution.StandingPath;
        }
        GD.PushWarning($"OPENDAO_CHARACTER_APPEARANCE status=unsupported " +
                       $"race={character.Race} gender={character.Gender} " +
                       $"appearance={character.Appearance} availability={resolution.Availability} " +
                       $"reason={resolution.Failure} npc_substitution=0");
        return string.Empty;
    }
}
