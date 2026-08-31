using Nikami.Aurora.GodotRuntime.Domain.Characters;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public sealed record LocomotionAnimationSet(
    string BankPath,
    string Idle,
    string Walk,
    string Run);

public interface ILocomotionAnimationProvider
{
    LocomotionAnimationSet? Resolve(CharacterProfile character);
}
