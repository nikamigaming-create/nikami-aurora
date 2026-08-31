using Godot;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nikami.Aurora.GodotRuntime.Infrastructure.Serialization;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Bootstrap;
using Nikami.Aurora.GodotRuntime.Domain.Abilities;
using Nikami.Aurora.GodotRuntime.Domain.Characters;
using Nikami.Aurora.GodotRuntime.Domain.Inventory;
using Nikami.Aurora.GodotRuntime.Domain.Quests;
using Nikami.Aurora.GodotRuntime.Domain.Sessions;
using Nikami.Aurora.GodotRuntime.Domain.Story;
using Nikami.Aurora.GodotRuntime.Domain.World;
using Nikami.Aurora.GodotRuntime.Infrastructure.World;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;
using Nikami.Aurora.GodotRuntime.Launcher;
using Nikami.Aurora.GodotRuntime.Presentation.Cinematics;
using Nikami.Aurora.GodotRuntime.Presentation.Player;
using Nikami.Aurora.GodotRuntime.Application.Characters;
using Nikami.Aurora.Core;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.GodotRuntime.Presentation.World;

public partial class OpenDaoWorld : Node3D
{
    private const string DaoSkyShaderPath = "res://shaders/dao_sky.gdshader";
    private const string DaoCloudVolumeShaderPath = "res://shaders/dao_cloud_volume.gdshader";
    private const string PlayableSmokeStartupFramesVariable =
        "OPENDAO_ACCEPTANCE_PLAYABLE_STARTUP_FRAMES";
    private const string AlienageArrivalWarmupFramesVariable =
        "OPENDAO_ACCEPTANCE_ALIENAGE_WARMUP_FRAMES";
    private const string GameplayHoldFramesVariable = "OPENDAO_ACCEPTANCE_GAMEPLAY_HOLD_FRAMES";
    private const string AlienageSkyCaptureVariable = "OPENDAO_CITY_ELF_SKY_CAPTURE";
    private const string CharacterPbrCloseCaptureVariable =
        "OPENDAO_CHARACTER_PBR_CLOSE_CAPTURE";
    private const string EffectCloseCaptureVariable = "OPENDAO_EFFECT_CLOSE_CAPTURE";
    private const string EffectCloseCaptureResRefVariable =
        "OPENDAO_EFFECT_CLOSE_RESREF";
    private const string AreaRuntimeEvidenceRootVariable =
        "OPENDAO_AREA_RUNTIME_EVIDENCE_ROOT";
    private const int GameplayCameraStableFrames = 3;
    private const int GameplayCameraMaximumSettleFrames = 24;

    private sealed record GameplayFrameEvidence(
        DaoGameplayCameraAcceptance Camera,
        int StableFrames,
        int NeighboringFrames,
        float AuthoredArmLength,
        float SelectedArmLength,
        float NonClearRatio,
        float MeanLuminance,
        float LuminanceStandardDeviation,
        float LuminanceRange,
        float DominantColorRatio)
    {
        public bool CollisionSafe =>
            Camera.ActualArmLength >= Camera.MinimumArmLength &&
            Camera.PredictedArmLength >= Camera.MinimumArmLength;
        public bool NonClearCoverage => NonClearRatio >= .55f;
        public bool ImageDetail => LuminanceStandardDeviation >= .05f &&
                                   LuminanceRange >= .18f &&
                                   DominantColorRatio <= .40f;
        public bool Passed => Camera.Passed && CollisionSafe &&
                              StableFrames >= GameplayCameraStableFrames &&
                              NonClearCoverage && ImageDetail;
    }

    private sealed record ContinuousGameplayEvidence(
        string Segment,
        string Target,
        bool PathReady,
        bool Reached,
        int PathNodes,
        int Frames,
        float TravelDistance,
        float TargetDistance,
        int CameraSamples,
        int CameraFailures,
        float MinimumActualArm,
        float MinimumPredictedArm,
        bool AuthoredWalk,
        bool CaptureSaved,
        GameplayFrameEvidence? CaptureFrame)
    {
        public bool Passed => PathReady && Reached && TravelDistance >= 2 &&
                              CameraSamples > 0 && CameraFailures == 0 &&
                              AuthoredWalk && CaptureSaved &&
                              CaptureFrame?.Passed == true;
    }

    private sealed record InWorldCreatureFrameEvidence(
        string ActorIdentity,
        string ActorTag,
        int PlacementOrdinal,
        string AuthoredPosition,
        string AuthoredRotation,
        string AuthoredTransformSha256,
        string ModelSha256,
        string Status,
        string Reason,
        float ProjectedHeight,
        float ScreenCoverage,
        int ClearLineOfSightProbes,
        float CropLuminanceStandardDeviation,
        float CropLuminanceRange,
        float CropDominantColorRatio,
        string EnvironmentFramePath,
        string EnvironmentFrameSha256,
        string CapturePath,
        string CaptureSha256);

    private DaoRuntimeServices? services;
    private DaoPresentationConfiguration presentationConfiguration = null!;
    private CancellationTokenSource? lifetime;
    private WorldProfile? profile;
    private CharacterProfile character = CharacterProfile.Default;
    private PlayerController player = null!;
    private Label status = null!;
    private WorldHud? hud;
    private OpeningCutsceneController? openingCutscene;
    private AuthoredDialogueController? openingDialogue;
    private PlaceableHighlightController? placeableHighlighter;
    private DaoLoadingPresentation? loadingPresentation;
    private string playerModelPath = string.Empty;
    private string playerBedModelPath = string.Empty;
    private WorldArrival? currentWorldArrival;
    private bool loading;

    public override async void _Ready()
    {
        try
        {
            services = new DaoRuntimeServices();
            presentationConfiguration = DaoPresentationConfiguration.Load();
            lifetime = new CancellationTokenSource();
            profile = services.GetRequired<IWorldProfileProvider>().Load();
            loadingPresentation = DaoLoadingPresentation.Show(
                this, GameInstallation.ResolveConfiguredRoot(), profile.AreaId);
            if (await loadingPresentation.CaptureIfRequestedAsync(lifetime.Token)) return;
            player = GetNode<PlayerController>("Player");
            status = GetNode<Label>("Status");
            character = services.GetRequired<ICharacterProfileProvider>().Load();
            playerModelPath = services.GetRequired<ICharacterModelResolver>().Resolve(character);
            playerBedModelPath = services.GetRequired<ICharacterCinematicModelResolver>()
                .Resolve(character, playerModelPath).BedModelPath;
            var locomotion = services.GetRequired<ILocomotionAnimationProvider>().Resolve(character);
            player.SetAvatar(playerModelPath, locomotion,
                services.GetRequired<Nikami.Aurora.GodotRuntime.Infrastructure.World.IGodotModelPostprocessor>());
            GD.Print($"OPENDAO_CHARACTER name={character.DisplayName} race={character.Race} " +
                     $"class={character.Class} origin={character.Origin} appearance={character.Appearance}");
            await LoadWorld(lifetime.Token);
        }
        catch (Exception error)
        {
            loadingPresentation?.Hide();
            GD.PushError("Nikami.Aurora.GodotRuntime startup failed: " + error);
            if (IsInstanceValid(status)) status.Text = "OPENDAO AREA VIEWER\nStartup failed: " + error.Message;
        }
    }

    public override void _Process(double delta)
    {
        services?.GetRequired<AbilityState>().Advance(delta);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (openingCutscene?.IsPlaying == true && key.Keycode is Key.Escape or Key.Space)
        {
            openingCutscene.Skip();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (openingDialogue?.IsPlaying == true) return;
        switch (key.Keycode)
        {
            case Key.I: hud?.ToggleInventory(); GetViewport().SetInputAsHandled(); break;
            case Key.J: hud?.ToggleQuests(); GetViewport().SetInputAsHandled(); break;
            case Key.K: hud?.ToggleAbilities(); GetViewport().SetInputAsHandled(); break;
            case Key.Key1:
                services?.GetRequired<AbilityState>().ActivateSlot(1);
                GetViewport().SetInputAsHandled();
                break;
            case Key.Escape when hud?.IsOpen == true:
                hud.Close(); GetViewport().SetInputAsHandled(); break;
            case Key.Escape when !loading:
                System.Environment.SetEnvironmentVariable("OPENDAO_AREA_BROWSER", "1");
                GetTree().ChangeSceneToFile("res://dao_boot.tscn");
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    public override void _ExitTree()
    {
        SaveSession();
        lifetime?.Cancel();
        lifetime?.Dispose();
        services?.Dispose();
        lifetime = null;
        services = null;
    }

    private async Task LoadWorld(CancellationToken cancellationToken)
    {
        if (services is null) return;
        loading = true;
        player.SetPhysicsProcess(false);
        player.Velocity = Vector3.Zero;
        profile ??= services.GetRequired<IWorldProfileProvider>().Load();
        var storyInitialization = services.GetRequired<CharacterStoryInitializer>()
            .Initialize(character);
        if (!storyInitialization.Succeeded)
            throw new InvalidOperationException($"Unable to initialize character story state: " +
                                                storyInitialization.Reason);
        GD.Print($"OPENDAO_CHARACTER_STORY status=ready " +
                 $"plot={storyInitialization.Data["plotGuid"]} " +
                 $"class_flag={storyInitialization.Data["classFlag"]} " +
                 $"gender_flag={storyInitialization.Data["genderFlag"]} " +
                 $"race_flag={storyInitialization.Data["raceFlag"]} " +
                 $"source={storyInitialization.Data["source"]}");
        var abilityLoadout = services.GetRequired<CharacterAbilityInitializer>().Initialize(character);
        if (abilityLoadout.Succeeded)
        {
            var slots = (IReadOnlyDictionary<int, int>)abilityLoadout.Data["quickSlots"]!;
            GD.Print($"OPENDAO_CHARACTER_ABILITIES status=ready class={character.Class} " +
                     $"origin={character.Origin} ids={string.Join(',', (IEnumerable<int>)abilityLoadout.Data["abilityIds"]!)} " +
                     $"slots={string.Join(',', slots.Select(value => $"{value.Key}:{value.Value}"))} " +
                     $"source=installed-gda");
        }
        else
        {
            GD.PushWarning("OPENDAO_CHARACTER_ABILITIES status=unavailable reason=" + abilityLoadout.Reason);
        }
        hud = new WorldHud(this, services.GetRequired<AbilityState>(),
            services.GetRequired<InventoryState>(), services.GetRequired<QuestJournal>(),
            profile, services.GetRequired<IAreaPresentationProvider>(), character, player);
        hud.Visible = false;
        var gameplayAvatar = player.GetNodeOrNull<Node3D>("AvatarRoot");
        if (gameplayAvatar is not null) gameplayAvatar.Visible = false;
        status.Text = $"Streaming {profile.DisplayName}…";
        var started = Time.GetTicksMsec();
        var result = await services.GetRequired<IWorldContentLoader>()
            .LoadAsync(profile, GetNode<Node3D>("DAOScene"), cancellationToken);
        if (!result.Succeeded)
        {
            status.Text = $"OPENDAO AREA VIEWER\nUnable to load {profile.DisplayName}: {result.Error}";
            GD.PushError("OPENDAO_WORLD_LOAD_FAIL reason=" + result.Error);
            player.SetPhysicsProcess(true);
            loading = false;
            return;
        }
        currentWorldArrival = services.GetRequired<IWorldArrivalResolver>().Resolve(profile);
        if (currentWorldArrival is not null)
            player.GlobalTransform = currentWorldArrival.Transform;
        else
            RestoreSession();
        if (!player.SnapToWalkableGround(player.GlobalPosition, $"world-load:{profile.AreaId}", false))
            GD.PushWarning($"Player spawn has no walkable surface in {profile.AreaId}: {player.GlobalPosition}");
        var renderingBackend = RenderingQualityPolicy.ParseBackend(
            RenderingServer.GetCurrentRenderingMethod().ToString());
        var presentationTier = RenderingQualityPolicy.ParseTier(
            System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_PRESENTATION_TIER"),
            renderingBackend);
        player.ConfigureThirdPersonView(presentationConfiguration.GameplayCamera,
            presentationTier == RenderingPresentationTier.Enhanced);
        player.SetAuthoredNavigation(result.Navigation);
        ConfigureLighting(result.AuthoredLights, result.Lighting, profile.LayoutName);
        if (result.Lighting is not null)
        {
            // Convert the Godot arrival point back to DAO's Z-up coordinates
            // before selecting the three point lights exposed by Hair0/Face1.
            services.GetRequired<ICharacterLightingBinder>().Apply(result.Lighting,
                player.GlobalPosition.X, -player.GlobalPosition.Z, player.GlobalPosition.Y);
        }
        GD.Print($"OPENDAO_WORLD_READY area={profile.AreaId} instances={result.Instances} actors={result.Actors} " +
                 $"draw_nodes={result.DrawNodes} collision_shapes={result.CollisionShapes} " +
                 $"authored_blockers={result.AuthoredBlockers} authored_lights={result.AuthoredLights} " +
                 $"authored_navigation={(result.Navigation is null ? 0 : 1)} " +
                 $"max_work_slice_ms={result.MaxWorkSliceMilliseconds:F2} " +
                 $"cache_hits={result.CacheHits} cache_misses={result.CacheMisses} yields={result.CooperativeYields} " +
                 $"elapsed_ms={Time.GetTicksMsec() - started}");
        var areaRuntimeEvidenceRequested = !string.IsNullOrWhiteSpace(
            System.Environment.GetEnvironmentVariable(AreaRuntimeEvidenceRootVariable));
        var origin = Nikami.Aurora.GodotRuntime.MainMenu.CharacterProfileRules.OriginFor(character.Origin);
        var hasOpeningDialogue = !areaRuntimeEvidenceRequested &&
                                 ShouldPlayOpeningDialogue(profile, origin);
        if (!areaRuntimeEvidenceRequested && ShouldPlayOpening(profile, origin))
        {
            openingCutscene = new OpeningCutsceneController
            {
                Name = "OpeningCutscene",
                CutsceneId = origin!.OpeningCutscene
            };
            AddChild(openingCutscene);
            await openingCutscene.PlayAsync(player.GetNode<Camera3D>("Head/Camera3D"), status,
                services.GetRequired<Nikami.Aurora.GodotRuntime.Infrastructure.World.IGodotModelCache>(),
                services.GetRequired<FaceFxRuntime>(),
                services.GetRequired<ICinematicActorModelResolver>(),
                character.Gender, playerModelPath,
                hasOpeningDialogue ? playerBedModelPath : string.Empty,
                hasOpeningDialogue,
                () => loadingPresentation?.Hide(),
                cancellationToken);
            loadingPresentation?.Hide();
        }

        if (hasOpeningDialogue)
        {
            status.Text = string.Empty;
            status.Visible = false;
            openingDialogue = new AuthoredDialogueController
            {
                Name = "OpeningDialogue",
                DialogueId = "bec110cr_shianni"
            };
            AddChild(openingDialogue);
            Action? releaseOpeningPresentation = openingCutscene is null
                ? null
                : openingCutscene.ReleaseRetainedPresentation;
            var retainedHiddenWorldActors = openingCutscene?.TakeRetainedHiddenWorldActors() ??
                                            Array.Empty<Node3D>();
            try
            {
                await openingDialogue.PlayAsync(player.GetNode<Camera3D>("Head/Camera3D"),
                    services.GetRequired<Nikami.Aurora.GodotRuntime.Infrastructure.World.IGodotModelCache>(),
                    services.GetRequired<FaceFxRuntime>(),
                    services.GetRequired<ICinematicActorModelResolver>(),
                    services.GetRequired<StoryState>(),
                    openingCutscene?.FinalCameraTransform ?? player.GetNode<Camera3D>("Head/Camera3D").GlobalTransform,
                    openingCutscene?.FinalCameraFieldOfView ?? 58,
                    openingCutscene?.FinalActorTransforms ?? new Dictionary<string, Transform3D>(),
                    openingCutscene?.FinalActorNodes ?? new Dictionary<string, Node3D>(),
                    openingCutscene?.FinalActorAnimations ??
                    new Dictionary<string, LayeredAnimationState>(),
                    retainedHiddenWorldActors,
                    character.Gender, playerModelPath, playerBedModelPath,
                    releaseOpeningPresentation,
                    cancellationToken);
            }
            finally
            {
                releaseOpeningPresentation?.Invoke();
                foreach (var actor in retainedHiddenWorldActors.Where(IsInstanceValid))
                    actor.Visible = true;
            }
            if (openingCutscene is not null)
                foreach (var actor in openingCutscene.FinalActorNodes.Values
                             .Where(IsInstanceValid).Where(actor => !actor.IsQueuedForDeletion()))
                    actor.QueueFree();
            status.Visible = true;
        }

        player.ResetLocomotionState();
        if (gameplayAvatar is not null) gameplayAvatar.Visible = true;
        hud.Visible = true;
        if (placeableHighlighter is null)
        {
            placeableHighlighter = new PlaceableHighlightController { Name = "PlaceableHighlight" };
            placeableHighlighter.UseRequested += OnPlaceableUseRequested;
            AddChild(placeableHighlighter);
        }
        placeableHighlighter.Attach(player.LocomotionCamera, GetNode<Node3D>("DAOScene"));
        player.SetPhysicsProcess(true);
        status.Text = string.Empty;
        loading = false;
        loadingPresentation?.Hide();
        if (await CaptureAreaRuntimeEvidenceIfRequested(result, cancellationToken)) return;
        if (System.Environment.GetEnvironmentVariable("OPENDAO_CITY_ELF_PLAYABLE_SMOKE") == "1")
        {
            await RunCityElfPlayableSmoke(cancellationToken);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("OPENDAO_CHARACTER_CREATION_ACCEPTANCE") == "1")
        {
            await RunCharacterGameStartAcceptance(cancellationToken);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("OPENDAO_CSHARP_WORLD_SMOKE_EXIT") == "1")
        {
            GD.Print($"OPENDAO_CSHARP_WORLD_SMOKE_PASS area={profile.AreaId} instances={result.Instances} actors={result.Actors}");
            GetTree().Quit(0);
            return;
        }
        if (System.Environment.GetEnvironmentVariable("OPENDAO_LOCOMOTION_TEST") == "1")
        {
            var passed = await LocomotionSmoke.RunAsync(player, cancellationToken);
            GetTree().Quit(passed ? 0 : 58);
            return;
        }
        if (await CaptureCharacterPbrCloseIfRequested(cancellationToken)) return;
        if (await CaptureEffectCloseIfRequested(cancellationToken)) return;
        await CaptureIfRequested(cancellationToken);
    }

    private void OnPlaceableUseRequested(Node3D target)
    {
        if (services is null || loading) return;
        var tag = target.GetMeta("dao_tag").AsString();
        var handle = target.GetMeta("dao_story_handle", 0).AsInt32();
        if (tag.Equals("bec110ip_pc_possessions", StringComparison.OrdinalIgnoreCase))
        {
            var story = services.GetRequired<StoryState>();
            const string plot = "85c3d035f1274fd59849b190d64d5290";
            var alreadyUsed = handle > 0 && Convert.ToInt32(story.GetLocal(handle, "PLC_DO_ONCE_A", "int") ?? 0) != 0;
            if (!alreadyUsed)
            {
                story.SetPlotFlag(plot, 2, true);
                if (handle > 0) story.SetLocal(handle, "PLC_DO_ONCE_A", "int", 1);
            }
            placeableHighlighter?.ShowFeedback(alreadyUsed
                ? "You have already checked your possessions."
                : "Possessions checked — training options unlocked.");
            GD.Print($"OPENDAO_PLACEABLE_USE status=pass tag={tag} handle={handle} event=7 " +
                     $"plot={plot} flag=2 value=1 one_shot={(alreadyUsed ? "repeat" : "committed")}");
            return;
        }
        if (tag.Equals("bec110ip_to_alienage", StringComparison.OrdinalIgnoreCase))
        {
            _ = TravelToAlienage();
            return;
        }
        placeableHighlighter?.ShowFeedback("Nothing happens.");
        GD.Print($"OPENDAO_PLACEABLE_USE status=unsupported tag={tag} handle={handle}");
    }

    private bool TravelToAlienage()
    {
        var catalog = new Nikami.Aurora.GodotRuntime.MainMenu.AreaCatalog();
        if (!catalog.Load())
        {
            placeableHighlighter?.ShowFeedback("The Alienage route is unavailable.");
            GD.PushWarning("OPENDAO_PLACEABLE_TRANSITION status=fail reason=" + catalog.Error);
            return false;
        }
        var destination = catalog.Areas.FirstOrDefault(area => area.Ready &&
            area.Id.Equals("bec100ar_elven_alienage", StringComparison.OrdinalIgnoreCase) &&
            area.Archive.EndsWith("al_bec01al_alienage.rim", StringComparison.OrdinalIgnoreCase));
        var transitionError = destination is null ? "destination-profile-absent" : string.Empty;
        if (destination is null ||
            !Nikami.Aurora.GodotRuntime.MainMenu.AreaCatalog.WriteProfileForLoading(destination,
                Nikami.Aurora.GodotRuntime.MainMenu.RuntimeSavePaths.SelectedProfile, out transitionError))
        {
            placeableHighlighter?.ShowFeedback("The Alienage route is unavailable.");
            GD.PushWarning("OPENDAO_PLACEABLE_TRANSITION status=fail reason=" +
                           transitionError);
            return false;
        }
        var pendingPath = Nikami.Aurora.GodotRuntime.MainMenu.RuntimeSavePaths.PendingTransition;
        var parent = Path.GetDirectoryName(pendingPath);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        var pending = new
        {
            schema = "opendao-pending-transition-v1",
            areaId = destination.Id.ToLowerInvariant(),
            areaKey = destination.Key.ToLowerInvariant(),
            waypointTag = "bec100wp_from_home",
            provenance = new
            {
                source = "authored-placeable-transition",
                sourceArea = profile?.AreaId ?? string.Empty,
                sourceTag = "bec110ip_to_alienage",
                destinationAreaKey = destination.Key
            }
        };
        File.WriteAllText(pendingPath,
            JsonSerializer.Serialize(pending, RuntimeJsonOptions.Indented) +
            System.Environment.NewLine);
        OS.SetEnvironment("OPENDAO_CONTINUE", "0");
        OS.SetEnvironment("OPENDAO_IGNORE_PENDING_TRANSITION", "");
        loading = true;
        GD.Print($"OPENDAO_PLACEABLE_TRANSITION status=ready source=bec110ar_players_house " +
                 $"tag=bec110ip_to_alienage destination={destination.Id} waypoint=bec100wp_from_home");
        var reload = GetTree().ReloadCurrentScene();
        if (reload != Error.Ok)
        {
            loading = false;
            placeableHighlighter?.ShowFeedback("Area transition failed.");
            GD.PushError("OPENDAO_PLACEABLE_TRANSITION status=fail reload=" + reload);
            return false;
        }
        return true;
    }

    private void ConfigureLighting(int authoredLights, AuthoredLightingProfile? lighting,
        string layoutName)
    {
        var renderingMethod = RenderingServer.GetCurrentRenderingMethod().ToString();
        var requestedTier = System.Environment.GetEnvironmentVariable(
            "NIKAMI_AURORA_PRESENTATION_TIER")?.Trim().ToLowerInvariant() ?? string.Empty;
        var renderPolicy = DragonAgeOriginsRenderFidelityPolicy.Evaluate(
            layoutName, requestedTier, renderingMethod, lighting?.Atmosphere is not null);
        if (lighting is null)
        {
            if (GetNodeOrNull<DirectionalLight3D>("Sun") is { } absentSun)
                absentSun.Visible = false;
            if (GetNodeOrNull<DirectionalLight3D>("Fill") is { } absentFill)
                absentFill.Visible = false;
            if (GetNodeOrNull<WorldEnvironment>("Environment")?.Environment is { } absentEnvironment)
            {
                ConfigureRendererEnhancements(absentEnvironment, renderPolicy);
                GetNodeOrNull<FogVolume>("AuthoredCloudVolume")?.QueueFree();
                absentEnvironment.VolumetricFogEnabled = false;
            }
            GD.Print("OPENDAO_AUTHORED_ATMOSPHERE status=unsupported " +
                     $"layout={layoutName.ToLowerInvariant()} " +
                     "reason=validated-atmo-contract-absent source_fields=0");
            PrintRenderPolicy(renderPolicy, volumetricClouds: false,
                atmosphere: "unsupported");
            return;
        }
        var sunCoefficients = lighting?.SunColor ?? Array.Empty<float>();
        var hasSun = HasRgbEnergy(sunCoefficients);
        var sunDirection = lighting?.SunDirection ?? Array.Empty<float>();
        if (GetNodeOrNull<DirectionalLight3D>("Sun") is { } sun)
        {
            sun.Visible = hasSun;
            if (hasSun)
            {
                var encodedSun = DaoLightEncoding.Encode(At(sunCoefficients, 0),
                    At(sunCoefficients, 1), At(sunCoefficients, 2));
                sun.LightColor = encodedSun.Color;
                sun.LightEnergy = Math.Clamp(encodedSun.Energy, 0.0f, 5.0f);
                var sourceToSun = new Vector3(At(sunDirection, 0), At(sunDirection, 1),
                    At(sunDirection, 2));
                if (sourceToSun.LengthSquared() > 0.000001f)
                {
                    sourceToSun = sourceToSun.Normalized();
                    // Haven's proven DAO-to-Godot mesh/light transform. The ARE
                    // vector points toward the sun; Godot's light emits along -Z.
                    var godotToSun = new Vector3(sourceToSun.X, -sourceToSun.Z,
                        sourceToSun.Y).Normalized();
                    var up = Math.Abs(godotToSun.Dot(Vector3.Up)) > 0.98f
                        ? Vector3.Forward
                        : Vector3.Up;
                    sun.LookAt(sun.GlobalPosition - godotToSun, up);
                }
            }
        }
        if (GetNodeOrNull<DirectionalLight3D>("Fill") is { } fill) fill.Visible = false;
        if (GetNodeOrNull<WorldEnvironment>("Environment")?.Environment is { } environment)
        {
            if (lighting!.Atmosphere is not null)
                ConfigureAtmosphere(environment, lighting, renderPolicy);
            else
            {
                ConfigureRendererEnhancements(environment, renderPolicy);
                GetNodeOrNull<FogVolume>("AuthoredCloudVolume")?.QueueFree();
                environment.VolumetricFogEnabled = false;
                GD.Print("OPENDAO_AUTHORED_ATMOSPHERE status=unsupported " +
                         $"layout={layoutName.ToLowerInvariant()} " +
                         "reason=exact-atmo-contract-absent source_fields=8");
                PrintRenderPolicy(renderPolicy, volumetricClouds: false,
                    atmosphere: "unsupported-base-lighting-only");
            }
            environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            // An absent retail probe is an authored absence, not permission to
            // invent neutral fill. No-sun areas retain their authored local
            // light-only input; probe-backed areas use the exact SH path.
            var probeLoaded = lighting?.ProbeLoaded == true;
            var irradiance = probeLoaded && lighting is not null
                ? new Vector3(SphericalAverage(lighting.ProbeMatrixR),
                    SphericalAverage(lighting.ProbeMatrixG),
                    SphericalAverage(lighting.ProbeMatrixB))
                : hasSun
                    ? new Vector3(At(lighting?.FogColor ?? Array.Empty<float>(), 0),
                        At(lighting?.FogColor ?? Array.Empty<float>(), 1),
                        At(lighting?.FogColor ?? Array.Empty<float>(), 2)) * 0.35f
                    : Vector3.Zero;
            irradiance = irradiance.Max(Vector3.Zero);
            var encodedAmbient = DaoLightEncoding.Encode(irradiance.X, irradiance.Y, irradiance.Z);
            environment.AmbientLightColor = encodedAmbient.Color;
            environment.AmbientLightEnergy = encodedAmbient.Energy;
            var characterSun = lighting?.CharacterSunColor ?? Array.Empty<float>();
            var ambientSource = probeLoaded
                ? "retail-sh-spherical-average"
                : hasSun ? "retail-fog-daylight-fill" : "none";
            GD.Print($"OPENDAO_AUTHORED_LIGHTING status=ready local_lights={authoredLights} " +
                     $"probe={(probeLoaded ? 1 : 0)} ambient_source=" +
                     $"{ambientSource} " +
                     $"probe_resource={lighting?.ProbeResource ?? string.Empty} " +
                     $"probe_sha256={lighting?.ProbeResourceSha256 ?? string.Empty} " +
                     $"color_encoding={DaoLightEncoding.Contract} " +
                     $"ambient=({irradiance.X:0.####},{irradiance.Y:0.####},{irradiance.Z:0.####}) " +
                     $"sun=({At(sunCoefficients, 0):0.####},{At(sunCoefficients, 1):0.####}," +
                     $"{At(sunCoefficients, 2):0.####}) sun_enabled={(hasSun ? 1 : 0)} " +
                     $"sun_direction=({At(sunDirection, 0):0.####},{At(sunDirection, 1):0.####}," +
                     $"{At(sunDirection, 2):0.####}) sun_intensity={lighting?.SunIntensity ?? 0:0.####} " +
                     $"character_sun=({At(characterSun, 0):0.####},{At(characterSun, 1):0.####}," +
                     $"{At(characterSun, 2):0.####},{At(characterSun, 3):0.####})");
        }
    }

    private void ConfigureAtmosphere(Godot.Environment environment,
        AuthoredLightingProfile lighting, DragonAgeAreaRenderPolicy renderPolicy)
    {
        var atmosphere = lighting.Atmosphere ??
                         throw new InvalidDataException(
                             "Validated DAO atmosphere is absent during configuration.");
        var renderingMethod = renderPolicy.RenderingMethod;
        var layoutName = renderPolicy.Layout;
        var enhanced = renderPolicy.EnhancedFeatures;
        var shader = GD.Load<Shader>(DaoSkyShaderPath);
        if (shader is null)
        {
            GD.PushError("OPENDAO_AUTHORED_ATMOSPHERE status=fail reason=sky-shader-missing");
            return;
        }

        var sourceToSun = new Vector3(At(lighting.SunDirection, 0),
            At(lighting.SunDirection, 1), At(lighting.SunDirection, 2));
        var godotToSun = sourceToSun.LengthSquared() > 0.000001f
            ? new Vector3(sourceToSun.X, sourceToSun.Z, -sourceToSun.Y).Normalized()
            : new Vector3(0, 0.7f, 0.7f).Normalized();
        var fogColor = SourceColor(lighting.FogColor, new Color(0.16f, 0.19f, 0.16f));
        var cloudColor = SourceColor(atmosphere.CloudColor, new Color(0.8f, 0.8f, 0.8f));
        var atmosphereSun = SourceColor(atmosphere.AtmosphereSunColor,
            new Color(0.74f, 0.32f, 0.14f));
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("fog_color", fogColor);
        material.SetShaderParameter("sun_color", SourceColor(lighting.SunColor, Colors.White));
        material.SetShaderParameter("sun_direction", godotToSun);
        material.SetShaderParameter("atmosphere_sun_color", atmosphereSun);
        material.SetShaderParameter("atmosphere_sun_intensity", Math.Max(0, lighting.SunIntensity));
        material.SetShaderParameter("turbidity", Math.Max(1, atmosphere.Turbidity));
        material.SetShaderParameter("rayleigh_multiplier", Math.Max(0, atmosphere.RayleighMultiplier));
        material.SetShaderParameter("mie_multiplier", Math.Max(0, atmosphere.MieMultiplier));
        material.SetShaderParameter("phase_eccentricity",
            Math.Clamp(atmosphere.PhaseEccentricity, -0.99f, 0.99f));
        material.SetShaderParameter("distance_multiplier", Math.Max(0, atmosphere.DistanceMultiplier));
        material.SetShaderParameter("atmosphere_alpha", Math.Max(0, atmosphere.AtmosphereAlpha));
        material.SetShaderParameter("moon_scale", Math.Max(0, atmosphere.MoonScale));
        material.SetShaderParameter("moon_alpha", Math.Max(0, atmosphere.MoonAlpha));
        material.SetShaderParameter("cloud_color", cloudColor);
        material.SetShaderParameter("authored_cloud_density", Math.Max(0, atmosphere.CloudDensity));
        material.SetShaderParameter("authored_cloud_sharpness", Math.Max(0, atmosphere.CloudSharpness));
        material.SetShaderParameter("authored_cloud_depth", atmosphere.CloudDepth);
        material.SetShaderParameter("cloud_range_multiplier_1", Math.Max(0.0001f, atmosphere.CloudRange1));
        material.SetShaderParameter("cloud_range_multiplier_2", Math.Max(0.0001f, atmosphere.CloudRange2));
        material.SetShaderParameter("fog_cap", Math.Max(0, atmosphere.FogCap));
        material.SetShaderParameter("fog_intensity", Math.Max(0, atmosphere.FogIntensity));
        material.SetShaderParameter("fog_zenith", atmosphere.FogZenith);

        environment.BackgroundMode = Godot.Environment.BGMode.Sky;
        environment.Sky = new Sky { SkyMaterial = material };
        environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
        environment.FogEnabled = atmosphere.FogIntensity > 0;
        environment.FogLightColor = fogColor;
        environment.FogLightEnergy = 1;
        environment.FogDensity = Math.Clamp(atmosphere.FogIntensity * 0.01f, 0, 0.2f);
        environment.FogSkyAffect = Math.Clamp(atmosphere.FogCap, 0, 1);

        // The source composition above remains authoritative. These Forward+
        // facilities improve depth, indirect contact, and cloud volume without
        // changing source placements, colors, or atmosphere coefficients.
        ConfigureRendererEnhancements(environment, renderPolicy);

        var volumetricRequested = enhanced && renderPolicy.QualityDecision.Volumetrics.Enabled &&
                                  renderingMethod.Equals("forward_plus",
                                      StringComparison.OrdinalIgnoreCase) &&
                                  atmosphere.CloudDensity > 0.001f;
        // Matched diagnostic captures exonerated the fog volume: the wedges
        // remained in source tier with volumetrics off and disappeared only
        // when the singular procedural sky-cloud projection was disabled.
        var volumetricClouds = volumetricRequested && ConfigureVolumetricClouds(
            environment, atmosphere, cloudColor, atmosphereSun);
        if (volumetricClouds)
        {
            // The lit fog volume is finite by design and supplies near-field
            // extinction/light shafts.  A redesigned seamless high-frequency
            // shell covers the far horizon without the old singular planar
            // projection or its cubemap-sized cloud columns.
            material.SetShaderParameter("cloud_shell_strength", 0.72f);
        }
        else
        {
            GetNodeOrNull<FogVolume>("AuthoredCloudVolume")?.QueueFree();
            environment.VolumetricFogEnabled = false;
        }
        GD.Print("OPENDAO_AUTHORED_ATMOSPHERE status=ready " +
                 $"background=source-atmo-sky preserved={atmosphere.SourceFieldCount} " +
                 "exact_contract=29 additional_validated=fog_water_intensity,fog_water_cap " +
                 $"preserved_sha256={atmosphere.SourceFieldsSha256} mapped=27 " +
                 "unsupported=fog_water_intensity,fog_water_cap,moon_rotation,skydome " +
                 $"cloud_density={atmosphere.CloudDensity:0.####} " +
                 $"fog_intensity={atmosphere.FogIntensity:0.####} " +
                 $"sun_intensity={lighting.SunIntensity:0.####} skydome={atmosphere.SkyDome} " +
                 $"cloud_composition={(volumetricClouds ? "lit-volume+far-shell" : "source-sky-shell")} " +
                 $"gray_clear_holes=blocked layout={layoutName.ToLowerInvariant()}");
        PrintRenderPolicy(renderPolicy, volumetricClouds, "source-validated");
    }

    private static void ConfigureRendererEnhancements(Godot.Environment environment,
        DragonAgeAreaRenderPolicy policy)
    {
        var enhanced = policy.EnhancedFeatures;
        environment.TonemapMode = enhanced
            ? Godot.Environment.ToneMapper.Agx
            : Godot.Environment.ToneMapper.Linear;
        environment.TonemapExposure = 1;
        environment.TonemapAgxWhite = 8;
        environment.TonemapAgxContrast = 1.05f;
        environment.SsaoEnabled = enhanced;
        environment.SsaoRadius = 1.4f;
        environment.SsaoIntensity = 1.2f;
        environment.SsilEnabled = enhanced;
        environment.SsilRadius = 3;
        environment.SsilIntensity = 0.65f;
        environment.GlowEnabled = enhanced;
        environment.SsrEnabled = policy.QualityDecision.Reflections.Enabled;
        environment.SdfgiEnabled = policy.QualityDecision.Sdfgi.Enabled;
    }

    private static void PrintRenderPolicy(DragonAgeAreaRenderPolicy policy,
        bool volumetricClouds, string atmosphere)
    {
        var enhanced = policy.EnhancedFeatures;
        var tier = policy.Tier.ToString().ToLowerInvariant();
        GD.Print(policy.QualityDecision.ToTelemetryMarker());
        GD.Print("OPENDAO_RENDER_PIPELINE status=ready " +
                 $"method={policy.RenderingMethod} tier={tier} " +
                 $"tonemap={(enhanced ? "agx" : "linear")} ssao={(enhanced ? 1 : 0)} " +
                 $"ssil={(enhanced ? 1 : 0)} glow={(enhanced ? 1 : 0)} " +
                 $"volumetric_clouds={(volumetricClouds ? 1 : 0)} " +
                 $"layout={policy.Layout.ToLowerInvariant()} atmosphere={atmosphere} " +
                 $"ssr={(policy.QualityDecision.Reflections.Enabled ? 1 : 0)} " +
                 $"sdfgi={(policy.QualityDecision.Sdfgi.Enabled ? 1 : 0)}");
        GD.Print($"OPENDAO_RENDER_ENHANCEMENT status={(enhanced ? "ready" : "disabled")} " +
                 $"renderer={policy.RenderingMethod} tier={tier} " +
                 $"tonemapper={(enhanced ? "agx" : "linear")} ssao={(enhanced ? 1 : 0)} " +
                 $"ssil={(enhanced ? 1 : 0)} volumetric_clouds={(volumetricClouds ? 1 : 0)} " +
                 $"parity_claim=none layout={policy.Layout.ToLowerInvariant()} " +
                 $"atmosphere={atmosphere}");
        GD.Print($"OPENDAO_AREA_RENDER_POLICY status={policy.Status} " +
                 $"layout={policy.Layout.ToLowerInvariant()} tier={tier} " +
                 $"renderer={policy.RenderingMethod} atmosphere={atmosphere} " +
                 "material_policy=gltf-pbr-source-contract " +
                 $"enhanced_features={(enhanced ? 1 : 0)} " +
                 "layout_override=none parity_claim=none");
    }

    private bool ConfigureVolumetricClouds(Godot.Environment environment,
        AuthoredAtmosphereProfile atmosphere, Color cloudColor, Color atmosphereSun)
    {
        var shader = GD.Load<Shader>(DaoCloudVolumeShaderPath);
        if (shader is null)
        {
            GD.PushError("OPENDAO_VOLUMETRIC_CLOUDS status=fail reason=shader-missing");
            return false;
        }

        var sceneBounds = SceneBounds.Calculate(GetNode<Node3D>("DAOScene"));
        var sceneTop = sceneBounds.Size.IsZeroApprox()
            ? player.GlobalPosition.Y + 20
            : sceneBounds.End.Y;
        var sceneHeight = sceneBounds.Size.IsZeroApprox() ? 20 : Math.Max(20, sceneBounds.Size.Y);
        var cloudBase = sceneTop + Math.Max(18, sceneHeight * 0.25f);
        var cloudThickness = Math.Max(55, sceneHeight * 1.25f);
        var cloudMaterial = new ShaderMaterial { Shader = shader };
        cloudMaterial.SetShaderParameter("density_scale",
            Math.Clamp(atmosphere.CloudDensity * 0.22f, 0.015f, 0.18f));
        cloudMaterial.SetShaderParameter("cloud_albedo", cloudColor);
        cloudMaterial.SetShaderParameter("cloud_ambient", atmosphereSun * 0.035f);
        cloudMaterial.SetShaderParameter("cloud_base_height", cloudBase);
        cloudMaterial.SetShaderParameter("cloud_layer_thickness", cloudThickness);
        cloudMaterial.SetShaderParameter("authored_cloud_sharpness",
            Math.Max(.0001f, atmosphere.CloudSharpness));
        cloudMaterial.SetShaderParameter("authored_cloud_depth", atmosphere.CloudDepth);
        cloudMaterial.SetShaderParameter("cloud_range_1",
            Math.Max(.0001f, atmosphere.CloudRange1));
        cloudMaterial.SetShaderParameter("cloud_range_2",
            Math.Max(.0001f, atmosphere.CloudRange2));

        GetNodeOrNull<FogVolume>("AuthoredCloudVolume")?.QueueFree();
        AddChild(new FogVolume
        {
            Name = "AuthoredCloudVolume",
            Shape = RenderingServer.FogVolumeShape.World,
            Material = cloudMaterial
        });
        environment.VolumetricFogEnabled = true;
        environment.VolumetricFogDensity = 0;
        environment.VolumetricFogAlbedo = cloudColor;
        environment.VolumetricFogAnisotropy = Math.Clamp(atmosphere.PhaseEccentricity, -0.8f, 0.8f);
        environment.VolumetricFogLength = Math.Max(220, cloudBase + cloudThickness);
        environment.VolumetricFogAmbientInject = 0.65f;
        environment.VolumetricFogSkyAffect = 1;
        environment.VolumetricFogTemporalReprojectionEnabled = true;
        environment.VolumetricFogTemporalReprojectionAmount = 0.82f;
        if (GetNodeOrNull<DirectionalLight3D>("Sun") is { } sun)
            sun.LightVolumetricFogEnergy = 1.15f;
        GD.Print($"OPENDAO_VOLUMETRIC_CLOUDS status=ready source=are-atmo " +
                 $"base={cloudBase:0.###} thickness={cloudThickness:0.###} " +
                 $"density={atmosphere.CloudDensity:0.###} " +
                 $"sharpness={atmosphere.CloudSharpness:0.###} " +
                 $"depth={atmosphere.CloudDepth:0.###} " +
                 $"ranges={atmosphere.CloudRange1:0.###},{atmosphere.CloudRange2:0.###} " +
                 "wind=unsupported-static enhancement=2026-quality parity_claim=none");
        return true;
    }

    private static Color SourceColor(IReadOnlyList<float> source, Color fallback) =>
        source.Count >= 3
            ? new Color(Math.Max(0, source[0]), Math.Max(0, source[1]), Math.Max(0, source[2]))
            : fallback;

    private static float At(IReadOnlyList<float> values, int index) =>
        index < values.Count ? values[index] : 0.0f;

    private static bool HasRgbEnergy(IReadOnlyList<float> values) =>
        At(values, 0) > 0.0001f || At(values, 1) > 0.0001f || At(values, 2) > 0.0001f;

    private static float SphericalAverage(IReadOnlyList<float> matrix) => matrix.Count >= 16
        ? matrix[15] + (matrix[0] + matrix[5] + matrix[10]) / 3.0f
        : 0.0f;

    private static bool ShouldPlayOpening(WorldProfile world,
        Nikami.Aurora.GodotRuntime.MainMenu.CharacterOrigin? origin) =>
        origin is { OpeningCutscene.Length: > 0 } &&
        world.AreaId.Equals(origin.AreaId, StringComparison.OrdinalIgnoreCase) &&
        System.Environment.GetEnvironmentVariable("OPENDAO_CONTINUE") != "1" &&
        (!RuntimeAutomation.WantsWorld() ||
         System.Environment.GetEnvironmentVariable("OPENDAO_CHARACTER_CREATION_ACCEPTANCE") == "1");

    private static bool ShouldPlayOpeningDialogue(WorldProfile world,
        Nikami.Aurora.GodotRuntime.MainMenu.CharacterOrigin? origin) =>
        origin is not null &&
        origin.Id.Equals("city-elf", StringComparison.OrdinalIgnoreCase) &&
        world.AreaId.Equals("bec110ar_players_house", StringComparison.OrdinalIgnoreCase) &&
        System.Environment.GetEnvironmentVariable("OPENDAO_CONTINUE") != "1" &&
        (!RuntimeAutomation.WantsWorld() ||
         System.Environment.GetEnvironmentVariable("OPENDAO_CHARACTER_CREATION_ACCEPTANCE") == "1");

}
