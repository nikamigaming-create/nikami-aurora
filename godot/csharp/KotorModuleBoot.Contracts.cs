using System.Text.Json.Serialization;
using Godot;
using Nikami.Aurora.Profiles.Kotor;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class KotorModuleBoot
{
    private sealed record ModuleManifest(
        string Schema,
        string Module,
        string ContentMode,
        string MissingSourceAssetPolicy,
        string AreaResRef,
        EntryRecord Entry,
        TargetRecord Target,
        AreaLightingRecord Lighting,
        CameraStyleRecord CameraStyle,
        KotorRuntimeConfiguration RuntimeConfiguration,
        KotorUiRecord Ui,
        PlayerRecord Player,
        IReadOnlyList<RoomRecord> Rooms,
        IReadOnlyList<EnvironmentMapRecord> EnvironmentMaps,
        IReadOnlyList<UnresolvedTextureReferenceRecord> UnresolvedTextureReferences,
        IReadOnlyList<CreatureRecord> Creatures,
        IReadOnlyList<DoorRecord> Doors,
        IReadOnlyList<PlaceableRecord> Placeables,
        IReadOnlyList<TriggerRecord> Triggers,
        IReadOnlyList<CameraRecord> Cameras,
        FirstEncounterRecord? FirstEncounter,
        IReadOnlyList<ScriptContractRecord> ScriptContracts,
        CountRecord Counts);

    private sealed record UnresolvedTextureReferenceRecord(
        string Room,
        string? DiffuseTexture,
        string? LightmapTexture,
        string? BumpMapTexture,
        bool MissingDiffuse,
        bool MissingLightmap,
        bool MissingBumpMap,
        int MeshCount);

    private sealed record EnvironmentMapRecord(
        string Schema,
        string Resref,
        string SourceSha256,
        int SourceByteCount,
        string SourceType,
        string SourceTxi,
        IReadOnlyList<string> FaceOrder,
        string SampleBasis,
        IReadOnlyList<EnvironmentMapFaceRecord> Faces);
    private sealed record EnvironmentMapFaceRecord(
        int Layer,
        string Face,
        string RowTransform,
        string Path,
        string PayloadSha256,
        int ByteCount,
        int Width,
        int Height);

    private sealed record EntryRecord(IReadOnlyList<float> Position, float DirectionRadians);
    private sealed record TargetRecord(string ExecutableSha256);
    private sealed record AreaLightingRecord(IReadOnlyList<float> DynamicAmbient, bool Shadows,
        int ShadowOpacity, string SourceSha256);
    private sealed record CameraStyleRecord(int Id, float ViewAngle, float Distance,
        float PitchDegrees, float Height, string SourceSha256);
    private sealed record PlayerRecord(string Schema, string Glb, int PortraitId,
        int AppearanceId, string AppearanceLabel, string BodyModel, string BodyTexture,
        int HeadIndex, string HeadModel, float Height, float WalkDistance, float RunDistance,
        IReadOnlyList<float>? TalkOffset, IReadOnlyList<float>? CameraOffset,
        PlayerAnimationRecord Animation,
        IReadOnlyList<PlayerEquipmentVariantRecord>? EquipmentVariants);
    private sealed record PlayerAnimationRecord(int MeshCount, int VertexCount, int TriangleCount,
        int SkinCount, int HeadSkinCount, IReadOnlyList<string> Animations,
        IReadOnlyList<float>? BoundsMinimum = null,
        IReadOnlyList<float>? BoundsMaximum = null,
        IReadOnlyList<float>? Extent = null);
    private sealed record PlayerEquipmentVariantRecord(
        string Schema,
        string Id,
        string Glb,
        string? ArmorResref,
        string? LeftHandResref,
        string? RightHandResref,
        string BodyModel,
        string BodyTexture,
        string HeadModel,
        string? WeaponModel,
        string? WeaponHook,
        IReadOnlyList<float>? TalkOffset,
        IReadOnlyList<float>? CameraOffset,
        PlayerAnimationRecord Animation,
        string? ArmorUtiSha256,
        string? LeftHandUtiSha256,
        string? RightHandUtiSha256,
        string BaseItemsSha256);
    private sealed record RoomRecord(string Model, string? Glb, IReadOnlyList<float> Position,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<float>>>? WalkmeshTriangles,
        IReadOnlyList<LightRecord>? Lights,
        IReadOnlyList<RoomEmitterRecord>? Emitters,
        bool SourcePlaceholder = false);
    private sealed record RoomEmitterRecord(
        string Schema,
        string NodePath,
        IReadOnlyList<float> AuthoredPosition,
        IReadOnlyList<float> Position,
        IReadOnlyList<float> Direction,
        IReadOnlyList<float> BasisRight,
        IReadOnlyList<float> BasisUp,
        IReadOnlyList<float> BasisForward,
        FirstEncounterEffectTexture Texture,
        string Update,
        string Render,
        string Blend,
        int Flags,
        int SpawnType,
        int Loop,
        int TwoSidedTexture,
        int RenderOrder,
        int FrameBlender,
        string DepthTexture,
        int AuthoredXGrid,
        int AuthoredYGrid,
        int XGrid,
        int YGrid,
        float BirthRate,
        float RandomBirthRate,
        float Velocity,
        float RandomVelocity,
        float XSize,
        float YSize,
        float SpawnWidthMeters,
        float SpawnHeightMeters,
        IReadOnlyList<float>? PointToPointTargetPosition,
        float Gravity,
        float Mass,
        float ParticleRotation,
        float SpreadRadians,
        float LifeExpectancy,
        IReadOnlyList<float> ColorStart,
        IReadOnlyList<float> ColorMid,
        IReadOnlyList<float> ColorEnd,
        float PercentStart,
        float PercentMid,
        float PercentEnd,
        float AlphaStart,
        float AlphaMid,
        float AlphaEnd,
        float SizeStart,
        float SizeMid,
        float SizeEnd,
        float FrameStart,
        float FrameEnd,
        float Fps,
        float BlurLength,
        float BounceCoefficient);
    private sealed record LightRecord(string Name, IReadOnlyList<float> Position,
        IReadOnlyList<float> Color, float Radius, float Multiplier, bool AmbientOnly,
        int DynamicType, bool AffectDynamic, bool Shadow, int Priority);
    private sealed record CameraRecord(int Id, IReadOnlyList<float> Position, float Height, float Fov,
        float PitchDegrees, IReadOnlyList<float> OrientationWxyz, IReadOnlyList<float> Forward,
        IReadOnlyList<float> Up);
    private sealed record FirstEncounterRecord(
        string Schema,
        string DoorTag,
        FirstEncounterSceneObject SceneObject,
        IReadOnlyList<FirstEncounterParticipant> Participants,
        IReadOnlyList<PlaceableRecord> EnvironmentPlaceables,
        IReadOnlyList<FirstEncounterWaypoint> PartyWaypoints,
        IReadOnlyList<int> CameraIds,
        FirstEncounterAnimationIds AnimationIds,
        FirstEncounterEffects Effects,
        FirstEncounterTiming TimingSeconds,
        FirstEncounterAudio Audio,
        IReadOnlyList<FirstEncounterScript> Scripts);
    private sealed record FirstEncounterSceneObject(
        string Template,
        string Tag,
        IReadOnlyList<float> Position,
        float Bearing,
        string Conversation,
        string OnUserDefined,
        string UtpSha256,
        DialogueReference Dialogue);
    private sealed record FirstEncounterParticipant(
        string Template,
        string Tag,
        IReadOnlyList<float> Position,
        float Bearing,
        string Glb,
        int FactionId,
        int HitPoints,
        int CurrentHitPoints,
        int MaxHitPoints,
        bool MinimumOneHitPoint,
        bool NoPermanentDeath,
        PlayerAnimationRecord Animation);
    private sealed record FirstEncounterWaypoint(
        string Template,
        string Tag,
        IReadOnlyList<float> Position,
        float Bearing);
    private sealed record FirstEncounterAnimationIds(
        int Damage,
        int CutsceneAttack,
        int TraskFirstLine,
        int TraskCharge);
    private sealed record FirstEncounterTiming(
        float CameraSwitch,
        float BattleMusic,
        float FirstControlResume,
        float SecondAttack,
        float ThirdAttack);
    private sealed record FirstEncounterEffects(
        string Schema,
        string ProjectileModel,
        string ProjectileMdlSha256,
        string ProjectileMdxSha256,
        string MuzzleModel,
        string MuzzleMdlSha256,
        string MuzzleMdxSha256,
        float ProjectileSize,
        string ProjectileUpdate,
        string ProjectileRender,
        string ProjectileBlend,
        int ProjectileFlags,
        float ProjectileBlurLength,
        float ProjectileLifeExpectancy,
        float MuzzleSize,
        float MuzzleLifetime,
        IReadOnlyList<FirstEncounterMuzzleEmitter> MuzzleEmitters,
        FirstEncounterEffectTexture LaserTexture,
        FirstEncounterEffectTexture MuzzleTexture,
        FirstEncounterEffectTexture FlareTexture);
    private sealed record FirstEncounterMuzzleEmitter(
        string Node,
        IReadOnlyList<float> Position,
        IReadOnlyList<float> BasisRight,
        IReadOnlyList<float> BasisUp,
        IReadOnlyList<float> BasisForward,
        string TextureResref,
        string Update,
        string Render,
        string Blend,
        int Flags,
        float Size,
        float Lifetime,
        IReadOnlyList<float> Color,
        float Alpha);
    private sealed record FirstEncounterEffectTexture(
        string Resref,
        string Path,
        string SourceSha256,
        int SourceByteCount,
        string SourceType,
        string SourceTxi,
        string PayloadSha256,
        int ByteCount);
    private sealed record FirstEncounterAudio(
        string AmmunitionTypesSha256,
        string AmbientMusicSha256,
        int StandardMusicId,
        int BattleMusicId,
        int MusicDelayMilliseconds,
        FirstEncounterAudioSource BlasterShot,
        FirstEncounterAudioSource BlasterImpact,
        FirstEncounterAudioSource BackgroundMusic,
        FirstEncounterAudioSource BattleMusic);
    private sealed record FirstEncounterAudioSource(
        string Resref,
        string Path,
        string Format,
        string SourceSha256,
        int SourceByteCount,
        string SourceEncoding,
        string PayloadSha256,
        int ByteCount,
        string PayloadEncoding);
    private sealed record FirstEncounterScript(
        string Resref,
        string SourceSha256,
        int InstructionCount);
    private sealed record FirstEncounterAudioStreams(
        AudioStream BlasterShot,
        AudioStream BlasterImpact,
        AudioStream BackgroundMusic,
        AudioStream BattleMusic);
    private sealed record FirstEncounterEffectTextures(
        Texture2D Laser,
        Texture2D Muzzle,
        Texture2D Flare,
        float ProjectileSize,
        float ProjectileBlurLength,
        float MuzzleSize,
        float MuzzleLifetime,
        IReadOnlyList<FirstEncounterMuzzleEmitter> MuzzleEmitters);
    private sealed record CreatureRecord(string Template, string? Tag,
        IReadOnlyList<float> Position, float Bearing,
        string? Glb, string? Conversation, DialogueReference? Dialogue,
        IReadOnlyList<float>? TalkOffset,
        string? RenderStatus = null,
        string? SourceTemplate = null,
        string? UtcSha256 = null,
        string? IdleAnimation = null,
        IReadOnlyList<float>? RenderExtent = null,
        PlayerAnimationRecord? Animation = null,
        IReadOnlyList<CreatureModelRecord>? Models = null);
    private sealed record CreatureModelRecord(
        string Role,
        string Model,
        string? OverrideTexture,
        string MdlSha256,
        string MdxSha256,
        int RenderSurfaces,
        int AdditiveSurfaces,
        int EmitterNodes,
        int LightNodes);
    private sealed record DoorRecord(string Template, string Tag, IReadOnlyList<float> Position, float Bearing,
        string LinkedToModule, string? Glb, string? Model, string? Conversation, string? OnOpen,
        bool Locked, bool KeyRequired);
    private sealed record PlaceableRecord(string Template, string Tag,
        IReadOnlyList<float> Position, float Bearing, string? Glb, string? Model,
        string? OnInventory, bool Locked, bool Static, bool Useable, bool HasInventory,
        int AnimationState,
        string? BaseItemsSha256,
        IReadOnlyList<ItemStackRecord>? Inventory);
    private sealed record TriggerRecord(
        string Template,
        string Tag,
        IReadOnlyList<float> Position,
        IReadOnlyList<IReadOnlyList<float>> Geometry,
        string? OnEnter,
        float HighlightHeight,
        string UttSha256);
    private sealed record ItemStackRecord(
        string Resref,
        string DisplayName,
        string Tag,
        int BaseItem,
        int Charges,
        int StackSize,
        int ModelVariation,
        int BodyVariation,
        int TextureVariation,
        int EquipableSlots,
        string ItemClass,
        int ModelType,
        string DefaultModel,
        string DefaultIcon,
        string UtiSha256,
        int Quantity,
        bool Droppable,
        bool Infinite);
    private sealed record DialogueReference(string Path, string SourceSha256, int StarterCount,
        int NodeCount, int OpeningStarter);
    private sealed record DialogueGraph(string Schema, int OpeningStarter,
        IReadOnlyList<DialogueLink> Starters, IReadOnlyDictionary<string, DialogueNode> Nodes);
    private sealed record DialogueNode(string Kind, string Text, string Speaker, string Sound,
        string Voice,
        string Script1, string Script2,
        int CameraAngle, int? CameraId, float? CameraFov, float? CameraHeight,
        IReadOnlyList<DialogueAnimation> Animations, DialogueMedia? Media,
        IReadOnlyList<DialogueLink> Links);
    private sealed record DialogueAnimation(
        int AnimationId,
        string AnimationName,
        bool Looping,
        bool FireForget,
        string Participant);
    private sealed record DialogueMedia(string AudioPath, string AudioFormat, string AudioSha256,
        int AudioByteCount, string? LipPath, string? LipSourceSha256, float? LipLength,
        int? LipFrameCount);
    private sealed record LipTrack(string Schema, string Resref, string SourceSha256, float Length,
        IReadOnlyList<LipFrame> Frames);
    private sealed record LipFrame(float Time, int Shape);
    private sealed record ScriptContractRecord(string Schema, string Resref, string Kind,
        string SourceSha256, int InstructionCount, string? DoorTag, int? RequiredPlayerXp,
        string? PlotLabel, int? PlotPercentage, int? PlotBaseXp, int? AwardedXp,
        bool? PauseConversation, string? MoveTargetTag, bool? MoveRun, float? MoveRange,
        bool? ResumeConversation, string? TriggerTemplate, string? GlobalName, int? GlobalValue,
        string? ActorTag, int? UserEvent, float? InputLockSeconds, float? DelaySeconds,
        string? Conversation, int? DialogueStarter, string? ActorScriptSourceSha256,
        int? ActorScriptInstructionCount, string? ConditionScriptSourceSha256,
        int? ConditionScriptInstructionCount);
    private sealed class LipRig(KotorLipModifier modifier, Animation animation,
        IReadOnlyList<KotorLipModifier.TrackBinding> tracks)
    {
        public KotorLipModifier Modifier { get; } = modifier;
        public Animation Animation { get; } = animation;
        public IReadOnlyList<KotorLipModifier.TrackBinding> Tracks { get; } = tracks;
        public int TrackCount => Tracks.Sum(track =>
            (track.PositionTrack >= 0 ? 1 : 0) + (track.RotationTrack >= 0 ? 1 : 0));
    }
    private sealed record DialogueLink(string Target, string Condition1, bool Condition1Not,
        string Condition2, bool Condition2Not, int Logic);
    private sealed record CountRecord(int Rooms, int Creatures, int Doors, int Waypoints, int Cameras,
        int Placeables, int Triggers, int WalkmeshTriangles, int AuthoredLights,
        int AuthoredEmitters, int MaterialSurfaces, int ResolvedDiffuseTextures,
        int ResolvedLightmaps, int ResolvedBumpMaps, int EnvironmentMaps,
        int UnresolvedTextureReferences, int AuthoredBumpMapScaleSurfaces = 0,
        int SourceDecalSurfaces = 0, int SourceRoomPlaceholders = 0,
        int AdditiveEnvironmentSurfaces = 0,
        int AdditiveLightmappedSurfaces = 0,
        int UniqueCreatureTemplates = 0,
        int RenderReadyCreatures = 0,
        int UnsupportedCreatures = 0,
        int AuthoredCreatureModels = 0,
        int EquippedWeaponModels = 0,
        int EquippedWeaponAdditiveSurfaces = 0);
    private readonly record struct NavigationTriangle(Vector3 A, Vector3 B, Vector3 C);
    private readonly record struct StaticMaterialReport(
        int LightmappedOpaque,
        int BaseOpaque,
        int LightmappedTransparent,
        int BaseTransparent,
        int SourceAdditive,
        int SourceDecal,
        int EnhancedPbr,
        int NormalMapped,
        int AuthoredNormalScale,
        int AdditiveEnvironment,
        int AdditiveLightmapped);
    private readonly record struct RoomEmitterReport(
        int Total, int Alpha, int Additive, int Single, int FiniteSingle,
        int Oriented, int OrientedAlpha, int NormalizedGrid, int Distributed,
        int Tinted, int SoftFade, int DepthAware, int AtlasRangeValidated,
        int VisualSafetyValidated, float MaximumSmokeQuadExtent,
        float MaximumSparkTrailExtent, int Smoke, int Spark, int PointToPoint,
        int CollisionBounce, int ParticleCollisionRooms,
        int ParticleCollisionWalkmeshTriangles,
        IReadOnlyList<float> BounceCoefficients,
        bool DamagedEnd);
    private readonly record struct ParticleCollisionReport(
        int Rooms,
        int WalkmeshTriangles);
    private readonly record struct AuthoredLightReport(
        int DynamicMaterialized,
        int BakedOnly,
        int AmbientOnly,
        int Disabled)
    {
        public int Classified =>
            DynamicMaterialized + BakedOnly + AmbientOnly + Disabled;
    }
    private readonly record struct DynamicMaterialReport(
        int Surfaces,
        int EnhancedPbr,
        int NormalMapped,
        int Transparent,
        int Additive,
        int ConfiguredAdditive,
        int AuthoredNormalScale);
    private sealed class InteractiveDoor(
        string instanceId,
        DoorRecord source,
        Node3D model,
        Vector3 closedPosition)
    {
        public string InstanceId { get; } = instanceId;
        public DoorRecord Source { get; } = source;
        public Node3D Model { get; } = model;
        public Vector3 ClosedPosition { get; } = closedPosition;
    }
    private sealed class MaterializedPlaceable(
        string instanceId,
        PlaceableRecord source,
        Node3D model)
    {
        public string InstanceId { get; } = instanceId;
        public PlaceableRecord Source { get; } = source;
        public Node3D Model { get; } = model;
    }
    private sealed class MaterializedCreature(
        CreatureRecord source,
        Node3D model)
    {
        public CreatureRecord Source { get; } = source;
        public Node3D Model { get; } = model;
    }
}
