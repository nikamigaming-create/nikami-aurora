namespace OpenDAO.Domain.Story;

public abstract record StoryOperation(int Handle);
public sealed record SetActive(int Handle, bool Value) : StoryOperation(Handle);
public sealed record SetInteractive(int Handle, bool Value) : StoryOperation(Handle);
public sealed record SetIdentity(int Handle, string? Tag = null, string? DisplayName = null) : StoryOperation(Handle);
public sealed record SetPosition(int Handle, StoryPosition Value, bool Safe) : StoryOperation(Handle);
public sealed record SetFacing(int Handle, float Value) : StoryOperation(Handle);
public sealed record SetProtection(int Handle, bool? Plot = null, bool? Immortal = null) : StoryOperation(Handle);
public sealed record SetGroupTeam(int Handle, int? GroupId = null, int? TeamId = null) : StoryOperation(Handle);
public sealed record DestroyObject(int Handle) : StoryOperation(Handle);

public sealed record StoryCommit(long Revision, IReadOnlyList<StoryOperation> Operations,
    IReadOnlyDictionary<int, StoryObject> ChangedObjects);
