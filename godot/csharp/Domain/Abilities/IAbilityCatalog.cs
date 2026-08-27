namespace OpenDAO.Domain.Abilities;

public interface IAbilityCatalog
{
    string SourcePath { get; }
    string TableSha256 { get; }
    string Error { get; }
    bool Load(string path);
    AbilityDefinition? Find(int abilityId);
}
