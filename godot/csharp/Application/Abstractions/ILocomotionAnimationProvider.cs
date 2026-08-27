using OpenDAO.Domain.Characters;

namespace OpenDAO.Application.Abstractions;

public sealed record LocomotionAnimationSet(
    string BankPath,
    string Idle,
    string Walk,
    string Run);

public interface ILocomotionAnimationProvider
{
    LocomotionAnimationSet? Resolve(CharacterProfile character);
}
