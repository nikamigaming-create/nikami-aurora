namespace Nikami.Aurora.Profiles.Kotor;

public sealed record KotorCombatExperienceRow(
    int PlayerLevel,
    IReadOnlyList<int> Rewards);

public sealed class KotorCombatExperienceTable
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> rows;

    public KotorCombatExperienceTable(
        string sourceSha256,
        IReadOnlyList<KotorCombatExperienceRow> sourceRows)
    {
        if (sourceSha256?.Length != 64 || !sourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Combat XP table requires a source hash", nameof(sourceSha256));
        if (sourceRows is null || sourceRows.Count == 0 ||
            sourceRows.Any(row => row.PlayerLevel < 1 || row.Rewards.Count == 0 ||
                                  row.Rewards.Any(reward => reward < 0)) ||
            sourceRows.Select(row => row.PlayerLevel).Distinct().Count() != sourceRows.Count ||
            sourceRows.Select(row => row.Rewards.Count).Distinct().Count() != 1)
            throw new ArgumentException("Combat XP table rows are invalid", nameof(sourceRows));
        SourceSha256 = sourceSha256.ToUpperInvariant();
        rows = sourceRows.ToDictionary(row => row.PlayerLevel, row => row.Rewards);
    }

    public string SourceSha256 { get; }

    public int RewardFor(int playerLevel, double challengeRating)
    {
        if (!rows.TryGetValue(playerLevel, out var rewards))
            throw new ArgumentOutOfRangeException(nameof(playerLevel));
        var challenge = checked((int)challengeRating);
        if (challengeRating != challenge || challenge < 0 || challenge >= rewards.Count)
            throw new NotSupportedException(
                $"Fractional or out-of-range challenge rating is not supported: {challengeRating}");
        return rewards[challenge];
    }
}

public sealed record KotorDamageComponent(
    int DiceCount,
    int DieSides,
    int FlatDamage,
    int DamageType,
    bool MultipliesOnCritical = false)
{
    public KotorDamageComponent Validate()
    {
        if (DiceCount < 0 || DieSides < 0 || FlatDamage < 0 || DamageType < 0 ||
            (DiceCount == 0) != (DieSides == 0) ||
            DiceCount == 0 && FlatDamage == 0)
            throw new ArgumentOutOfRangeException(nameof(DiceCount));
        return this;
    }
}

public sealed record KotorCombatWeaponDefinition(
    string Resref,
    int AttackModifier,
    int CriticalThreat,
    int CriticalMultiplier,
    bool Ranged,
    IReadOnlyList<KotorDamageComponent> Damage)
{
    public KotorCombatWeaponDefinition Validate()
    {
        if (string.IsNullOrWhiteSpace(Resref) || CriticalThreat is < 1 or > 20 ||
            CriticalMultiplier < 1 || Damage is null || Damage.Count == 0)
            throw new ArgumentException("Combat weapon definition is invalid", nameof(Resref));
        return this with { Damage = Damage.Select(component => component.Validate()).ToArray() };
    }
}

public sealed record KotorCombatantDefinition(
    string Id,
    int FactionId,
    int CurrentHitPoints,
    int MaximumHitPoints,
    int Defense,
    int AttackBonus,
    double ChallengeRating,
    bool MinimumOneHitPoint,
    bool GrantsExperience,
    KotorCombatWeaponDefinition Weapon)
{
    public KotorCombatantDefinition Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || MaximumHitPoints <= 0 ||
            CurrentHitPoints is < 0 || CurrentHitPoints > MaximumHitPoints || Defense < 0 ||
            !double.IsFinite(ChallengeRating) || ChallengeRating < 0)
            throw new ArgumentException("Combatant definition is invalid", nameof(Id));
        return this with { Weapon = Weapon.Validate() };
    }
}

public sealed record KotorCombatantSnapshot(
    string Id,
    int FactionId,
    int CurrentHitPoints,
    int MaximumHitPoints,
    bool IsDead);

public abstract record KotorCombatEvent;
public sealed record KotorAttackQueued(string AttackerId, string TargetId) : KotorCombatEvent;
public sealed record KotorAttackCancelled(string AttackerId, string TargetId) : KotorCombatEvent;
public sealed record KotorAttackResolved(
    string AttackerId,
    string TargetId,
    int D20,
    int AttackTotal,
    int TargetDefense,
    bool Hit,
    bool Critical,
    int Damage,
    int HitPointsBefore,
    int HitPointsAfter) : KotorCombatEvent;
public sealed record KotorCombatantDied(string Id, int ExperienceReward) : KotorCombatEvent;

public sealed record KotorCombatTransition(
    IReadOnlyList<KotorCombatEvent> Events,
    IReadOnlyDictionary<string, KotorCombatantSnapshot> Combatants,
    int AwardedExperience);

public sealed class KotorCombatSimulation
{
    private readonly Dictionary<string, KotorCombatantDefinition> combatants;
    private readonly KotorCombatExperienceTable experience;
    private (string Attacker, string Target)? queuedAttack;

    public KotorCombatSimulation(
        IReadOnlyList<KotorCombatantDefinition> definitions,
        KotorCombatExperienceTable experience)
    {
        if (definitions is null || definitions.Count < 2)
            throw new ArgumentException("Combat requires at least two combatants", nameof(definitions));
        this.experience = experience ?? throw new ArgumentNullException(nameof(experience));
        var validated = definitions.Select(definition => definition.Validate()).ToArray();
        if (validated.Select(definition => definition.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            validated.Length)
            throw new ArgumentException("Combatant identifiers must be unique", nameof(definitions));
        combatants = validated.ToDictionary(definition => definition.Id, StringComparer.OrdinalIgnoreCase);
    }

    public KotorCombatTransition QueueAttack(string attackerId, string targetId)
    {
        var attacker = RequireLiving(attackerId);
        var target = RequireLiving(targetId);
        if (attacker.FactionId == target.FactionId)
            throw new InvalidOperationException("Friendly targets cannot be attacked");
        queuedAttack = (attacker.Id, target.Id);
        return Transition([new KotorAttackQueued(attacker.Id, target.Id)], 0);
    }

    public KotorCombatTransition CancelAttack()
    {
        if (queuedAttack is not { } attack) return Transition([], 0);
        queuedAttack = null;
        return Transition([new KotorAttackCancelled(attack.Attacker, attack.Target)], 0);
    }

    public KotorCombatTransition ResolveNextAttack(
        int playerLevel,
        int d20,
        IReadOnlyList<int> damageRolls)
    {
        if (queuedAttack is not { } attack)
            throw new InvalidOperationException("No combat attack is queued");
        if (d20 is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(d20));
        var attacker = RequireLiving(attack.Attacker);
        var target = RequireLiving(attack.Target);
        var requiredRolls = attacker.Weapon.Damage.Sum(component => component.DiceCount);
        if (damageRolls is null || damageRolls.Count != requiredRolls)
            throw new ArgumentException("Damage roll count does not match the weapon", nameof(damageRolls));

        queuedAttack = null;
        var attackTotal = checked(d20 + attacker.AttackBonus + attacker.Weapon.AttackModifier);
        var hit = d20 == 20 || d20 != 1 && attackTotal >= target.Defense;
        var critical = hit && d20 >= 21 - attacker.Weapon.CriticalThreat;
        var damage = 0;
        if (hit)
        {
            var roll = 0;
            foreach (var component in attacker.Weapon.Damage)
            {
                var componentDamage = component.FlatDamage;
                for (var index = 0; index < component.DiceCount; index++)
                {
                    var value = damageRolls[roll++];
                    if (value is < 1 || value > component.DieSides)
                        throw new ArgumentOutOfRangeException(nameof(damageRolls));
                    componentDamage = checked(componentDamage + value);
                }
                if (critical && component.MultipliesOnCritical)
                    componentDamage = checked(
                        componentDamage * attacker.Weapon.CriticalMultiplier);
                damage = checked(damage + componentDamage);
            }
        }

        var before = target.CurrentHitPoints;
        var floor = target.MinimumOneHitPoint ? 1 : 0;
        var after = Math.Max(floor, checked(before - damage));
        combatants[target.Id] = target with { CurrentHitPoints = after };
        var events = new List<KotorCombatEvent>
        {
            new KotorAttackResolved(attacker.Id, target.Id, d20, attackTotal,
                target.Defense, hit, critical, damage, before, after)
        };
        var awarded = 0;
        if (before > 0 && after == 0)
        {
            awarded = target.GrantsExperience
                ? experience.RewardFor(playerLevel, target.ChallengeRating)
                : 0;
            events.Add(new KotorCombatantDied(target.Id, awarded));
        }
        return Transition(events, awarded);
    }

    public IReadOnlyDictionary<string, KotorCombatantSnapshot> CaptureSnapshot() =>
        combatants.ToDictionary(
            pair => pair.Key,
            pair => new KotorCombatantSnapshot(pair.Value.Id, pair.Value.FactionId,
                pair.Value.CurrentHitPoints, pair.Value.MaximumHitPoints,
                pair.Value.CurrentHitPoints == 0),
            StringComparer.OrdinalIgnoreCase);

    private KotorCombatantDefinition RequireLiving(string id)
    {
        if (!combatants.TryGetValue(id, out var combatant))
            throw new KeyNotFoundException($"Combatant is unknown: {id}");
        if (combatant.CurrentHitPoints == 0)
            throw new InvalidOperationException($"Combatant is dead: {id}");
        return combatant;
    }

    private KotorCombatTransition Transition(IReadOnlyList<KotorCombatEvent> events, int awarded) =>
        new(events, CaptureSnapshot(), awarded);
}
