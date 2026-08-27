namespace OpenDAO.Domain.Abilities;

public sealed record CharacterAbilityGrant(
    int AbilityId,
    int QuickSlot,
    string SourceTable,
    string SourceEntry,
    string SourceSha256,
    long SourceColumnHash,
    string SourceRow);

public sealed record CharacterAbilityLoadout(
    string CatalogPath,
    string CatalogSha256,
    string ClassRow,
    string BackgroundRow,
    IReadOnlyList<CharacterAbilityGrant> Grants);
