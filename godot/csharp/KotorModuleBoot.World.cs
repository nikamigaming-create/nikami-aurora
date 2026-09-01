using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Nikami.Aurora.Core;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;
using Nikami.Aurora.Profiles.Kotor;
using NumericsVector3 = System.Numerics.Vector3;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class KotorModuleBoot
{
    private void AddCreatureMarkers(IEnumerable<CreatureRecord> creatures)
    {
        foreach (var creature in creatures)
        {
            if (!string.IsNullOrWhiteSpace(creature.Glb)) continue;
            var isTrask = creature.Template.Equals("end_trask", StringComparison.OrdinalIgnoreCase);
            var isCarth = creature.Template.StartsWith("p_carth", StringComparison.OrdinalIgnoreCase);
            var material = new StandardMaterial3D
            {
                AlbedoColor = isTrask
                    ? new Color(0.2f, 0.95f, 0.45f, 0.9f)
                    : isCarth
                        ? new Color(0.2f, 0.7f, 1.0f, 0.9f)
                        : new Color(0.95f, 0.22f, 0.18f, 0.55f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha
            };
            var marker = new MeshInstance3D
            {
                Name = $"Authored_{creature.Template}",
                Mesh = new CapsuleMesh { Radius = 0.32f, Height = 1.7f },
                MaterialOverride = material,
                Position = ToGodot(creature.Position) + Vector3.Up * 0.85f
            };
            AddChild(marker);
        }
    }

    private int LoadPlayerModel(PlayerRecord source, CameraStyleRecord cameraStyle,
        string manifestDirectory)
    {
        if (source.Schema != "nikami-aurora-kotor-player-v1" ||
            string.IsNullOrWhiteSpace(source.Glb))
            throw new InvalidDataException("Player manifest is missing or unsupported");
        ValidateBasePlayerPresentation(source);
        playerManifestDirectory = manifestDirectory;
        basePlayerRecord = source;
        playerTalkOffset = source.TalkOffset is { Count: >= 3 }
            ? ToGodot(source.TalkOffset)
            : null;
        playerEquipmentVariants = source.EquipmentVariants ?? [];
        openingEquipmentVariant = playerEquipmentVariants.SingleOrDefault(variant =>
            variant.Id.Equals("opening-clothing-short-sword", StringComparison.OrdinalIgnoreCase));
        if (openingEquipmentVariant is not null &&
            openingEquipmentVariant.Schema != "nikami-aurora-kotor-player-equipment-v1")
            throw new InvalidDataException(
                $"Unsupported player equipment variant: {openingEquipmentVariant.Schema}");
        var path = Path.GetFullPath(Path.Combine(manifestDirectory,
            source.Glb.Replace('/', Path.DirectorySeparatorChar)));
        var document = new GltfDocument();
        var state = new GltfState();
        if (document.AppendFromFile(path, state) != Error.Ok ||
            document.GenerateScene(state) is not Node3D model)
            throw new InvalidDataException($"Godot could not import player model: {path}");
        AccumulateDynamicMaterialReport(
            ConfigureDynamicObjectMaterials(model, enhancedPresentation));
        model.Name = "PlayerModel";
        playerBody.AddChild(model);
        playerModel = model;
        xrLocalPlayerHeadVisible = null;
        BindXrPlayerRig(model);
        UpdateXrLocalAvatarVisibility();
        playerAnimationPlayer = FindDescendant<AnimationPlayer>(model)
            ?? throw new InvalidDataException("Player model has no animation player");
        foreach (var animationName in playerAnimationPlayer.GetAnimationList())
        {
            var animation = playerAnimationPlayer.GetAnimation(animationName);
            if (animation is not null)
                animation.LoopMode = Animation.LoopModeEnum.None;
        }
        var walkName = FindAnimationName(playerAnimationPlayer, "walk");
        var runName = FindAnimationName(playerAnimationPlayer, "run");
        var walkAnimation = playerAnimationPlayer.GetAnimation(walkName);
        var runAnimation = playerAnimationPlayer.GetAnimation(runName);
        if (walkAnimation is null || runAnimation is null)
            throw new InvalidDataException("Player movement animations are missing");
        playerWalkSpeed = source.WalkDistance / (float)walkAnimation.GetLength();
        playerRunSpeed = source.RunDistance / (float)runAnimation.GetLength();
        var playerFaceRig = BuildLipRig(model, playerAnimationPlayer);
        playerFaceRig?.Modifier.SetNeutral();
        GD.Print($"NIKAMI_AURORA_PLAYER_FACE status=" +
                 $"{(playerFaceRig is null ? "unavailable" : "neutralized")}");
        cameraPivot.Position = source.CameraOffset is { Count: >= 3 }
            ? ToGodot(source.CameraOffset)
            : Vector3.Up * source.Height;
        xrGameplayOriginCalibrated = false;
        if (xrActive)
            RecenterXrGameplayBase();
        var cameraDistance = Math.Max(0.1f, cameraStyle.Distance);
        var cameraHeight = cameraStyle.Height;
        cameraArm.SpringLength = Mathf.Sqrt(
            cameraDistance * cameraDistance + cameraHeight * cameraHeight);
        pitch = -Mathf.Atan2(cameraHeight, cameraDistance);
        cameraPivot.Rotation = new Vector3(pitch, 0, 0);
        PlayPlayerAnimation("pause1", immediate: true);
        GD.Print($"NIKAMI_AURORA_PLAYER status=ready appearance={source.AppearanceId} " +
                 $"label={source.AppearanceLabel} body={source.BodyModel} head={source.HeadModel} " +
                 $"skins={source.Animation.SkinCount} animations={string.Join(',', source.Animation.Animations)} " +
                 $"walkSpeed={playerWalkSpeed:F3} runSpeed={playerRunSpeed:F3} " +
                 $"cameraDistance={cameraDistance:F3} cameraHeight={cameraHeight:F3} " +
                 $"sourcePitch={cameraStyle.PitchDegrees:F3}");
        return 1;
    }

    private static StringName FindAnimationName(AnimationPlayer player, string requested)
    {
        var match = player.GetAnimationList().FirstOrDefault(name =>
            name.ToString().Equals(requested, StringComparison.OrdinalIgnoreCase) ||
            name.ToString().EndsWith('/' + requested, StringComparison.OrdinalIgnoreCase));
        if (match == default)
            throw new InvalidDataException($"Animation is missing: {requested}");
        return match;
    }

    private void PlayPlayerAnimation(
        string requested,
        bool immediate = false,
        bool loop = true,
        bool restart = false)
    {
        if (playerAnimationPlayer is null ||
            !restart && currentPlayerAnimation.Equals(
                requested, StringComparison.OrdinalIgnoreCase))
            return;
        var match = FindAnimationName(playerAnimationPlayer, requested);
        var animation = playerAnimationPlayer.GetAnimation(match)
            ?? throw new InvalidDataException($"Animation is missing: {requested}");
        animation.LoopMode = loop
            ? Animation.LoopModeEnum.Linear
            : Animation.LoopModeEnum.None;
        playerAnimationPlayer.Play(match, customBlend: immediate ? 0.0 : 0.12);
        if (immediate)
            playerAnimationPlayer.Advance(0.0);
        currentPlayerAnimation = requested;
        GD.Print($"NIKAMI_AURORA_PLAYER_ANIMATION status=playing animation={match} " +
                 $"loop={(loop ? 1 : 0)} restart={(restart ? 1 : 0)}");
    }

    private int LoadActorModels(IEnumerable<CreatureRecord> creatures, string manifestDirectory)
    {
        actorModels.Clear();
        actorRecords.Clear();
        materializedCreatures.Clear();
        actorAnimations.Clear();
        actorEffectRigs.Clear();
        actorEffectTextures.Clear();
        materializedCreatureEmitters = 0;
        materializedCreatureLights = 0;
        materializedCreatureEffectAnimations = 0;
        actorTalkOffsets.Clear();
        actorLipRigs.Clear();
        var loaded = 0;
        foreach (var creature in creatures)
        {
            if (string.IsNullOrWhiteSpace(creature.Glb)) continue;
            var path = Path.GetFullPath(Path.Combine(manifestDirectory,
                creature.Glb.Replace('/', Path.DirectorySeparatorChar)));
            var document = new GltfDocument();
            var state = new GltfState();
            if (document.AppendFromFile(path, state) != Error.Ok ||
                document.GenerateScene(state) is not Node3D actor)
                throw new InvalidDataException($"Godot could not import actor {creature.Template}: {path}");
            AccumulateDynamicMaterialReport(
                ConfigureDynamicObjectMaterials(actor, enhancedPresentation));
            actor.Name = $"Actor_{creature.Template}";
            actor.Position = ToGodot(creature.Position);
            // PyKotor's bearing zero faces native +Y. KOTOR_TO_GODOT maps that
            // axis to Godot -Z, so the yaw sign is preserved.
            actor.Rotation = new Vector3(0, creature.Bearing, 0);
            AddChild(actor);
            var effectRig = LoadCreatureEffects(
                creature, actor, manifestDirectory, actorEffectTextures,
                enhancedPresentation);
            materializedCreatureEmitters += effectRig.Emitters.Count;
            materializedCreatureLights += effectRig.Lights.Count;
            materializedCreatureEffectAnimations += effectRig.Animations.Count;
            materializedCreatures.Add(new MaterializedCreature(creature, actor));
            var actorKeys = new[] { creature.Template, creature.Tag }
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var key in actorKeys)
            {
                actorModels[key!] = actor;
                actorRecords[key!] = creature;
                actorEffectRigs[key!] = effectRig;
            }
            if (creature.TalkOffset is { Count: >= 3 })
            {
                foreach (var key in actorKeys)
                    actorTalkOffsets[key!] = ToGodot(creature.TalkOffset);
            }
            var animationPlayer = FindDescendant<AnimationPlayer>(actor);
            if (effectRig.Animations.Count > 0 && animationPlayer is null)
                throw new InvalidDataException(
                    $"Creature effect animations have no runtime player: {creature.Template}");
            if (animationPlayer is not null)
            {
                var runtimeAnimationNames = animationPlayer.GetAnimationList()
                    .Select(name => name.ToString().Split('/')[^1])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (effectRig.Animations.Keys.Any(name =>
                        !runtimeAnimationNames.Contains(name)))
                    throw new InvalidDataException(
                        $"Creature effect animation is missing from glTF: {creature.Template}");
                foreach (var key in actorKeys)
                    actorAnimations[key!] = animationPlayer;
                foreach (var animationName in animationPlayer.GetAnimationList())
                {
                    var animation = animationPlayer.GetAnimation(animationName);
                    if (animation is not null)
                        animation.LoopMode = Animation.LoopModeEnum.Linear;
                }
                PlayActorAnimation(
                    creature.Template,
                    string.IsNullOrWhiteSpace(creature.IdleAnimation)
                        ? "pause1"
                        : creature.IdleAnimation);
                GD.Print($"NIKAMI_AURORA_ACTOR_ANIMATION status=ready actor={creature.Template} " +
                         $"tracks={string.Join(',', animationPlayer.GetAnimationList())}");
                var lipRig = BuildLipRig(actor, animationPlayer);
                if (lipRig is not null)
                {
                    foreach (var key in actorKeys)
                        actorLipRigs[key!] = lipRig;
                    GD.Print($"NIKAMI_AURORA_LIP_RIG status=ready actor={creature.Template} " +
                             $"bones={lipRig.Tracks.Count} tracks={lipRig.TrackCount} " +
                             $"shapes=16 duration={lipRig.Animation.GetLength():F3}");
                }
            }
            loaded++;
        }
        return loaded;
    }

    private int LoadDoorModels(IReadOnlyList<DoorRecord> doors, string manifestDirectory)
    {
        interactiveDoors.Clear();
        for (var index = 0; index < doors.Count; index++)
        {
            var door = doors[index];
            if (string.IsNullOrWhiteSpace(door.Glb)) continue;
            var path = Path.GetFullPath(Path.Combine(manifestDirectory,
                door.Glb.Replace('/', Path.DirectorySeparatorChar)));
            var document = new GltfDocument();
            var state = new GltfState();
            if (document.AppendFromFile(path, state) != Error.Ok ||
                document.GenerateScene(state) is not Node3D model)
                throw new InvalidDataException($"Godot could not import door {door.Tag}: {path}");
            AccumulateDynamicMaterialReport(
                ConfigureDynamicObjectMaterials(model, enhancedPresentation));
            model.Name = $"Door_{door.Tag}";
            model.Position = ToGodot(door.Position);
            model.Rotation = new Vector3(0, door.Bearing, 0);
            AddChild(model);
            var instanceId = DoorInstanceId(index);
            interactiveDoors.Add(new InteractiveDoor(instanceId, door, model, model.Position));
            GD.Print($"NIKAMI_AURORA_DOOR status=ready id={instanceId} " +
                     $"tag={door.Tag} model={door.Model} " +
                     $"conversation={door.Conversation} nativeOnOpen={door.OnOpen}");
        }
        return interactiveDoors.Count;
    }

    private int LoadPlaceableModels(
        IReadOnlyList<PlaceableRecord> placeables,
        string manifestDirectory)
    {
        materializedPlaceables.Clear();
        var loaded = 0;
        for (var index = 0; index < placeables.Count; index++)
        {
            var placeable = placeables[index];
            if (string.IsNullOrWhiteSpace(placeable.Glb)) continue;
            var path = Path.GetFullPath(Path.Combine(manifestDirectory,
                placeable.Glb.Replace('/', Path.DirectorySeparatorChar)));
            var document = new GltfDocument();
            var state = new GltfState();
            if (document.AppendFromFile(path, state) != Error.Ok ||
                document.GenerateScene(state) is not Node3D model)
                throw new InvalidDataException(
                    $"Godot could not import placeable {placeable.Tag}: {path}");
            AccumulateDynamicMaterialReport(
                ConfigureDynamicObjectMaterials(model, enhancedPresentation));
            var instanceId = PlaceableInstanceId(index);
            model.Name = $"Placeable_{instanceId}_{placeable.Template}";
            model.Position = ToGodot(placeable.Position);
            model.Rotation = new Vector3(0, placeable.Bearing, 0);
            AddChild(model);
            materializedPlaceables.Add(new MaterializedPlaceable(instanceId, placeable, model));
            loaded++;
            GD.Print($"NIKAMI_AURORA_PLACEABLE status=ready id={instanceId} tag={placeable.Tag} " +
                     $"template={placeable.Template} model={placeable.Model} " +
                     $"static={placeable.Static} useable={placeable.Useable} " +
                     $"nativeOnInventory={placeable.OnInventory}");
        }
        return loaded;
    }

    private bool IsFirstEncounterEnvironmentReady(FirstEncounterRecord encounter) =>
        encounter.EnvironmentPlaceables.All(expected =>
            materializedPlaceables.Any(actual =>
                actual.Source.Template.Equals(
                    expected.Template, StringComparison.OrdinalIgnoreCase) &&
                (ToGodot(actual.Source.Position) - ToGodot(expected.Position))
                .LengthSquared() < 0.0001f));

    private void UpdateInteractionPrompt()
    {
        if (dialoguePanel.Visible)
        {
            interactionPrompt.Visible = false;
            return;
        }
        var placeable = NearestPlaceable(2.6f);
        if (placeable is not null)
        {
            interactionPrompt.Visible = true;
            interactionPrompt.Text = IsPlaceableOpened(placeable)
                ? "LOCKER OPENED"
                : "E  OPEN FOOTLOCKER";
            return;
        }
        var door = NearestDoor(2.6f);
        interactionPrompt.Visible = door is not null;
        if (door is not null)
            interactionPrompt.Text = IsDoorOpen(door)
                ? "E  CLOSE LOCKDOWN DOOR"
                : "E  OPEN LOCKDOWN DOOR";
    }

    private MaterializedPlaceable? NearestPlaceable(float maximumDistance)
    {
        MaterializedPlaceable? nearest = null;
        var bestSquared = maximumDistance * maximumDistance;
        foreach (var placeable in materializedPlaceables)
        {
            if (!placeable.Source.Useable) continue;
            var delta = placeable.Model.Position - playerBody.GlobalPosition;
            delta.Y = 0;
            var distanceSquared = delta.LengthSquared();
            if (distanceSquared >= bestSquared) continue;
            bestSquared = distanceSquared;
            nearest = placeable;
        }
        return nearest;
    }

    private void UsePlaceable(MaterializedPlaceable placeable)
    {
        ApplyGameplayTransition(
            RequireGameplaySimulation().UsePlaceable(placeable.InstanceId));
    }

    private KotorGameplaySimulation RequireGameplaySimulation() =>
        gameplaySimulation ?? throw new InvalidOperationException("KOTOR gameplay state is not initialized");

    private bool IsDoorOpen(InteractiveDoor door) =>
        gameplaySimulation?.IsDoorOpen(door.InstanceId) ?? false;

    private bool IsPlaceableOpened(MaterializedPlaceable placeable) =>
        gameplaySimulation?.IsPlaceableOpened(placeable.InstanceId) ?? false;

    private InteractiveDoor? NearestDoor(float maximumDistance)
    {
        InteractiveDoor? nearest = null;
        var bestSquared = maximumDistance * maximumDistance;
        foreach (var door in interactiveDoors)
        {
            var delta = door.ClosedPosition - playerBody.GlobalPosition;
            delta.Y = 0;
            var distanceSquared = delta.LengthSquared();
            if (distanceSquared >= bestSquared) continue;
            bestSquared = distanceSquared;
            nearest = door;
        }
        return nearest;
    }

    private void ToggleDoor(InteractiveDoor door)
    {
        ApplyGameplayTransition(
            RequireGameplaySimulation().ToggleDoor(door.InstanceId));
    }

    private InteractiveDoor RequireInteractiveDoor(string tag) =>
        interactiveDoors.FirstOrDefault(candidate =>
            candidate.Source.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"Interactive door was not materialized: {tag}");

    private void ExecuteScript(string? resref)
    {
        ApplyGameplayTransition(
            RequireGameplaySimulation().ExecuteScript(resref));
    }

    private void ExecuteScript(string? resref, DialogueScriptParameters? parameters)
    {
        ApplyGameplayTransition(RequireGameplaySimulation().ExecuteScript(
            resref,
            parameters is null
                ? null
                : new KotorScriptInvocation(
                    parameters.Int1,
                    parameters.Int2,
                    parameters.Int3,
                    parameters.Int4,
                    parameters.Int5,
                    parameters.String6)));
    }

    private void ApplyGameplayTransition(KotorGameplayTransition transition)
    {
        var equipmentChanged = false;
        foreach (var gameplayEvent in transition.Events)
        {
            switch (gameplayEvent)
            {
                case KotorDoorStateChanged doorState:
                    PresentDoorState(doorState);
                    break;
                case KotorPlaceableOpened placeableOpened:
                    PresentPlaceableOpened(placeableOpened.Placeable);
                    break;
                case KotorPlaceableAlreadyOpened alreadyOpened:
                    GD.Print($"NIKAMI_AURORA_PLACEABLE status=already-open " +
                             $"id={alreadyOpened.Placeable.InstanceId} " +
                             $"tag={alreadyOpened.Placeable.Tag}");
                    break;
                case KotorItemsTransferred transferred:
                    PresentItemsTransferred(transferred);
                    break;
                case KotorEquipmentChanged equipped:
                    equipmentChanged = true;
                    GD.Print($"NIKAMI_AURORA_EQUIPMENT status=equipped " +
                             $"slot={equipped.Slot} item={equipped.Item.Resref} " +
                             $"previous={equipped.PreviousResref}");
                    break;
                case KotorEquipmentRemoved removed:
                    equipmentChanged = true;
                    GD.Print($"NIKAMI_AURORA_EQUIPMENT status=removed " +
                             $"slot={removed.Slot} item={removed.Item.Resref}");
                    break;
                case KotorItemUsed used:
                    GD.Print($"NIKAMI_AURORA_ITEM status=used item={used.Item.Resref} " +
                             $"quantity={used.QuantityBefore}->{used.QuantityAfter} " +
                             $"target={used.PartyMemberId} " +
                             $"vitality={used.VitalityBefore}->{used.VitalityAfter}");
                    break;
                case KotorPartyMemberSelected selected:
                    GD.Print($"NIKAMI_AURORA_PARTY status=selected " +
                             $"member={selected.BeforeId}->{selected.AfterId}");
                    break;
                case KotorTriggerEntered entered:
                    GD.Print($"NIKAMI_AURORA_TRIGGER status=entered " +
                             $"id={entered.Trigger.InstanceId} " +
                             $"template={entered.Trigger.Template} " +
                             $"onEnter={entered.Trigger.OnEnterScript}");
                    break;
                case KotorGlobalNumberChanged global:
                    GD.Print($"NIKAMI_AURORA_GLOBAL status=changed name={global.Name} " +
                             $"value={global.Before}->{global.After}");
                    break;
                case KotorMapRevealed map:
                    GD.Print($"NIKAMI_AURORA_MAP status=revealed value={map.Before}->{map.After}");
                    break;
                case KotorDialogueRequested requested:
                    PresentRequestedDialogue(requested);
                    break;
                case KotorPlayerMoveRequested requested:
                    PresentPlayerMove(requested);
                    break;
                case KotorGlobalFadeRequested requested:
                    PresentGlobalFade(requested);
                    break;
                case KotorBackgroundMusicRequested requested:
                    PresentBackgroundMusic(requested);
                    break;
                case KotorSoundObjectPlayRequested requested:
                    PresentSoundObjectPlay(requested);
                    break;
                case KotorSoundObjectStopRequested requested:
                    PresentSoundObjectStop(requested);
                    break;
                case KotorVideoEffectRequested requested:
                    PresentVideoEffect(requested);
                    break;
                case KotorLocalBooleanChanged local:
                    GD.Print($"NIKAMI_AURORA_LOCAL_BOOLEAN status=changed " +
                             $"object={local.ObjectTag} index={local.Index} " +
                             $"value={local.Before}->{local.After}");
                    break;
                case KotorRoomAnimationRequested requested:
                    PresentRoomAnimation(requested);
                    break;
                case KotorExperienceAwarded experience:
                    PresentExperienceAward(experience);
                    break;
                case KotorCombatExperienceAwarded experience:
                    GD.Print("NIKAMI_AURORA_COMBAT_XP status=awarded " +
                             $"source={experience.SourceId} " +
                             $"xp={experience.Before}+{experience.Awarded}->{experience.After}");
                    break;
                case KotorCombatDamageApplied damage:
                    GD.Print("NIKAMI_AURORA_COMBAT_DAMAGE status=applied " +
                             $"source={damage.SourceId} target={damage.TargetId} " +
                             $"damage={damage.Damage} " +
                             $"vitality={damage.VitalityBefore}->{damage.VitalityAfter}");
                    break;
                case KotorLevelChanged level:
                    GD.Print($"NIKAMI_AURORA_LEVEL status=changed " +
                             $"level={level.Before}->{level.After} xp={level.Experience}");
                    break;
                case KotorScriptExecuted executed:
                    PresentScriptExecution(executed.Contract);
                    break;
                case KotorScriptSkipped skipped:
                    GD.Print($"NIKAMI_AURORA_NCS status=skipped script={skipped.Contract.Resref} " +
                             $"kind={skipped.Contract.KindName} " +
                             $"requiredXp={skipped.Contract.RequiredPlayerExperience} " +
                             $"actualXp={skipped.ActualPlayerExperience}");
                    break;
                case KotorScriptUnsupported unsupported:
                    if (reportedUnsupportedScripts.Add(unsupported.Resref))
                        GD.Print($"NIKAMI_AURORA_NCS status=unsupported script={unsupported.Resref}");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported KOTOR gameplay event: {gameplayEvent.GetType().Name}");
            }
        }
        if (equipmentChanged)
            PresentEquipment(transition.After);
    }

    private void PresentRoomAnimation(KotorRoomAnimationRequested request)
    {
        if (!roomModels.TryGetValue(request.RoomModel, out var roomRoot) ||
            !roomRecords.TryGetValue(request.RoomModel, out var room))
            throw new InvalidDataException(
                $"Room animation could not resolve model {request.RoomModel}");
        var animationName = $"scriptloop{request.AnimationIndex:00}";
        var animation = room.AlphaAnimations?.SingleOrDefault(candidate =>
            candidate.Name.Equals(animationName, StringComparison.OrdinalIgnoreCase));
        var emitterAnimation = room.EmitterAnimations?.SingleOrDefault(candidate =>
            candidate.Name.Equals(animationName, StringComparison.OrdinalIgnoreCase));
        if (animation is null && emitterAnimation is null)
            throw new InvalidDataException(
                $"Room {request.RoomModel} has no animation {animationName}");

        foreach (var alphaNode in room.AlphaNodes ?? [])
        {
            var tweenKey = $"{request.RoomModel}/{alphaNode.NodeName}";
            if (roomAlphaTweens.Remove(tweenKey, out var existing) && existing.IsValid())
                existing.Kill();
            SetRoomNodeAlpha(
                RequireRoomAnimationNode(roomRoot, request.RoomModel, alphaNode.NodeName),
                alphaNode.BaseAlpha);
        }

        foreach (var track in animation?.Tracks ?? [])
        {
            var mesh = RequireRoomAnimationNode(roomRoot, request.RoomModel, track.NodeName);
            if (track.Keys.Count == 0) continue;
            var tweenKey = $"{request.RoomModel}/{track.NodeName}";
            var tween = CreateTween();
            var previousTime = 0.0f;
            var previousValue = room.AlphaNodes?.Single(node =>
                node.NodeName.Equals(track.NodeName, StringComparison.OrdinalIgnoreCase)).BaseAlpha
                ?? 1.0f;
            foreach (var key in track.Keys)
            {
                if (!float.IsFinite(key.Time) || key.Time < previousTime ||
                    !float.IsFinite(key.Value) || key.Value < 0 || key.Value > 1)
                    throw new InvalidDataException(
                        $"Invalid room alpha key {request.RoomModel}/{animationName}/" +
                        $"{track.NodeName}");
                var duration = key.Time - previousTime;
                if (duration > 0)
                {
                    var start = previousValue;
                    var end = key.Value;
                    tween.TweenMethod(
                        Callable.From<float>(value => SetRoomNodeAlpha(mesh, value)),
                        start,
                        end,
                        duration);
                }
                else
                {
                    var value = key.Value;
                    tween.TweenCallback(
                        Callable.From(() => SetRoomNodeAlpha(mesh, value)));
                }
                previousTime = key.Time;
                previousValue = key.Value;
            }
            roomAlphaTweens[tweenKey] = tween;
        }
        if (emitterAnimation is not null)
            StartRoomEmitterAnimation(room, roomRoot, emitterAnimation);
        GD.Print($"NIKAMI_AURORA_ROOM_ANIMATION status=playing " +
                 $"room={request.RoomModel} animation={animationName} " +
                 $"alpha_tracks={animation?.Tracks.Count ?? 0} " +
                 $"emitter_tracks={emitterAnimation?.Tracks.Count ?? 0}");
    }

    private static MeshInstance3D RequireRoomAnimationNode(
        Node roomRoot,
        string roomModel,
        string nodeName) =>
        roomRoot.FindChild(nodeName, recursive: true, owned: false) as MeshInstance3D
        ?? throw new InvalidDataException(
            $"Room alpha animation node was not materialized: {roomModel}/{nodeName}");

    private static void SetRoomNodeAlpha(MeshInstance3D mesh, float alpha)
    {
        if (!float.IsFinite(alpha) || alpha < 0 || alpha > 1)
            throw new ArgumentOutOfRangeException(nameof(alpha));
        for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
        {
            switch (mesh.GetActiveMaterial(surface))
            {
                case BaseMaterial3D material:
                    var color = material.AlbedoColor;
                    color.A = alpha;
                    material.AlbedoColor = color;
                    break;
                case ShaderMaterial material:
                    var tint = material.GetShaderParameter("albedo_tint").AsColor();
                    tint.A = alpha;
                    material.SetShaderParameter("albedo_tint", tint);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Room alpha node has an unsupported material: {mesh.Name}");
            }
        }
    }

    private void PresentDoorState(KotorDoorStateChanged state)
    {
        var door = interactiveDoors.FirstOrDefault(candidate =>
            candidate.InstanceId.Equals(state.Door.InstanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Gameplay state could not resolve door instance {state.Door.InstanceId}");
        var destination = state.Open
            ? door.ClosedPosition + Vector3.Up * 2.8f
            : door.ClosedPosition;
        CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut)
            .TweenProperty(door.Model, "position", destination, 0.7);
        GD.Print($"NIKAMI_AURORA_DOOR status={(state.Open ? "opened" : "closed")} " +
                 $"id={door.InstanceId} tag={door.Source.Tag} model={door.Source.Model} " +
                 $"conversation={door.Source.Conversation} nativeOnOpen={door.Source.OnOpen}");
    }

    private void PresentPlaceableOpened(KotorPlaceableDefinition state)
    {
        var placeable = materializedPlaceables.FirstOrDefault(candidate =>
            candidate.InstanceId.Equals(state.InstanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Gameplay state could not resolve placeable instance {state.InstanceId}");
        GD.Print($"NIKAMI_AURORA_PLACEABLE status=opened id={placeable.InstanceId} " +
                 $"tag={placeable.Source.Tag} " +
                 $"model={placeable.Source.Model} " +
                 $"nativeOnInventory={placeable.Source.OnInventory}");
    }

    private void PresentItemsTransferred(KotorItemsTransferred transferred)
    {
        var placeable = materializedPlaceables.FirstOrDefault(candidate =>
            candidate.InstanceId.Equals(
                transferred.Placeable.InstanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Gameplay state could not resolve loot source {transferred.Placeable.InstanceId}");
        var summary = string.Join(", ", transferred.Items.Select(stack =>
            $"{stack.Quantity}x {stack.Item.DisplayName}"));
        GD.Print($"NIKAMI_AURORA_INVENTORY status=transferred " +
                 $"source={placeable.InstanceId} items={summary}");

        ShowWorldNotice("LOOT ACQUIRED", transferred.Items.Select(stack =>
            $"{stack.Quantity}x  {stack.Item.DisplayName}").Concat(
            ["Q / B-Y  EQUIP GEAR"]));

        if (xrActive)
            (activeInteractionController ?? xrRightHand)
                .TriggerHapticPulse("haptic", 0.0, 0.35, 0.08, 0.0);
    }

    private void ShowWorldNotice(string title, IEnumerable<string> lines)
    {
        if (worldNotice is not null && GodotObject.IsInstanceValid(worldNotice))
            worldNotice.QueueFree();
        var label = new Label3D
        {
            Name = "WorldNotice",
            Text = title + '\n' + string.Join("\n", lines),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            DoubleSided = true,
            NoDepthTest = true,
            FixedSize = false,
            PixelSize = 0.002f,
            FontSize = 32,
            OutlineSize = 6,
            Modulate = new Color(0.45f, 0.88f, 1.0f),
            OutlineModulate = new Color(0.01f, 0.02f, 0.04f, 0.95f)
        };
        worldNotice = label;
        AddChild(label);
        var activeView = xrActive ? (Node3D)xrCamera : camera;
        var viewForward = -activeView.GlobalTransform.Basis.Z.Normalized();
        var viewRight = activeView.GlobalTransform.Basis.X.Normalized();
        label.GlobalPosition = activeView.GlobalPosition + viewForward * 1.8f +
                               viewRight * 0.48f + Vector3.Up * 0.1f;
        var tween = CreateTween();
        tween.TweenInterval(3.0);
        tween.TweenProperty(label, "modulate:a", 0.0f, 0.8);
        tween.TweenCallback(Callable.From(() =>
        {
            if (worldNotice == label)
                worldNotice = null;
            if (GodotObject.IsInstanceValid(label))
                label.QueueFree();
        }));
    }

    private void PresentEquipment(KotorGameplaySnapshot snapshot)
    {
        var unsupportedSlots = snapshot.Equipment.Keys.Where(slot =>
            slot is not KotorEquipmentSlot.Armor and
            not KotorEquipmentSlot.LeftHand and
            not KotorEquipmentSlot.RightHand).ToArray();
        if (unsupportedSlots.Length > 0)
            throw new InvalidDataException(
                $"Player equipment has no visual variant coverage: " +
                string.Join(',', unsupportedSlots));
        snapshot.Equipment.TryGetValue(KotorEquipmentSlot.Armor, out var armor);
        snapshot.Equipment.TryGetValue(KotorEquipmentSlot.LeftHand, out var leftHand);
        snapshot.Equipment.TryGetValue(KotorEquipmentSlot.RightHand, out var rightHand);
        var basePlayer = basePlayerRecord
            ?? throw new InvalidDataException("Base player model is unavailable");
        var isBaseAppearance = armor is null && leftHand is null && rightHand is null;
        var variant = isBaseAppearance
            ? null
            : playerEquipmentVariants.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.ArmorResref, armor, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    candidate.LeftHandResref, leftHand, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    candidate.RightHandResref, rightHand, StringComparison.OrdinalIgnoreCase));
        if (!isBaseAppearance && variant is null)
            throw new InvalidDataException(
                $"No player model matches Armor={armor ?? "none"}, " +
                $"LeftHand={leftHand ?? "none"}, " +
                $"RightHand={rightHand ?? "none"}");

        var glb = variant?.Glb ?? basePlayer.Glb;
        var animationContract = variant?.Animation ?? basePlayer.Animation;
        var cameraOffset = variant?.CameraOffset ?? basePlayer.CameraOffset;
        var talkOffset = variant?.TalkOffset ?? basePlayer.TalkOffset;
        var variantId = variant?.Id ?? "opening-base";
        var path = Path.GetFullPath(Path.Combine(playerManifestDirectory,
            glb.Replace('/', Path.DirectorySeparatorChar)));
        var document = new GltfDocument();
        var state = new GltfState();
        if (document.AppendFromFile(path, state) != Error.Ok ||
            document.GenerateScene(state) is not Node3D model)
            throw new InvalidDataException($"Godot could not import player equipment model: {path}");
        var variantMaterialReport = ConfigureDynamicObjectMaterials(
            model, enhancedPresentation);
        GD.Print($"NIKAMI_AURORA_DYNAMIC_PBR status=variant variant={variantId} " +
                 $"tier={(enhancedPresentation ? "enhanced" : "source")} " +
                 $"surfaces={variantMaterialReport.Surfaces} " +
                 $"pbr_surfaces={variantMaterialReport.EnhancedPbr} " +
                 $"normal_mapped_surfaces={variantMaterialReport.NormalMapped}");
        model.Name = $"PlayerModel_{variantId}";
        var variantEnvironmentBindings = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        ConfigureSourceEnvironmentMaterials(
            model, environmentMapTextures, environmentReflectionStrength,
            environmentMaximumReflectionWeight,
            variantEnvironmentBindings, enhancedPresentation);
        if (variantEnvironmentBindings.Count > 0)
            GD.Print($"NIKAMI_AURORA_PLAYER_ENVIRONMENT_MAP status=ready " +
                     $"variant={variantId} bindings=" +
                     string.Join(',', variantEnvironmentBindings.OrderBy(pair => pair.Key)
                         .Select(pair => $"{pair.Key}:{pair.Value}")));
        var weaponNodes = FindDescendants<Node3D>(model).Where(node =>
            node.Name.ToString().StartsWith(
                "weapon__", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (variant?.WeaponHook is { Length: > 0 } expectedWeaponHook)
        {
            var weaponRoots = weaponNodes.Where(node =>
                node.GetParent() is not Node3D parent ||
                !parent.Name.ToString().StartsWith(
                    "weapon__", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (weaponRoots.Length != 1 ||
                !weaponRoots[0].GetParent().Name.ToString().Equals(
                    expectedWeaponHook, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Player weapon hierarchy does not attach to {expectedWeaponHook}");
            GD.Print($"NIKAMI_AURORA_PLAYER_WEAPON status=attached " +
                     $"variant={variantId} hook={expectedWeaponHook} " +
                     $"nodes={weaponNodes.Length}");
        }
        else if (weaponNodes.Length != 0)
        {
            throw new InvalidDataException(
                $"Unarmed player variant contains weapon nodes: {variantId}");
        }
        var animationPlayer = FindDescendant<AnimationPlayer>(model)
            ?? throw new InvalidDataException("Player equipment model has no animation player");
        foreach (var animationName in animationPlayer.GetAnimationList())
        {
            var clip = animationPlayer.GetAnimation(animationName);
            if (clip is not null)
                clip.LoopMode = Animation.LoopModeEnum.None;
        }
        foreach (var expected in animationContract.Animations)
            _ = FindAnimationName(animationPlayer, expected);
        var playerFaceRig = BuildLipRig(model, animationPlayer);
        playerFaceRig?.Modifier.SetNeutral();
        GD.Print($"NIKAMI_AURORA_PLAYER_FACE status=" +
                 $"{(playerFaceRig is null ? "unavailable" : "neutralized")} " +
                 $"variant={variantId}");

        var requestedAnimation = string.IsNullOrWhiteSpace(currentPlayerAnimation)
            ? "pause1"
            : currentPlayerAnimation;
        if (playerModel is not null)
        {
            playerModel.Visible = false;
            playerModel.QueueFree();
        }
        playerBody.AddChild(model);
        playerModel = model;
        playerTalkOffset = talkOffset is { Count: >= 3 }
            ? ToGodot(talkOffset)
            : null;
        xrLocalPlayerHeadVisible = null;
        BindXrPlayerRig(model);
        UpdateXrLocalAvatarVisibility();
        playerAnimationPlayer = animationPlayer;
        currentPlayerAnimation = "";
        var walkAnimation = animationPlayer.GetAnimation(
            FindAnimationName(animationPlayer, "walk"));
        var runAnimation = animationPlayer.GetAnimation(
            FindAnimationName(animationPlayer, "run"));
        if (walkAnimation is null || runAnimation is null)
            throw new InvalidDataException("Player equipment movement animations are missing");
        playerWalkSpeed = basePlayer.WalkDistance / (float)walkAnimation.GetLength();
        playerRunSpeed = basePlayer.RunDistance / (float)runAnimation.GetLength();
        if (cameraOffset is { Count: >= 3 })
        {
            cameraPivot.Position = ToGodot(cameraOffset);
            xrGameplayOriginCalibrated = false;
            if (xrActive && !dialogueCameraActive)
                RecenterXrGameplayBase();
        }
        PlayPlayerAnimation(requestedAnimation, immediate: true);
        if (equipmentScreen?.Visible != true)
            ShowWorldNotice(
                snapshot.Equipment.Count == 0 ? "UNEQUIPPED" : "EQUIPPED",
                snapshot.Equipment.Values.ToArray());
        if (xrActive)
            (activeInteractionController ?? xrRightHand)
                .TriggerHapticPulse("haptic", 0.0, 0.5, 0.12, 0.0);
        GD.Print($"NIKAMI_AURORA_PLAYER_EQUIPMENT status=ready variant={variantId} " +
                 $"armor={armor ?? "none"} leftHand={leftHand ?? "none"} " +
                 $"rightHand={rightHand ?? "none"} " +
                 $"body={variant?.BodyModel ?? basePlayer.BodyModel} " +
                 $"texture={variant?.BodyTexture ?? basePlayer.BodyTexture} " +
                 $"head={variant?.HeadModel ?? basePlayer.HeadModel} " +
                 $"weapon={variant?.WeaponModel ?? "none"} " +
                 $"skins={animationContract.SkinCount} " +
                 $"headSkins={animationContract.HeadSkinCount} " +
                 $"animations={string.Join(',', animationContract.Animations)}");
    }

    private void PresentPlayerMove(KotorPlayerMoveRequested request)
    {
        if (!moduleWaypoints.TryGetValue(request.WaypointTag, out var waypoint))
            throw new InvalidDataException(
                $"Player-move waypoint was not found: {request.WaypointTag}");
        var position = ToGodot(waypoint.Position);
        if (TryProjectToWalkmesh(position, out var floor)) position.Y = floor;
        playerBody.GlobalPosition = position;
        // Movement, turning, and the minimap all consume the authoritative yaw.
        // Keep that state joined to scripted Odyssey teleports so subsequent
        // input continues from the waypoint's authored facing.
        yaw = waypoint.Bearing;
        playerBody.Rotation = new Vector3(0, yaw, 0);
        cameraPivot.Rotation = new Vector3(pitch, 0, 0);
        simulationPlayerPosition = ToNumerics(waypoint.Position);
        simulationPlayerPosition.Z = position.Y;
        GD.Print($"NIKAMI_AURORA_PLAYER_MOVE status=ready " +
                 $"waypoint={request.WaypointTag} position={position} " +
                 $"bearing={waypoint.Bearing:F3}");
    }

    private void ConfigureVideoEffects(VideoEffectTableRecord source)
    {
        if (source.Schema != "nikami-aurora-kotor-video-effects-v1" ||
            source.SourceSha256.Length != 64 || !source.SourceSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Odyssey video-effect table is invalid");
        videoEffects.Clear();
        foreach (var effect in source.Effects)
        {
            if (effect.Id < 0 || string.IsNullOrWhiteSpace(effect.Label) ||
                effect.Modulation.Count < 3 ||
                !effect.Modulation.Take(3).All(value => float.IsFinite(value) && value >= 0) ||
                !float.IsFinite(effect.Saturation) || effect.Saturation < 0 ||
                effect.Saturation > 1 || !videoEffects.TryAdd(effect.Id, effect))
                throw new InvalidDataException(
                    $"Odyssey video-effect row is invalid: {effect.Id}");
        }
    }

    private void PresentVideoEffect(KotorVideoEffectRequested request)
    {
        if (!request.Enabled)
        {
            videoEffectOverlay.Visible = false;
            GD.Print("NIKAMI_AURORA_VIDEO_EFFECT status=disabled");
            return;
        }
        if (!videoEffects.TryGetValue(request.EffectId, out var source) ||
            videoEffectOverlay.Material is not ShaderMaterial material)
            throw new InvalidDataException(
                $"Odyssey video effect was not resolved: {request.EffectId}");
        material.SetShaderParameter("source_modulation", new Vector3(
            source.Modulation[0], source.Modulation[1], source.Modulation[2]));
        material.SetShaderParameter(
            "source_saturation", source.EnableSaturation ? source.Saturation : 1.0f);
        material.SetShaderParameter("source_scan_noise", source.EnableScanNoise);
        videoEffectOverlay.Visible = true;
        GD.Print($"NIKAMI_AURORA_VIDEO_EFFECT status=enabled id={source.Id} " +
                 $"label={source.Label} saturation={source.Saturation:F3} " +
                 $"modulation={source.Modulation[0]:F3}," +
                 $"{source.Modulation[1]:F3},{source.Modulation[2]:F3} " +
                 $"scan_noise={source.EnableScanNoise}");
    }

    private void PresentGlobalFade(KotorGlobalFadeRequested request)
    {
        dialogueFadeTween?.Kill();
        cinematicFade.Color = new Color(0, 0, 0, request.FadeIn ? 1 : 0);
        cinematicFade.Visible = true;
        var targetAlpha = request.FadeIn ? 0.0f : 1.0f;
        if (request.DelaySeconds == 0 && request.LengthSeconds == 0)
        {
            cinematicFade.Color = new Color(0, 0, 0, targetAlpha);
            if (request.FadeIn)
                cinematicFade.Visible = false;
        }
        else
        {
            var tween = cinematicFade.CreateTween();
            dialogueFadeTween = tween;
            if (request.DelaySeconds > 0)
                tween.TweenInterval(request.DelaySeconds);
            tween.TweenProperty(
                cinematicFade, "color:a", targetAlpha, request.LengthSeconds);
            if (request.FadeIn)
                tween.TweenCallback(Callable.From(() => cinematicFade.Visible = false));
        }
        GD.Print($"NIKAMI_AURORA_GLOBAL_FADE status=active " +
                 $"direction={(request.FadeIn ? "in" : "out")} " +
                 $"delay={request.DelaySeconds:F3} length={request.LengthSeconds:F3}");
    }

    private async void PresentBackgroundMusic(KotorBackgroundMusicRequested request)
    {
        var generation = ++areaMusicRequestGeneration;
        if (request.DelaySeconds > 0)
        {
            var timer = GetTree().CreateTimer(request.DelaySeconds);
            await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
            if (generation != areaMusicRequestGeneration)
                return;
        }
        if (request.Playing)
        {
            if (areaMusic.Stream is null)
                throw new InvalidOperationException(
                    "Authored background-music request has no loaded area stream");
            areaMusic.Play();
        }
        else
        {
            areaMusic.Stop();
        }
        GD.Print($"NIKAMI_AURORA_AREA_MUSIC " +
                 $"status={(request.Playing ? "playing" : "stopped")} " +
                 $"resref={currentMusicResref} delay={request.DelaySeconds:F3}");
    }

    private async void PresentSoundObjectPlay(KotorSoundObjectPlayRequested request)
    {
        if (!moduleSoundObjects.TryGetValue(request.Tag, out var sound))
            throw new InvalidDataException(
                $"Authored sound object was not materialized: {request.Tag}");
        if (request.DelaySeconds > 0)
        {
            var timer = GetTree().CreateTimer(request.DelaySeconds);
            await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        }
        PlayMaterializedSoundObject(sound);
        var audioIdentity = sound.Source.AudioSources?.FirstOrDefault()?.Resref ??
                            sound.Source.Audio?.Resref ?? "unknown";
        GD.Print($"NIKAMI_AURORA_SOUND_OBJECT status=playing " +
                 $"tag={request.Tag} template={sound.Source.Template} " +
                 $"audio={audioIdentity} delay={request.DelaySeconds:F3} " +
                 $"looping={sound.Source.Looping} positional={sound.Source.Positional}");
    }

    private async void PresentSoundObjectStop(KotorSoundObjectStopRequested request)
    {
        if (!moduleSoundObjects.TryGetValue(request.Tag, out var sound))
            throw new InvalidDataException(
                $"Authored sound object was not materialized: {request.Tag}");
        if (request.DelaySeconds > 0)
        {
            var timer = GetTree().CreateTimer(request.DelaySeconds);
            await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        }
        if (request.FadeSeconds > 0)
        {
            var tween = CreateTween();
            tween.TweenProperty(
                sound.Player, "volume_db", -80.0f, request.FadeSeconds);
            await ToSignal(tween, Tween.SignalName.Finished);
        }
        sound.Generation++;
        sound.Player.Call("stop");
        sound.Player.Set("volume_db", sound.SourceVolumeDb);
        GD.Print($"NIKAMI_AURORA_SOUND_OBJECT status=stopped " +
                 $"tag={request.Tag} delay={request.DelaySeconds:F3} " +
                 $"fade={request.FadeSeconds:F3}");
    }

    private static void PresentExperienceAward(KotorExperienceAwarded experience)
    {
        var contract = experience.Contract;
        GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                 $"kind={contract.KindName} plot={contract.PlotLabel} " +
                 $"percentage={contract.PlotPercentage} base={contract.PlotBaseExperience} " +
                 $"awarded={experience.Awarded} xp={experience.Before}->{experience.After}");
    }

    private static void PresentScriptExecution(KotorScriptContract contract)
    {
        if (contract.Kind == KotorScriptContractKind.TriggerDialogue &&
            contract.TriggerDialogue is { } trigger)
        {
            GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                     $"kind={contract.KindName} trigger={trigger.TriggerTemplate} " +
                     $"global={trigger.GlobalName ?? "none"}:" +
                     $"{trigger.GlobalValue?.ToString() ?? "none"} " +
                     $"actor={trigger.ActorTag} event={trigger.UserEvent} " +
                     $"conversation={trigger.Conversation} starter={trigger.DialogueStarter}");
            return;
        }
        if (contract.Kind is KotorScriptContractKind.GlobalNumberAdd or
            KotorScriptContractKind.GlobalNumberSet)
        {
            GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                     $"kind={contract.KindName} global={contract.GlobalName} " +
                     $"value={contract.GlobalValue}");
            return;
        }
        if (contract.Kind == KotorScriptContractKind.RevealMap)
        {
            GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                     $"kind={contract.KindName}");
            return;
        }
        GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                 $"kind={contract.KindName} door={contract.DoorTag} " +
                 $"pause={contract.PauseConversation} moveTarget={contract.MoveTargetTag} " +
                 $"run={contract.MoveRun} range={contract.MoveRange:F3} " +
                 $"resume={contract.ResumeConversation}");
    }

    private async void PresentRequestedDialogue(KotorDialogueRequested request)
    {
        try
        {
            inputLockedUntilMsec = Math.Max(
                inputLockedUntilMsec,
                Time.GetTicksMsec() + (ulong)Math.Ceiling(request.InputLockSeconds * 1000.0f));
            GD.Print($"NIKAMI_AURORA_DIALOGUE_TRIGGER status=queued " +
                     $"actor={request.ActorTag} event={request.UserEvent} " +
                     $"conversation={request.Conversation} starter={request.StarterIndex} " +
                     $"delay={request.DelaySeconds:F3} inputLock={request.InputLockSeconds:F3}");
            if (request.DelaySeconds > 0)
            {
                var timer = GetTree().CreateTimer(request.DelaySeconds);
                await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
            }
            if (openingDialogueGraph is null ||
                !request.ActorTag.Equals(dialogueOwnerActor, StringComparison.OrdinalIgnoreCase) ||
                !request.Conversation.Equals(
                    openingDialogueConversation, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Triggered dialogue could not resolve {request.ActorTag}:{request.Conversation}");
            dialoguePanel.Visible = false;
            StopDialoguePerformance();
            RestoreGameplayCamera();
            currentDialogueConversation = request.Conversation;
            PresentDialogueStarter(openingDialogueGraph, request.StarterIndex);
            GD.Print($"NIKAMI_AURORA_DIALOGUE_TRIGGER status=started " +
                     $"conversation={request.Conversation} starter={request.StarterIndex}");
        }
        catch (Exception exception)
        {
            GD.PushError($"NIKAMI_AURORA_DIALOGUE_TRIGGER status=fail error={exception}");
        }
    }

    private async void StartFirstEncounter()
    {
        if (firstEncounterStarted) return;
        firstEncounterStarted = true;
        try
        {
            var encounter = firstEncounter
                ?? throw new InvalidDataException("First encounter is unavailable");
            var graph = firstEncounterGraph
                ?? throw new InvalidDataException("First encounter dialogue is unavailable");
            cinematicSequenceActive = true;
            dialoguePanel.Visible = false;
            StopDialoguePerformance();
            var door = RequireInteractiveDoor(encounter.DoorTag);
            if (!IsDoorOpen(door))
                ToggleDoor(door);

            var playerWaypoint = encounter.PartyWaypoints.Single(item =>
                item.Tag.Equals("wp_end_room3_1", StringComparison.OrdinalIgnoreCase));
            var traskWaypoint = encounter.PartyWaypoints.Single(item =>
                item.Tag.Equals("wp_end_room3_2", StringComparison.OrdinalIgnoreCase));
            simulationPlayerPosition = ToNumerics(playerWaypoint.Position);
            playerBody.GlobalPosition = ToGodot(playerWaypoint.Position);
            yaw = playerWaypoint.Bearing;
            playerBody.Rotation = new Vector3(0, yaw, 0);
            cameraPivot.Rotation = new Vector3(pitch, 0, 0);
            if (!actorModels.TryGetValue("end_trask", out var trask))
                throw new InvalidDataException("First encounter could not resolve Trask");
            trask.GlobalPosition = ToGodot(traskWaypoint.Position);
            trask.Rotation = new Vector3(0, traskWaypoint.Bearing, 0);

            var openingControl = graph.Nodes["entry:0"];
            ApplyDialogueAnimations(openingControl);
            ApplyStaticDialogueCamera(26);
            currentDialogueNodeKey = "encounter:camera26";
            AssertFirstEncounterCameraBeat(26);
            GD.Print("NIKAMI_AURORA_FIRST_ENCOUNTER status=started " +
                     "door=end_door02 dialogue=end_room3 camera=26");

            await WaitSeconds(encounter.TimingSeconds.CameraSwitch);
            ApplyStaticDialogueCamera(19);
            currentDialogueNodeKey = "encounter:camera19";
            AssertFirstEncounterCameraBeat(19);
            PlayActorAnimation("end_sith2", "b7a1", false);
            PlayActorAnimation("end_soldier2", "c3d4", false);
            FireEncounterBlaster("end_sith2", "end_soldier2");

            await WaitSeconds(encounter.TimingSeconds.SecondAttack);
            PlayActorAnimation("end_sith3", "b7a1", false);
            PlayActorAnimation("end_soldier2", "c3d4", false);
            FireEncounterBlaster("end_sith3", "end_soldier2");

            var elapsedBeforeBattleMusic =
                encounter.TimingSeconds.CameraSwitch + encounter.TimingSeconds.SecondAttack;
            await WaitSeconds(Math.Max(
                0.0f,
                encounter.TimingSeconds.BattleMusic - elapsedBeforeBattleMusic));
            var audio = firstEncounterAudio
                ?? throw new InvalidDataException("First encounter audio is unavailable");
            SwitchAreaMusic(audio.BattleMusic, encounter.Audio.BattleMusic.Resref);
            await WaitSeconds(Math.Max(
                0.0f,
                encounter.TimingSeconds.FirstControlResume -
                encounter.TimingSeconds.BattleMusic));
            var secondControl = graph.Nodes["entry:1"];
            ApplyDialogueAnimations(secondControl);
            ApplyStaticDialogueCamera(20);
            currentDialogueNodeKey = "encounter:camera20";
            AssertFirstEncounterCameraBeat(20);
            PlayActorAnimation("end_sith2", "b7a1", false);
            FireEncounterBlaster("end_sith2", "end_soldier2");

            await WaitSeconds(encounter.TimingSeconds.ThirdAttack);
            PlayActorAnimation("end_soldier2", "die", false);
            await WaitSeconds(0.5f);

            currentDialogueConversation = encounter.SceneObject.Conversation;
            currentDialogueNodeKey = "entry:4";
            PresentDialogueNode(graph, "entry:4", new HashSet<string>(), 0);
        }
        catch (Exception exception)
        {
            cinematicSequenceActive = false;
            GD.PushError($"NIKAMI_AURORA_FIRST_ENCOUNTER status=fail error={exception}");
            if (launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_FIRST_ENCOUNTER") == "1")
                GetTree().Quit(1);
        }
    }

    private async System.Threading.Tasks.Task WaitSeconds(float seconds)
    {
        if (seconds <= 0) return;
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    private void AssertFirstEncounterCameraBeat(int cameraId)
    {
        var beat = KotorFirstEncounterCameraContract.Require(cameraId);
        Vector3 subjectCenter;
        if (beat.SubjectTag.Equals("PLAYER", StringComparison.OrdinalIgnoreCase))
        {
            subjectCenter = playerBody.GlobalPosition + Vector3.Up * 0.9f;
        }
        else
        {
            if (!actorModels.TryGetValue(beat.SubjectTag, out var actor))
                throw new InvalidDataException(
                    $"Encounter camera {cameraId} subject is unavailable: {beat.SubjectTag}");
            subjectCenter = actorTalkOffsets.TryGetValue(beat.SubjectTag, out var talkOffset)
                ? actor.GlobalTransform * talkOffset
                : actor.GlobalPosition + Vector3.Up * 1.0f;
        }
        AssertDesktopCameraFraming(
            $"encounter:camera{cameraId}:{beat.SubjectTag}",
            subjectCenter,
            beat.SubjectRadius,
            new CinematicFramingRequirements(
                beat.MinimumViewportMargin,
                beat.MinimumProjectedHeight,
                beat.MaximumProjectedHeight));
    }

    private void InitializeFirstEncounterCombat()
    {
        var encounter = firstEncounter
            ?? throw new InvalidDataException("First encounter is unavailable at combat handoff");
        var playerSource = playerCombat
            ?? throw new InvalidDataException("Player combat source is unavailable");
        var gameplay = RequireGameplaySimulation().CaptureSnapshot();
        var definitions = new List<KotorCombatantDefinition>
        {
            new(
                gameplay.PlayerPartyMemberId,
                0,
                gameplay.PlayerCurrentVitality,
                gameplay.PlayerMaximumVitality,
                playerSource.Defense,
                playerSource.AttackBonus,
                0,
                false,
                false,
                ToCombatWeapon(playerSource.Weapon ?? throw new InvalidDataException(
                    "Player combat weapon is unavailable")))
        };
        definitions.AddRange(encounter.Participants
            .Where(source => source.Tag.Equals("end_sith2", StringComparison.OrdinalIgnoreCase) ||
                             source.Tag.Equals("end_sith3", StringComparison.OrdinalIgnoreCase))
            .Select(source => new KotorCombatantDefinition(
                source.Tag,
                1,
                source.CurrentHitPoints,
                source.MaxHitPoints,
                source.Combat.Defense,
                source.Combat.AttackBonus,
                source.Combat.ChallengeRating,
                source.MinimumOneHitPoint,
                true,
                ToCombatWeapon(source.Combat.Weapon ?? throw new InvalidDataException(
                    $"Combat weapon is unavailable: {source.Tag}")))));
        firstEncounterCombat = new KotorCombatSimulation(
            definitions,
            combatExperienceTable ?? throw new InvalidDataException(
                "Combat XP table is unavailable"));
        selectedCombatTarget = "end_sith3";
        GD.Print("NIKAMI_AURORA_COMBAT status=ready " +
                 $"player={gameplay.PlayerPartyMemberId} target={selectedCombatTarget} " +
                 "controls=Tab:target,Space:attack");
    }

    private static KotorCombatWeaponDefinition ToCombatWeapon(
        CreatureCombatWeaponRecord source)
    {
        var damage = new List<KotorDamageComponent>();
        if (source.BaseDiceCount > 0)
            damage.Add(new KotorDamageComponent(
                source.BaseDiceCount, source.BaseDieSides, 0, source.DamageFlags, true));
        damage.AddRange(source.BonusDamage.Select(component =>
            new KotorDamageComponent(
                component.DiceCount,
                component.DieSides,
                component.Flat,
                component.DamageType)));
        return new KotorCombatWeaponDefinition(
            source.Resref,
            source.AttackModifier,
            source.CriticalThreat,
            source.CriticalMultiplier,
            source.Ranged,
            damage);
    }

    private void CycleFirstEncounterTarget()
    {
        var combat = firstEncounterCombat;
        if (combat is null) return;
        var living = combat.CaptureSnapshot().Values
            .Where(item => !item.IsDead && item.Id.StartsWith("end_sith", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Id)
            .ToArray();
        if (living.Length == 0) return;
        var current = Array.FindIndex(living, item =>
            item.Equals(selectedCombatTarget, StringComparison.OrdinalIgnoreCase));
        selectedCombatTarget = living[(current + 1 + living.Length) % living.Length];
        status.Text = $"TARGET: {selectedCombatTarget.ToUpperInvariant()}";
        GD.Print($"NIKAMI_AURORA_COMBAT status=target target={selectedCombatTarget}");
    }

    private void ResolveFirstEncounterPlayerTurn()
    {
        var combat = firstEncounterCombat;
        var encounter = firstEncounter;
        if (combat is null || encounter is null) return;
        var snapshot = combat.CaptureSnapshot();
        var playerId = RequireGameplaySimulation().CaptureSnapshot().PlayerPartyMemberId;
        if (snapshot[playerId].IsDead) return;
        if (!snapshot.TryGetValue(selectedCombatTarget, out var target) || target.IsDead)
        {
            CycleFirstEncounterTarget();
            snapshot = combat.CaptureSnapshot();
            if (!snapshot.TryGetValue(selectedCombatTarget, out target) || target.IsDead) return;
        }

        if (actorModels.TryGetValue(selectedCombatTarget, out var targetModel))
            FaceModelToward(playerModel!, targetModel.GlobalPosition + Vector3.Up);
        currentPlayerAnimation = "";
        PlayPlayerAnimation("c2a1");
        ResolveFirstEncounterAttack(
            playerId, selectedCombatTarget,
            playerCombat!.Weapon ?? throw new InvalidDataException(
                "Player combat weapon is unavailable"));

        snapshot = combat.CaptureSnapshot();
        if (snapshot.Values.Where(item => item.Id.StartsWith("end_sith", StringComparison.OrdinalIgnoreCase))
            .All(item => item.IsDead))
        {
            firstEncounterCombatReady = false;
            status.Text = "COMBAT COMPLETE";
            details.Text = $"Experience: {RequireGameplaySimulation().CaptureSnapshot().PlayerExperience}";
            GD.Print("NIKAMI_AURORA_COMBAT status=complete hostiles=0");
            if (launchEnvironment.Get("NIKAMI_AURORA_TEST_FIRST_COMBAT") == "1")
                RequestCleanExit(0);
            return;
        }

        foreach (var hostile in snapshot.Values.Where(item =>
                     !item.IsDead && item.Id.StartsWith("end_sith", StringComparison.OrdinalIgnoreCase)))
        {
            var source = encounter.Participants.Single(item =>
                item.Tag.Equals(hostile.Id, StringComparison.OrdinalIgnoreCase));
            PlayActorAnimation(hostile.Id, "b7a1", false);
            FireEncounterBlaster(hostile.Id, "PLAYER");
            ResolveFirstEncounterAttack(
                hostile.Id, playerId,
                source.Combat.Weapon!);
            if (combat.CaptureSnapshot()[playerId].IsDead)
            {
                firstEncounterCombatReady = false;
                currentPlayerAnimation = "";
                PlayPlayerAnimation("die");
                status.Text = "PLAYER DEFEATED";
                break;
            }
        }
    }

    private void ResolveFirstEncounterAttack(
        string attackerId,
        string targetId,
        CreatureCombatWeaponRecord weapon)
    {
        var combat = firstEncounterCombat
            ?? throw new InvalidOperationException("First encounter combat is not active");
        combat.QueueAttack(attackerId, targetId);
        var rolls = new List<int>();
        if (weapon.BaseDiceCount > 0)
            for (var index = 0; index < weapon.BaseDiceCount; index++)
                rolls.Add(combatRandom.Next(1, weapon.BaseDieSides + 1));
        foreach (var component in weapon.BonusDamage)
            for (var index = 0; index < component.DiceCount; index++)
                rolls.Add(combatRandom.Next(1, component.DieSides + 1));
        var playerLevel = RequireGameplaySimulation().CaptureSnapshot().PlayerLevel;
        var transition = combat.ResolveNextAttack(
            playerLevel, combatRandom.Next(1, 21), rolls);
        foreach (var combatEvent in transition.Events)
        {
            switch (combatEvent)
            {
                case KotorAttackResolved resolved:
                    GD.Print("NIKAMI_AURORA_COMBAT status=resolved " +
                             $"attacker={resolved.AttackerId} target={resolved.TargetId} " +
                             $"roll={resolved.D20} total={resolved.AttackTotal} " +
                             $"defense={resolved.TargetDefense} hit={resolved.Hit} " +
                             $"critical={resolved.Critical} damage={resolved.Damage} " +
                             $"hp={resolved.HitPointsBefore}->{resolved.HitPointsAfter}");
                    if (!resolved.Hit) break;
                    if (resolved.TargetId.Equals(
                            RequireGameplaySimulation().CaptureSnapshot().PlayerPartyMemberId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyGameplayTransition(RequireGameplaySimulation().ApplyCombatDamage(
                            resolved.AttackerId, resolved.Damage));
                        currentPlayerAnimation = "";
                        PlayPlayerAnimation("g2d1");
                    }
                    else
                    {
                        PlayActorAnimation(
                            resolved.TargetId,
                            resolved.HitPointsAfter == 0 ? "die" : "g5d1",
                            false);
                    }
                    break;
                case KotorCombatantDied died when died.ExperienceReward > 0:
                    ApplyGameplayTransition(RequireGameplaySimulation().AwardCombatExperience(
                        died.Id, died.ExperienceReward));
                    break;
            }
        }
    }

    private void FinishFirstEncounter()
    {
        if (actorModels.TryGetValue("end_sith2", out var sith2))
            FaceModelToward(sith2, playerBody.GlobalPosition + Vector3.Up * 1.0f);
        if (actorModels.TryGetValue("end_sith3", out var sith3))
            FaceModelToward(sith3, playerBody.GlobalPosition + Vector3.Up * 1.0f);
        PlayActorAnimation("end_soldier2", "dead");
        PlayActorAnimation("end_sith2", "b7a1", false);
        PlayActorAnimation("end_sith3", "b7a1", false);
        FireEncounterBlaster("end_sith3", "end_trask");
        RestoreFirstEncounterWaypointFacing();
        cinematicSequenceActive = false;
        RestoreGameplayCamera();
        LogThirdPersonCameraPath();
        AssertThirdPersonGameplayFacing();
        AssertDesktopCameraFraming(
            "encounter:third-person-player",
            playerBody.GlobalPosition + Vector3.Up * 0.9f,
            0.62f,
            new CinematicFramingRequirements(0.02f, 0.12f, 0.72f),
            camera);
        currentDialogueNodeKey = "encounter:gameplay-ready";
        InitializeFirstEncounterCombat();
        firstEncounterCombatReady = true;
        GD.Print("NIKAMI_AURORA_FIRST_ENCOUNTER status=gameplay-ready " +
                 "camera=third-person input=released music=battle " +
                 "hostiles=end_sith2,end_sith3 soldier=end_soldier2:dead");
    }

    private void RestoreFirstEncounterWaypointFacing()
    {
        var encounter = firstEncounter
            ?? throw new InvalidDataException("First encounter is unavailable at handoff");
        if (playerModel is null || !actorModels.TryGetValue("end_trask", out var trask))
            throw new InvalidDataException(
                "First encounter party is incomplete at gameplay handoff");
        var playerWaypoint = encounter.PartyWaypoints.Single(item =>
            item.Tag.Equals("wp_end_room3_1", StringComparison.OrdinalIgnoreCase));
        var traskWaypoint = encounter.PartyWaypoints.Single(item =>
            item.Tag.Equals("wp_end_room3_2", StringComparison.OrdinalIgnoreCase));
        yaw = playerWaypoint.Bearing;
        playerBody.Rotation = new Vector3(0, yaw, 0);
        FaceModelToward(
            playerModel,
            playerModel.GlobalPosition + HorizontalForward(playerBody));
        trask.Rotation = new Vector3(0, traskWaypoint.Bearing, 0);
        cameraPivot.Rotation = new Vector3(pitch, 0, 0);
    }

    private void AssertThirdPersonGameplayFacing()
    {
        if (xrActive || playerModel is null ||
            !actorModels.TryGetValue("end_trask", out var trask))
            return;
        var playerForward = HorizontalForward(playerModel);
        var traskForward = HorizontalForward(trask);
        var playerBodyForward = HorizontalForward(playerBody);
        var encounter = firstEncounter
            ?? throw new InvalidDataException("First encounter is unavailable at facing gate");
        var traskWaypoint = encounter.PartyWaypoints.Single(item =>
            item.Tag.Equals("wp_end_room3_2", StringComparison.OrdinalIgnoreCase));
        var expectedTraskForward = -(new Basis(
            Vector3.Up, traskWaypoint.Bearing).Z).Normalized();
        var passageDirection = HorizontalDirection(
            playerModel.GlobalPosition,
            RequireInteractiveDoor(encounter.DoorTag).ClosedPosition);
        var playerToCamera = HorizontalDirection(
            playerModel.GlobalPosition, camera.GlobalPosition);
        var playerWaypointDot = playerForward.Dot(playerBodyForward);
        var traskWaypointDot = traskForward.Dot(expectedTraskForward);
        var passageDot = playerForward.Dot(passageDirection);
        var cameraBehindDot = playerForward.Dot(playerToCamera);
        if (playerWaypointDot < 0.999f || traskWaypointDot < 0.999f ||
            passageDot < 0.65f || cameraBehindDot > -0.92f)
            throw new InvalidDataException(
                $"Third-person gameplay facing rejected: playerWaypoint={playerWaypointDot:F4} " +
                $"traskWaypoint={traskWaypointDot:F4} passage={passageDot:F4} " +
                $"cameraBehind={cameraBehindDot:F4}");
        GD.Print($"NIKAMI_AURORA_PLAYER_CAMERA status=behind-rendered-player " +
                 $"playerWaypoint={playerWaypointDot:F4} " +
                 $"traskWaypoint={traskWaypointDot:F4} passage={passageDot:F4} " +
                 $"cameraBehind={cameraBehindDot:F4}");
    }

    private static Vector3 HorizontalForward(Node3D node)
    {
        var forward = -node.GlobalBasis.Z;
        forward.Y = 0;
        if (forward.LengthSquared() < 0.0001f)
            throw new InvalidDataException($"Node has no horizontal forward basis: {node.Name}");
        return forward.Normalized();
    }

    private static Vector3 HorizontalDirection(Vector3 from, Vector3 to)
    {
        var direction = to - from;
        direction.Y = 0;
        if (direction.LengthSquared() < 0.0001f)
            throw new InvalidDataException("Facing target has no horizontal separation");
        return direction.Normalized();
    }

    private void LogThirdPersonCameraPath()
    {
        if (xrActive) return;
        var start = cameraArm.GlobalPosition;
        var desired = start + cameraArm.GlobalBasis.Z.Normalized() *
            cameraArm.SpringLength;
        var ray = PhysicsRayQueryParameters3D.Create(
            start, desired, CameraVisibilityCollisionLayer,
            new Godot.Collections.Array<Rid> { playerBody.GetRid() });
        ray.CollideWithAreas = false;
        ray.CollideWithBodies = true;
        var hit = cameraArm.GetWorld3D().DirectSpaceState.IntersectRay(ray);
        var occluder = "none";
        var hitPosition = desired;
        if (hit.TryGetValue("collider", out var collider) &&
            collider.AsGodotObject() is Node colliderNode)
            occluder = colliderNode.Name.ToString();
        if (hit.TryGetValue("position", out var position))
            hitPosition = position.AsVector3();
        GD.Print($"NIKAMI_AURORA_PLAYER_CAMERA status=handoff " +
                 $"player={playerBody.GlobalPosition} pivot={start} " +
                 $"actual={camera.GlobalPosition} desired={desired} " +
                 $"hitLength={cameraArm.GetHitLength():F4} " +
                 $"pathHit={start.DistanceTo(hitPosition):F4} occluder={occluder}");
    }

    private void AccumulateDynamicMaterialReport(DynamicMaterialReport report)
    {
        dynamicMaterialSurfaces += report.Surfaces;
        enhancedDynamicPbrSurfaces += report.EnhancedPbr;
        enhancedDynamicNormalSurfaces += report.NormalMapped;
        authoredDynamicNormalScaleSurfaces += report.AuthoredNormalScale;
        transparentDynamicSurfaces += report.Transparent;
        additiveDynamicSurfaces += report.Additive;
        configuredAdditiveDynamicSurfaces += report.ConfiguredAdditive;
    }

    private DynamicMaterialReport ConfigureDynamicObjectMaterials(
        Node node,
        bool enhanced)
    {
        var surfaces = 0;
        var enhancedPbr = 0;
        var normalMapped = 0;
        var transparent = 0;
        var additive = 0;
        var configuredAdditive = 0;
        var authoredNormalScale = 0;
        if (node is MeshInstance3D instance && instance.Mesh is not null)
        {
            for (var surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
            {
                if (instance.GetActiveMaterial(surface) is not BaseMaterial3D source)
                    continue;
                surfaces++;
                var sourceTransparent =
                    source.Transparency != BaseMaterial3D.TransparencyEnum.Disabled;
                var cycle = KotorEnvironmentMaterialPolicy.CycleTexture(
                    source.ResourceName.ToString());
                var sourceAdditive =
                    source.BlendMode == BaseMaterial3D.BlendModeEnum.Add ||
                    source.ResourceName.ToString().Contains(
                        KotorEnvironmentMaterialPolicy.AdditiveMarker,
                        StringComparison.OrdinalIgnoreCase);
                var sourceDecal = KotorEnvironmentMaterialPolicy.IsSourceDecal(
                    source.ResourceName.ToString());
                transparent += sourceTransparent ? 1 : 0;
                additive += sourceAdditive ? 1 : 0;
                var sourceNormalScale = ResolveNormalScale(source, out var hasAuthoredNormalScale);
                authoredNormalScale += hasAuthoredNormalScale ? 1 : 0;
                if (!enhanced && !sourceAdditive && !sourceDecal && cycle is null)
                    continue;

                var material = (BaseMaterial3D)source.Duplicate();
                material.AlbedoTexture = CreateCycleTexture(
                    source.AlbedoTexture, source.ResourceName.ToString());
                if (sourceAdditive)
                {
                    // glTF has no additive alpha mode. The importer therefore
                    // carries Odyssey's TXI identity in the material name and
                    // this runtime boundary restores the actual blend/depth
                    // contract for actor and equipped-weapon surfaces.
                    material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                    material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled;
                    material.NoDepthTest = false;
                    if (enhanced)
                    {
                        var color = material.AlbedoColor;
                        var glow = KotorModulePresentationPolicy
                            .AdditiveGlowMultiplier(enhancedPresentation: true);
                        material.AlbedoColor = new Color(
                            color.R * glow,
                            color.G * glow,
                            color.B * glow,
                            color.A);
                    }
                    configuredAdditive++;
                }
                if (sourceDecal)
                {
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled;
                    material.NoDepthTest = false;
                    material.RenderPriority =
                        KotorEnvironmentMaterialPolicy.SourceDecalRenderPriority;
                }
                if (enhanced && !sourceAdditive)
                {
                    material.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel;
                    material.Metallic = 0.0f;
                    material.MetallicSpecular =
                        KotorEnvironmentMaterialPolicy.EnhancedDielectricSpecular;
                    material.Roughness =
                        KotorEnvironmentMaterialPolicy.EnhancedFallbackRoughness;
                    material.NormalEnabled = source.NormalTexture is not null;
                    material.NormalScale = sourceNormalScale;
                }
                instance.SetSurfaceOverrideMaterial(surface, material);
                enhancedPbr += enhanced && !sourceAdditive ? 1 : 0;
                normalMapped += enhanced && !sourceAdditive && material.NormalEnabled ? 1 : 0;
            }
        }
        foreach (var child in node.GetChildren())
        {
            var childReport = ConfigureDynamicObjectMaterials(child, enhanced);
            surfaces += childReport.Surfaces;
            enhancedPbr += childReport.EnhancedPbr;
            normalMapped += childReport.NormalMapped;
            transparent += childReport.Transparent;
            additive += childReport.Additive;
            configuredAdditive += childReport.ConfiguredAdditive;
            authoredNormalScale += childReport.AuthoredNormalScale;
        }
        return new DynamicMaterialReport(
            surfaces, enhancedPbr, normalMapped, transparent, additive,
            configuredAdditive,
            authoredNormalScale);
    }

    private AuthoredLightReport LoadAuthoredLights(
        IEnumerable<RoomRecord> rooms,
        AreaLightingRecord lighting)
    {
        var loaded = 0;
        var bakedOnly = 0;
        var ambientOnly = 0;
        var disabled = 0;
        foreach (var room in rooms)
        {
            if (room.Lights is null) continue;
            foreach (var source in room.Lights)
            {
                // Odyssey treats radius >= 100 as directional. Keep that
                // source semantic out of this point-light path until its basis
                // and attenuation contract are explicitly implemented.
                if (source.Radius >= 100 && source.Multiplier > 0 &&
                    source.AffectDynamic && !source.AmbientOnly)
                    throw new InvalidDataException(
                        $"Unsupported Odyssey directional-light semantic: " +
                        $"{room.Model}/{source.Name}");
                if (source.Radius <= 0 || source.Multiplier <= 0)
                {
                    disabled++;
                    continue;
                }
                if (source.AmbientOnly)
                {
                    ambientOnly++;
                    continue;
                }
                if (!source.AffectDynamic)
                {
                    // The source explicitly excludes dynamic objects. Its
                    // static contribution is already carried by UV2 room
                    // lightmaps, so instantiating it would double-light rooms.
                    bakedOnly++;
                    continue;
                }
                var light = new OmniLight3D
                {
                    Name = $"SourceLight_{room.Model}_{source.Name}",
                    Position = ToGodotWithOffset(source.Position, room.Position),
                    LightColor = ToColor(source.Color),
                    LightEnergy = source.Multiplier,
                    LightSpecular = enhancedPresentation
                        ? KotorEnvironmentMaterialPolicy.EnhancedDielectricSpecular
                        : KotorEnvironmentMaterialPolicy.SourceDielectricSpecular,
                    OmniRange = source.Radius,
                    OmniAttenuation = 1.0f,
                    ShadowEnabled = lighting.Shadows && source.Shadow
                };
                AddChild(light);
                loaded++;
            }
        }
        return new AuthoredLightReport(loaded, bakedOnly, ambientOnly, disabled);
    }

    private StaticMaterialReport ConfigureStaticRoomMaterials(
        Node node,
        Color dynamicAmbient,
        IReadOnlyDictionary<string, Cubemap> environmentMaps,
        float reflectionStrength,
        float maximumReflectionWeight,
        KotorLightmapTransfer transfer,
        IDictionary<string, int> environmentBindings)
    {
        var lightmappedOpaque = 0;
        var baseOpaque = 0;
        var lightmappedTransparent = 0;
        var baseTransparent = 0;
        var sourceAdditiveCount = 0;
        var sourceDecalCount = 0;
        var enhancedPbrCount = 0;
        var normalMappedCount = 0;
        var authoredNormalScaleCount = 0;
        var additiveEnvironmentCount = 0;
        var additiveLightmappedCount = 0;
        if (node is MeshInstance3D instance && instance.Mesh is not null)
        {
            for (var surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
            {
                if (instance.GetActiveMaterial(surface) is not BaseMaterial3D source) continue;
                var sourceAdditive = source.ResourceName.ToString().Contains(
                    KotorEnvironmentMaterialPolicy.AdditiveMarker,
                    StringComparison.OrdinalIgnoreCase);
                var sourceDecal = KotorEnvironmentMaterialPolicy.IsSourceDecal(
                    source.ResourceName.ToString());
                var sourceTransparent =
                    source.Transparency != BaseMaterial3D.TransparencyEnum.Disabled;
                var albedoTexture = CreateCycleTexture(
                    source.AlbedoTexture, source.ResourceName.ToString());
                if (sourceDecal && !sourceTransparent)
                    throw new InvalidDataException(
                        $"Source decal did not retain transparency: {source.ResourceName}");
                sourceDecalCount += sourceDecal ? 1 : 0;
                var sourceNormalScale = ResolveNormalScale(
                    source, out var hasAuthoredNormalScale);
                authoredNormalScaleCount += hasAuthoredNormalScale ? 1 : 0;
                var environmentResref = KotorEnvironmentMaterialPolicy.EnvironmentMapResref(
                    source.ResourceName.ToString());
                Cubemap? environmentMap = null;
                if (environmentResref is not null)
                {
                    if (!environmentMaps.TryGetValue(environmentResref, out environmentMap))
                        throw new InvalidDataException(
                            $"Unsupported source environment material: {source.ResourceName}");
                    environmentBindings.TryGetValue(
                        environmentResref, out var existingEnvironmentBindings);
                    environmentBindings[environmentResref] =
                        existingEnvironmentBindings + 1;
                }
                if (source.AlbedoTexture is not null && source.EmissionTexture is not null)
                {
                    var useEnhancedLightmapPbr =
                        transfer.DynamicLightsEnabled && !sourceAdditive;
                    var lightmapped = new ShaderMaterial
                    {
                        Shader = sourceAdditive
                            ? environmentMap is not null
                                ? OdysseyAdditiveEnvironmentLightmapShader
                                : OdysseyAdditiveLightmapShader
                            : environmentMap is not null
                                ? sourceTransparent
                                    ? useEnhancedLightmapPbr
                                        ? OdysseyTransparentEnvironmentLightmapShader
                                        : OdysseySourceTransparentEnvironmentLightmapShader
                                    : useEnhancedLightmapPbr
                                        ? OdysseyEnvironmentLightmapShader
                                        : OdysseySourceEnvironmentLightmapShader
                            : sourceTransparent
                                ? useEnhancedLightmapPbr
                                    ? OdysseyTransparentLightmapShader
                                    : OdysseySourceTransparentLightmapShader
                                : useEnhancedLightmapPbr
                                    ? OdysseyLightmapShader
                                    : OdysseySourceLightmapShader,
                        ResourceName = source.ResourceName,
                        RenderPriority = sourceDecal
                            ? KotorEnvironmentMaterialPolicy.SourceDecalRenderPriority
                            : 0
                    };
                    lightmapped.SetShaderParameter("albedo_texture", albedoTexture!);
                    lightmapped.SetShaderParameter("albedo_tint", source.AlbedoColor);
                    lightmapped.SetShaderParameter("lightmap_texture", source.EmissionTexture);
                    lightmapped.SetShaderParameter("dynamic_ambient", new Vector3(
                        dynamicAmbient.R, dynamicAmbient.G, dynamicAmbient.B));
                    ConfigureLightmapTransfer(lightmapped, transfer);
                    var normalTexture = source.NormalTexture;
                    var hasNormalMap = useEnhancedLightmapPbr && normalTexture is not null;
                    if (!sourceAdditive)
                    {
                        if (useEnhancedLightmapPbr)
                        {
                            lightmapped.SetShaderParameter("has_normal_texture", hasNormalMap);
                            if (normalTexture is not null && hasNormalMap)
                                lightmapped.SetShaderParameter("normal_texture", normalTexture);
                            lightmapped.SetShaderParameter("normal_scale", sourceNormalScale);
                        }
                        lightmapped.SetShaderParameter(
                            "dielectric_specular",
                            environmentMap is null
                                ? KotorEnvironmentMaterialPolicy.DielectricSpecular(
                                    useEnhancedLightmapPbr)
                                : 0.0f);
                        lightmapped.SetShaderParameter(
                            "material_roughness",
                            KotorEnvironmentMaterialPolicy.FallbackRoughness(
                                useEnhancedLightmapPbr));
                    }
                    enhancedPbrCount += useEnhancedLightmapPbr ? 1 : 0;
                    normalMappedCount += hasNormalMap ? 1 : 0;
                    if (environmentMap is not null)
                    {
                        lightmapped.SetShaderParameter("environment_map", environmentMap);
                        lightmapped.SetShaderParameter(
                            "reflection_strength", reflectionStrength);
                        lightmapped.SetShaderParameter(
                            "maximum_reflection_weight", maximumReflectionWeight);
                    }
                    instance.SetSurfaceOverrideMaterial(surface, lightmapped);
                    if (sourceTransparent)
                        lightmappedTransparent++;
                    else
                        lightmappedOpaque++;
                    if (sourceAdditive)
                    {
                        sourceAdditiveCount++;
                        additiveLightmappedCount++;
                        additiveEnvironmentCount += environmentMap is not null ? 1 : 0;
                    }
                    continue;
                }
                if (environmentMap is not null)
                {
                    var hasNormalMap = transfer.DynamicLightsEnabled && !sourceAdditive &&
                                       source.NormalTexture is not null;
                    instance.SetSurfaceOverrideMaterial(surface,
                        CreateEnvironmentMaterial(
                            source, environmentMap, reflectionStrength,
                            maximumReflectionWeight,
                            transfer.DynamicLightsEnabled && !sourceAdditive));
                    enhancedPbrCount +=
                        transfer.DynamicLightsEnabled && !sourceAdditive ? 1 : 0;
                    normalMappedCount += hasNormalMap ? 1 : 0;
                    if (sourceTransparent)
                        baseTransparent++;
                    else
                        baseOpaque++;
                    if (sourceAdditive)
                    {
                        sourceAdditiveCount++;
                        additiveEnvironmentCount++;
                    }
                    continue;
                }
                var material = (BaseMaterial3D)source.Duplicate();
                material.AlbedoTexture = albedoTexture;
                var enhancedPbr = transfer.DynamicLightsEnabled && !sourceAdditive;
                material.ShadingMode = enhancedPbr
                    ? BaseMaterial3D.ShadingModeEnum.PerPixel
                    : BaseMaterial3D.ShadingModeEnum.Unshaded;
                material.Transparency = sourceTransparent
                    ? BaseMaterial3D.TransparencyEnum.Alpha
                    : BaseMaterial3D.TransparencyEnum.Disabled;
                material.BlendMode = sourceAdditive
                    ? BaseMaterial3D.BlendModeEnum.Add
                    : BaseMaterial3D.BlendModeEnum.Mix;
                material.DepthDrawMode = sourceTransparent
                    ? BaseMaterial3D.DepthDrawModeEnum.Disabled
                    : BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
                material.NoDepthTest = false;
                material.RenderPriority = sourceDecal
                    ? KotorEnvironmentMaterialPolicy.SourceDecalRenderPriority
                    : 0;
                var albedo = material.AlbedoColor;
                if (!sourceTransparent)
                    albedo.A = 1.0f;
                material.AlbedoColor = albedo;
                material.Metallic = 0;
                material.MetallicSpecular =
                    KotorEnvironmentMaterialPolicy.DielectricSpecular(enhancedPbr);
                material.Roughness =
                    KotorEnvironmentMaterialPolicy.FallbackRoughness(enhancedPbr);
                material.NormalEnabled = enhancedPbr && source.NormalTexture is not null;
                material.NormalScale = sourceNormalScale;
                enhancedPbrCount += enhancedPbr ? 1 : 0;
                normalMappedCount += material.NormalEnabled ? 1 : 0;
                instance.SetSurfaceOverrideMaterial(surface, material);
                if (sourceTransparent)
                    baseTransparent++;
                else
                    baseOpaque++;
                if (sourceAdditive)
                    sourceAdditiveCount++;
            }
        }
        foreach (var child in node.GetChildren())
        {
            var childReport = ConfigureStaticRoomMaterials(
                child, dynamicAmbient, environmentMaps, reflectionStrength,
                maximumReflectionWeight, transfer,
                environmentBindings);
            lightmappedOpaque += childReport.LightmappedOpaque;
            baseOpaque += childReport.BaseOpaque;
            lightmappedTransparent += childReport.LightmappedTransparent;
            baseTransparent += childReport.BaseTransparent;
            sourceAdditiveCount += childReport.SourceAdditive;
            sourceDecalCount += childReport.SourceDecal;
            enhancedPbrCount += childReport.EnhancedPbr;
            normalMappedCount += childReport.NormalMapped;
            authoredNormalScaleCount += childReport.AuthoredNormalScale;
            additiveEnvironmentCount += childReport.AdditiveEnvironment;
            additiveLightmappedCount += childReport.AdditiveLightmapped;
        }
        return new StaticMaterialReport(
            lightmappedOpaque, baseOpaque, lightmappedTransparent, baseTransparent,
            sourceAdditiveCount, sourceDecalCount, enhancedPbrCount, normalMappedCount,
            authoredNormalScaleCount, additiveEnvironmentCount,
            additiveLightmappedCount);
    }

    private static void ConfigureLightmapTransfer(
        ShaderMaterial material,
        KotorLightmapTransfer transfer)
    {
        material.SetShaderParameter(
            "dynamic_light_albedo_weight", transfer.DynamicLightAlbedoWeight);
        material.SetShaderParameter(
            "baked_emission_weight", transfer.BakedEmissionWeight);
        material.SetShaderParameter(
            "dynamic_ambient_emission_weight", transfer.DynamicAmbientEmissionWeight);
    }

    private ShaderMaterial CreateEnvironmentMaterial(
        BaseMaterial3D source,
        Cubemap environmentMap,
        float reflectionStrength,
        float maximumReflectionWeight,
        bool enhancedPbr)
    {
        if (source.AlbedoTexture is null)
            throw new InvalidDataException(
                $"Environment material lacks an albedo texture: {source.ResourceName}");
        var sourceTransparent =
            source.Transparency != BaseMaterial3D.TransparencyEnum.Disabled;
        var sourceAdditive = source.ResourceName.ToString().Contains(
            KotorEnvironmentMaterialPolicy.AdditiveMarker,
            StringComparison.OrdinalIgnoreCase);
        var material = new ShaderMaterial
        {
            Shader = sourceAdditive
                ? OdysseyAdditiveEnvironmentShader
                : sourceTransparent
                    ? enhancedPbr
                        ? OdysseyTransparentEnvironmentShader
                        : OdysseySourceTransparentEnvironmentShader
                    : enhancedPbr
                        ? OdysseyEnvironmentShader
                        : OdysseySourceEnvironmentShader,
            ResourceName = source.ResourceName,
            RenderPriority = KotorEnvironmentMaterialPolicy.IsSourceDecal(
                source.ResourceName.ToString())
                ? KotorEnvironmentMaterialPolicy.SourceDecalRenderPriority
                : 0
        };
        material.SetShaderParameter(
            "albedo_texture",
            CreateCycleTexture(source.AlbedoTexture, source.ResourceName.ToString())!);
        material.SetShaderParameter("environment_map", environmentMap);
        material.SetShaderParameter("albedo_tint", source.AlbedoColor);
        var normalTexture = source.NormalTexture;
        var normalScale = ResolveNormalScale(source, out _);
        var hasNormalMap = enhancedPbr && !sourceAdditive && normalTexture is not null;
        if (!sourceAdditive && enhancedPbr)
        {
            material.SetShaderParameter("has_normal_texture", hasNormalMap);
            if (normalTexture is not null && hasNormalMap)
                material.SetShaderParameter("normal_texture", normalTexture);
            material.SetShaderParameter("normal_scale", normalScale);
        }
        material.SetShaderParameter("reflection_strength", reflectionStrength);
        material.SetShaderParameter(
            "maximum_reflection_weight", maximumReflectionWeight);
        return material;
    }

    private Texture2D? CreateCycleTexture(Texture2D? source, string materialName)
    {
        var cycle = KotorEnvironmentMaterialPolicy.CycleTexture(materialName);
        if (cycle is null)
            return source;
        if (source is null)
            throw new InvalidDataException(
                $"Cycle material lacks an albedo texture: {materialName}");
        var sourceImage = source.GetImage();
        var frameWidth = sourceImage.GetWidth() / cycle.Value.Columns;
        var frameHeight = sourceImage.GetHeight() / cycle.Value.Rows;
        if (frameWidth <= 0 || frameHeight <= 0 ||
            frameWidth * cycle.Value.Columns != sourceImage.GetWidth() ||
            frameHeight * cycle.Value.Rows != sourceImage.GetHeight())
            throw new InvalidDataException(
                $"Cycle atlas dimensions do not divide the source texture: {materialName}");
        var frameCount = cycle.Value.Columns * cycle.Value.Rows;
        var frames = new List<Image>(frameCount);
        for (var frame = 0; frame < frameCount; frame++)
            frames.Add(sourceImage.GetRegion(new Rect2I(
                frame % cycle.Value.Columns * frameWidth,
                frame / cycle.Value.Columns * frameHeight,
                frameWidth,
                frameHeight)));
        var texture = ImageTexture.CreateFromImage(frames[0]);
        texture.ResourceName = materialName;
        cycleTextures.Add(new CycleTextureBinding(
            texture, frames, cycle.Value.FramesPerSecond));
        return texture;
    }

    private void AdvanceCycleTextures(double delta)
    {
        foreach (var binding in cycleTextures)
        {
            binding.Elapsed += delta;
            var frame = (int)Math.Floor(
                binding.Elapsed * binding.FramesPerSecond) % binding.Frames.Count;
            if (frame == binding.CurrentFrame)
                continue;
            binding.CurrentFrame = frame;
            binding.Texture.Update(binding.Frames[frame]);
        }
    }

    private sealed class CycleTextureBinding(
        ImageTexture texture,
        IReadOnlyList<Image> frames,
        float framesPerSecond)
    {
        public ImageTexture Texture { get; } = texture;
        public IReadOnlyList<Image> Frames { get; } = frames;
        public float FramesPerSecond { get; } = framesPerSecond;
        public double Elapsed { get; set; }
        public int CurrentFrame { get; set; }
    }

    private static float ResolveNormalScale(
        BaseMaterial3D source, out bool authored)
    {
        var scale = KotorEnvironmentMaterialPolicy.AuthoredNormalScale(
            source.ResourceName.ToString());
        authored = scale.HasValue;
        if (authored && source.NormalTexture is null)
            throw new InvalidDataException(
                $"Authored normal scale lacks a normal texture: {source.ResourceName}");
        return scale ?? source.NormalScale;
    }

    private void ConfigureSourceEnvironmentMaterials(
        Node node,
        IReadOnlyDictionary<string, Cubemap> environmentMaps,
        float reflectionStrength,
        float maximumReflectionWeight,
        IDictionary<string, int> environmentBindings,
        bool enhancedPbr)
    {
        if (node is MeshInstance3D instance && instance.Mesh is not null)
        {
            for (var surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
            {
                if (instance.GetActiveMaterial(surface) is not BaseMaterial3D source)
                    continue;
                var materialName = source.ResourceName.ToString();
                var environmentResref =
                    KotorEnvironmentMaterialPolicy.EnvironmentMapResref(materialName);
                if (environmentResref is null)
                    continue;
                if (materialName.Contains(
                        KotorEnvironmentMaterialPolicy.AdditiveMarker,
                        StringComparison.OrdinalIgnoreCase) ||
                    !environmentMaps.TryGetValue(environmentResref, out var environmentMap))
                    throw new InvalidDataException(
                        $"Unsupported source environment material: {materialName}");
                instance.SetSurfaceOverrideMaterial(surface,
                    CreateEnvironmentMaterial(
                        source, environmentMap, reflectionStrength,
                        maximumReflectionWeight, enhancedPbr));
                environmentBindings.TryGetValue(
                    environmentResref, out var existingEnvironmentBindings);
                environmentBindings[environmentResref] =
                    existingEnvironmentBindings + 1;
            }
        }
        foreach (var child in node.GetChildren())
            ConfigureSourceEnvironmentMaterials(
                child, environmentMaps, reflectionStrength,
                maximumReflectionWeight, environmentBindings, enhancedPbr);
    }

    private void BuildNavigation(IEnumerable<RoomRecord> rooms)
    {
        navigationTriangles.Clear();
        var profileTriangles = new List<KotorNavigationTriangle>();
        foreach (var room in rooms)
        {
            if (room.WalkmeshTriangles is null) continue;
            foreach (var triangle in room.WalkmeshTriangles)
            {
                if (triangle.Count != 3) continue;
                var nativeA = ToNumericsWithOffset(triangle[0], room.Position);
                var nativeB = ToNumericsWithOffset(triangle[1], room.Position);
                var nativeC = ToNumericsWithOffset(triangle[2], room.Position);
                var denominator = (nativeB.Y - nativeC.Y) * (nativeA.X - nativeC.X) +
                                  (nativeC.X - nativeB.X) * (nativeA.Y - nativeC.Y);
                if (!float.IsFinite(denominator) || MathF.Abs(denominator) < 0.000001f)
                    continue;
                navigationTriangles.Add(new NavigationTriangle(
                    ToGodotWithOffset(triangle[0], room.Position),
                    ToGodotWithOffset(triangle[1], room.Position),
                    ToGodotWithOffset(triangle[2], room.Position)));
                profileTriangles.Add(new KotorNavigationTriangle(
                    nativeA, nativeB, nativeC));
            }
        }
        movementSimulation = new KotorMovementSimulation(
            profileTriangles,
            new KotorMovementConfiguration(playerWalkSpeed, playerRunSpeed));
    }

    private bool MovePlayer(Vector3 displacement)
    {
        if (movementSimulation is null) return false;
        var nativeDisplacement = new NumericsVector3(displacement.X, -displacement.Z, displacement.Y);
        var result = movementSimulation.TryDisplace(
            simulationPlayerPosition, nativeDisplacement, CurrentDoorObstacles());
        ApplyMovementResult(result);
        return result.Accepted;
    }

    private KotorMovementResult StepPlayer(KotorMovementIntent intent, float deltaSeconds)
    {
        if (movementSimulation is null)
            return new KotorMovementResult(
                simulationPlayerPosition, false, false, KotorLocomotionMode.Idle);
        var movementFacing = yaw;
        if (xrActive)
        {
            var headForward = -xrCamera.GlobalBasis.Z;
            headForward.Y = 0;
            if (headForward.LengthSquared() > 0.000001f)
            {
                headForward = headForward.Normalized();
                movementFacing = Mathf.Atan2(-headForward.X, -headForward.Z);
            }
        }
        var result = movementSimulation.Step(
            simulationPlayerPosition, movementFacing, intent, deltaSeconds,
            CurrentDoorObstacles());
        ApplyMovementResult(result);
        return result;
    }

    private IReadOnlyList<KotorDoorObstacle> CurrentDoorObstacles()
    {
        currentDoorObstacles.Clear();
        foreach (var door in interactiveDoors)
            currentDoorObstacles.Add(new KotorDoorObstacle(
                ToNumerics(door.Source.Position), IsDoorOpen(door)));
        return currentDoorObstacles;
    }

    private void ApplyMovementResult(KotorMovementResult result)
    {
        if (!result.Accepted) return;
        var previous = simulationPlayerPosition;
        simulationPlayerPosition = result.Position;
        playerBody.GlobalPosition = ToGodot(result.Position);
        if (result.Moved && gameplaySimulation is not null)
            ApplyGameplayTransition(
                gameplaySimulation.UpdateTriggers(previous, result.Position));
    }

    private bool TryProjectToWalkmesh(Vector3 position, out float ground)
    {
        if (movementSimulation is not null &&
            movementSimulation.TryProjectToWalkmesh(ToNumerics(position), out var nativeGround))
        {
            ground = nativeGround;
            return true;
        }
        ground = 0;
        return false;
    }

    private static Vector3 ToGodot(IReadOnlyList<float> source) =>
        new(source[0], source[2], -source[1]);

    private static Vector3 ToGodotPresentation(NumericsVector3 source) =>
        new(source.X, source.Y, source.Z);

    private static Vector3 ToGodot(NumericsVector3 source) =>
        new(source.X, source.Z, -source.Y);

    private static NumericsVector3 ToNumerics(IReadOnlyList<float> source) =>
        new(source[0], source[1], source[2]);

    private static NumericsVector3 ToNumerics(Vector3 source) =>
        new(source.X, -source.Z, source.Y);

    private static NumericsVector3 ToNumericsPresentation(Vector3 source) =>
        new(source.X, source.Y, source.Z);

    private static NumericsVector3 ToNumericsWithOffset(
        IReadOnlyList<float> source, IReadOnlyList<float> offset) =>
        new(source[0] + offset[0], source[1] + offset[1], source[2] + offset[2]);

    private static Color ToColor(IReadOnlyList<float> source) =>
        new(source[0], source[1], source[2]);

    private static Vector3 ToGodotWithOffset(IReadOnlyList<float> source, IReadOnlyList<float> offset) =>
        ToGodot(new[] { source[0] + offset[0], source[1] + offset[1], source[2] + offset[2] });
}
