using Godot;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Application.Characters;
using Nikami.Aurora.GodotRuntime.Domain.Characters;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

public sealed class CachedDaoCharacterCinematicModelResolver : ICharacterCinematicModelResolver
{
    public CharacterCinematicModels Resolve(CharacterProfile character, string standingModelPath)
    {
        if (string.IsNullOrWhiteSpace(standingModelPath))
            return new CharacterCinematicModels(string.Empty, string.Empty);
        var resolution = CachedDaoCharacterAppearanceCatalog.Resolve(
            character.Race, character.Gender, character.Appearance);
        if (!resolution.IsReady || resolution.Appearance is not { } authored)
        {
            GD.PushWarning($"OPENDAO_CINEMATIC_CHARACTER_IDENTITY status=unsupported " +
                           $"race={character.Race} gender={character.Gender} " +
                           $"appearance={character.Appearance} availability={resolution.Availability} " +
                           $"reason={resolution.Failure} npc_substitution=0");
            return new CharacterCinematicModels(string.Empty, string.Empty);
        }

        var requestedStanding = Path.GetFullPath(standingModelPath);
        var authoredStanding = Path.GetFullPath(resolution.StandingPath);
        if (!requestedStanding.Equals(authoredStanding, StringComparison.OrdinalIgnoreCase))
        {
            GD.PushError($"OPENDAO_CINEMATIC_CHARACTER_IDENTITY status=fail " +
                         $"reason=standing-selection-join-mismatch selection={authored.SelectionKey} " +
                         $"requested={requestedStanding} authored={authoredStanding}");
            return new CharacterCinematicModels(string.Empty, string.Empty);
        }

        GD.Print($"OPENDAO_CINEMATIC_CHARACTER_IDENTITY status=ready " +
                 $"character={character.Name} selection={authored.SelectionKey} " +
                 $"morph={authored.MorphResource} morph_sha256={authored.MorphSha256} " +
                 $"provenance={resolution.Provenance} standing={resolution.StandingPath} " +
                 $"bed={resolution.BedPath} identity_join=pass parity_claim=none");
        return new CharacterCinematicModels(resolution.StandingPath, resolution.BedPath);
    }
}
