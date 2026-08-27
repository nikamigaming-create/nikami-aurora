using OpenDAO.Application.Abstractions;
using OpenDAO.Domain.Abilities;
using OpenDAO.Domain.Characters;
using OpenDAO.Domain.Common;

namespace OpenDAO.Application.Characters;

public sealed class CharacterAbilityInitializer(
    ICharacterAbilityLoadoutProvider loadouts,
    IAbilityCatalog catalog,
    AbilityState state)
{
    public OperationResult Initialize(CharacterProfile character)
    {
        var loadout = loadouts.Resolve(character);
        if (loadout is null)
        {
            return OperationResult.Unsupported(loadouts.Error);
        }

        if (!catalog.Load(loadout.CatalogPath))
        {
            return OperationResult.Unsupported(catalog.Error,
                ("catalog", loadout.CatalogPath));
        }

        var prepared = new List<(CharacterAbilityGrant Grant, AbilityDefinition Definition)>();
        var slotted = new Dictionary<int, int>();
        foreach (var grant in loadout.Grants)
        {
            var definition = catalog.Find(grant.AbilityId);
            if (definition is null)
            {
                return OperationResult.Unsupported("character-ability-record-absent",
                    ("abilityId", grant.AbilityId), ("catalog", loadout.CatalogPath));
            }

            var provenance = new Dictionary<string, object?>(definition.Provenance)
            {
                ["grantBasis"] = "installed-character-loadout",
                ["grantTable"] = grant.SourceTable,
                ["grantEntry"] = grant.SourceEntry,
                ["grantTableSha256"] = grant.SourceSha256,
                ["grantColumnHash"] = grant.SourceColumnHash,
                ["grantRow"] = grant.SourceRow,
                ["quickSlot"] = grant.QuickSlot,
            };
            prepared.Add((grant, definition with { Provenance = provenance }));
        }

        var original = state.Snapshot();
        foreach (var preparedGrant in prepared)
        {
            var grant = preparedGrant.Grant;
            var result = state.Grant(preparedGrant.Definition, grant.QuickSlot);
            if (!result.Succeeded)
            {
                state.Restore(original);
                return result;
            }

            if (grant.QuickSlot > 0)
            {
                slotted[grant.QuickSlot] = grant.AbilityId;
            }
        }

        return OperationResult.Complete(
            ("catalog", loadout.CatalogPath),
            ("catalogSha256", loadout.CatalogSha256),
            ("classRow", loadout.ClassRow),
            ("backgroundRow", loadout.BackgroundRow),
            ("abilityIds", prepared.Select(value => value.Grant.AbilityId).ToArray()),
            ("quickSlots", slotted));
    }
}
