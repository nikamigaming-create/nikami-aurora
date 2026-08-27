using Godot;

namespace OpenDAO.Domain.Sessions;

public sealed record PlayerSession(string SourceKey, string AreaId, Vector3 Position, float Yaw,
    float Pitch, long SavedAtUnix);
