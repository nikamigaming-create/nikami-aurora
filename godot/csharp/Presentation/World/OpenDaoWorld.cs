using Godot;
using System.Text.Json;
using OpenDAO.Application.Abstractions;
using OpenDAO.Bootstrap;
using OpenDAO.Domain.Abilities;
using OpenDAO.Domain.Characters;
using OpenDAO.Domain.Inventory;
using OpenDAO.Domain.Quests;
using OpenDAO.Domain.Sessions;
using OpenDAO.Domain.Story;
using OpenDAO.Domain.World;
using OpenDAO.Infrastructure.World;
using OpenDAO.Infrastructure.Configuration;
using OpenDAO.Launcher;
using OpenDAO.Presentation.Cinematics;
using OpenDAO.Presentation.Player;
using OpenDAO.Application.Characters;

namespace OpenDAO.Presentation.World;

public partial class OpenDaoWorld : Node3D
{
    private const string PlayableSmokeStartupFramesVariable =
        "OPENDAO_ACCEPTANCE_PLAYABLE_STARTUP_FRAMES";
    private const string AlienageArrivalWarmupFramesVariable =
        "OPENDAO_ACCEPTANCE_ALIENAGE_WARMUP_FRAMES";
    private const string GameplayHoldFramesVariable = "OPENDAO_ACCEPTANCE_GAMEPLAY_HOLD_FRAMES";

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
                services.GetRequired<OpenDAO.Infrastructure.World.IGodotModelPostprocessor>());
            GD.Print($"OPENDAO_CHARACTER name={character.DisplayName} race={character.Race} " +
                     $"class={character.Class} origin={character.Origin} appearance={character.Appearance}");
            await LoadWorld(lifetime.Token);
        }
        catch (Exception error)
        {
            loadingPresentation?.Hide();
            GD.PushError("OpenDAO startup failed: " + error);
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
        var arrival = services.GetRequired<IWorldArrivalResolver>().Resolve(profile);
        if (arrival is not null)
            player.GlobalTransform = arrival.Transform;
        else
            RestoreSession();
        if (!player.SnapToWalkableGround(player.GlobalPosition, $"world-load:{profile.AreaId}", false))
            GD.PushWarning($"Player spawn has no walkable surface in {profile.AreaId}: {player.GlobalPosition}");
        player.ConfigureThirdPersonView(presentationConfiguration.GameplayCamera);
        player.SetAuthoredNavigation(result.Navigation);
        ConfigureLighting(result.AuthoredLights, result.Lighting);
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
        var origin = OpenDAO.MainMenu.CharacterProfileRules.OriginFor(character.Origin);
        var hasOpeningDialogue = ShouldPlayOpeningDialogue(profile, origin);
        if (ShouldPlayOpening(profile, origin))
        {
            openingCutscene = new OpeningCutsceneController
            {
                Name = "OpeningCutscene",
                CutsceneId = origin!.OpeningCutscene
            };
            AddChild(openingCutscene);
            await openingCutscene.PlayAsync(player.GetNode<Camera3D>("Head/Camera3D"), status,
                services.GetRequired<OpenDAO.Infrastructure.World.IGodotModelCache>(),
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
                    services.GetRequired<OpenDAO.Infrastructure.World.IGodotModelCache>(),
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
        var catalog = new OpenDAO.MainMenu.AreaCatalog();
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
            !OpenDAO.MainMenu.AreaCatalog.WriteProfileForLoading(destination,
                OpenDAO.MainMenu.RuntimeSavePaths.SelectedProfile, out transitionError))
        {
            placeableHighlighter?.ShowFeedback("The Alienage route is unavailable.");
            GD.PushWarning("OPENDAO_PLACEABLE_TRANSITION status=fail reason=" +
                           transitionError);
            return false;
        }
        var pendingPath = OpenDAO.MainMenu.RuntimeSavePaths.PendingTransition;
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
            JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }) +
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

    private void ConfigureLighting(int authoredLights, AuthoredLightingProfile? lighting)
    {
        if (authoredLights <= 0) return;
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
            environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            // An absent retail probe is an authored absence, not permission to
            // invent neutral fill. The city-elf room has zero sun/character sun
            // and seven local lights, so those lights are its complete diffuse
            // illumination input. Probe-backed areas are kept distinct for the
            // exact SH binding path.
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

    private static float At(IReadOnlyList<float> values, int index) =>
        index < values.Count ? values[index] : 0.0f;

    private static bool HasRgbEnergy(IReadOnlyList<float> values) =>
        At(values, 0) > 0.0001f || At(values, 1) > 0.0001f || At(values, 2) > 0.0001f;

    private static float SphericalAverage(IReadOnlyList<float> matrix) => matrix.Count >= 16
        ? matrix[15] + (matrix[0] + matrix[5] + matrix[10]) / 3.0f
        : 0.0f;

    private static bool ShouldPlayOpening(WorldProfile world,
        OpenDAO.MainMenu.CharacterOrigin? origin) =>
        origin is { OpeningCutscene.Length: > 0 } &&
        world.AreaId.Equals(origin.AreaId, StringComparison.OrdinalIgnoreCase) &&
        System.Environment.GetEnvironmentVariable("OPENDAO_CONTINUE") != "1" &&
        (!RuntimeAutomation.WantsWorld() ||
         System.Environment.GetEnvironmentVariable("OPENDAO_CHARACTER_CREATION_ACCEPTANCE") == "1");

    private static bool ShouldPlayOpeningDialogue(WorldProfile world,
        OpenDAO.MainMenu.CharacterOrigin? origin) =>
        origin is not null &&
        origin.Id.Equals("city-elf", StringComparison.OrdinalIgnoreCase) &&
        world.AreaId.Equals("bec110ar_players_house", StringComparison.OrdinalIgnoreCase) &&
        System.Environment.GetEnvironmentVariable("OPENDAO_CONTINUE") != "1" &&
        (!RuntimeAutomation.WantsWorld() ||
         System.Environment.GetEnvironmentVariable("OPENDAO_CHARACTER_CREATION_ACCEPTANCE") == "1");

    private async Task RunCityElfPlayableSmoke(CancellationToken cancellationToken)
    {
        await WaitForProcessFrames(
            ConfiguredFrameCount(PlayableSmokeStartupFramesVariable), cancellationToken);
        var stage = System.Environment.GetEnvironmentVariable("OPENDAO_CITY_ELF_PLAYABLE_SMOKE_STAGE") ??
                    string.Empty;
        if (profile?.AreaId.Equals("bec110ar_players_house", StringComparison.OrdinalIgnoreCase) == true &&
            stage.Length == 0)
        {
            var capturePath = System.Environment.GetEnvironmentVariable("OPENDAO_GAME_START_CAPTURE") ?? string.Empty;
            var capture = capturePath.Length == 0
                ? Error.Ok
                : GetViewport().GetTexture().GetImage().SavePng(capturePath);
            var locomotionPassed = await LocomotionSmoke.RunAsync(player, cancellationToken);
            var crate = FindPlaceable("bec110ip_pc_possessions");
            var door = FindPlaceable("bec110ip_to_alienage");
            if (crate is not null) OnPlaceableUseRequested(crate);
            var story = services!.GetRequired<StoryState>();
            var crateHandle = crate?.GetMeta("dao_story_handle", 0).AsInt32() ?? 0;
            var crateUsePassed = crate is not null && crateHandle > 0 &&
                                 story.GetPlotFlag("85c3d035f1274fd59849b190d64d5290", 2) &&
                                 Convert.ToInt32(story.GetLocal(crateHandle, "PLC_DO_ONCE_A", "int") ?? 0) == 1;
            var gameStartPassed = capture == Error.Ok && player.IsPhysicsProcessing() && locomotionPassed &&
                                  crateUsePassed && door is not null;
            GD.Print($"OPENDAO_CHARACTER_GAME_START_ACCEPTANCE status={(gameStartPassed ? "pass" : "fail")} " +
                     $"character={character.Name} area={profile.AreaId} " +
                     $"player_control={(player.IsPhysicsProcessing() ? 1 : 0)} " +
                     $"opening_cutscene=start_wake locomotion={(locomotionPassed ? "pass" : "fail")} " +
                     $"capture={capturePath}");
            if (!gameStartPassed)
            {
                GD.Print($"OPENDAO_CITY_ELF_PLAYABLE_SMOKE status=fail stage=house " +
                         $"crate={(crate is null ? 0 : 1)} door={(door is null ? 0 : 1)} " +
                         $"crate_use={(crateUsePassed ? "pass" : "fail")} " +
                         $"locomotion={(locomotionPassed ? "pass" : "fail")}");
                GetTree().Quit(61);
                return;
            }
            System.Environment.SetEnvironmentVariable("OPENDAO_CITY_ELF_PLAYABLE_SMOKE_STAGE", "crate-used");
            if (!TravelToAlienage())
            {
                GD.Print("OPENDAO_CITY_ELF_PLAYABLE_SMOKE status=fail stage=transition");
                GetTree().Quit(62);
            }
            return;
        }
        if (profile?.AreaId.Equals("bec100ar_elven_alienage", StringComparison.OrdinalIgnoreCase) == true &&
            stage == "crate-used")
        {
            await WaitForProcessFrames(
                ConfiguredFrameCount(AlienageArrivalWarmupFramesVariable), cancellationToken);
            var locomotionPassed = await LocomotionSmoke.RunAsync(player, cancellationToken);
            await WaitForProcessFrames(ConfiguredFrameCount(GameplayHoldFramesVariable), cancellationToken);
            var destinationCapture = System.Environment.GetEnvironmentVariable(
                "OPENDAO_PLAYABLE_DESTINATION_CAPTURE") ?? string.Empty;
            var destinationImage = GetViewport().GetTexture().GetImage();
            var captured = destinationCapture.Length == 0
                ? Error.Ok
                : destinationImage.SavePng(destinationCapture);
            var visibility = MeasureWorldVisibility(destinationImage);
            var visibilityPassed = visibility.VisibleRatio >= 0.15f;
            var passed = player.IsPhysicsProcessing() && locomotionPassed &&
                         captured == Error.Ok && visibilityPassed;
            GD.Print($"OPENDAO_CITY_ELF_EXTERIOR_GAMEPLAY status={(passed ? "pass" : "fail")} " +
                     $"area={profile.AreaId} waypoint=bec100wp_from_home " +
                     $"locomotion={(locomotionPassed ? "pass" : "fail")} " +
                     $"player_control={(player.IsPhysicsProcessing() ? 1 : 0)} " +
                     $"world_visible={(visibilityPassed ? "pass" : "fail")}");
            GD.Print($"OPENDAO_CITY_ELF_PLAYABLE_SMOKE status={(passed ? "pass" : "fail")} " +
                     "crate_use=pass transition=pass " +
                     $"destination={profile.AreaId} waypoint=bec100wp_from_home " +
                     $"locomotion={(locomotionPassed ? "pass" : "fail")} " +
                     $"player_control={(player.IsPhysicsProcessing() ? 1 : 0)} " +
                     $"world_visible={(visibilityPassed ? "pass" : "fail")} " +
                     $"visible_ratio={visibility.VisibleRatio:0.####} " +
                     $"mean_luminance={visibility.MeanLuminance:0.####} capture={destinationCapture}");
            System.Environment.SetEnvironmentVariable("OPENDAO_CITY_ELF_PLAYABLE_SMOKE_STAGE", string.Empty);
            GetTree().Quit(passed ? 0 : 63);
            return;
        }
        GD.Print($"OPENDAO_CITY_ELF_PLAYABLE_SMOKE status=fail stage=unexpected " +
                 $"area={profile?.AreaId ?? string.Empty} marker={stage}");
        GetTree().Quit(64);
    }

    private async Task WaitForProcessFrames(int frameCount, CancellationToken cancellationToken)
    {
        for (var frame = 0; frame < frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static int ConfiguredFrameCount(string variableName) => int.TryParse(
        System.Environment.GetEnvironmentVariable(variableName), out var configuredFrames)
        ? Math.Max(0, configuredFrames)
        : 0;

    private static (float VisibleRatio, float MeanLuminance) MeasureWorldVisibility(Image image)
    {
        if (image.IsEmpty() || image.GetWidth() < 4 || image.GetHeight() < 4) return (0, 0);
        const int columnsPerBand = 32;
        const int rows = 36;
        var width = image.GetWidth();
        var height = image.GetHeight();
        var visible = 0;
        var samples = 0;
        var luminanceSum = 0.0f;
        for (var row = 0; row < rows; row++)
        {
            var y = (int)Math.Round(height * (0.18f + 0.60f * row / (rows - 1)));
            for (var band = 0; band < 2; band++)
            {
                var xStart = band == 0 ? 0.05f : 0.62f;
                var xEnd = band == 0 ? 0.38f : 0.95f;
                for (var column = 0; column < columnsPerBand; column++)
                {
                    var x = (int)Math.Round(width *
                        (xStart + (xEnd - xStart) * column / (columnsPerBand - 1)));
                    var color = image.GetPixel(Math.Clamp(x, 0, width - 1),
                        Math.Clamp(y, 0, height - 1));
                    var luminance = 0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B;
                    luminanceSum += luminance;
                    if (luminance >= 0.03f) visible++;
                    samples++;
                }
            }
        }
        return samples == 0 ? (0, 0) : ((float)visible / samples, luminanceSum / samples);
    }

    private Node3D? FindPlaceable(string tag) => GetNode<Node3D>("DAOScene")
        .FindChildren("*", "Node3D", true, false).OfType<Node3D>()
        .FirstOrDefault(node => node.HasMeta("dao_placeable") &&
                                node.GetMeta("dao_tag").AsString()
                                    .Equals(tag, StringComparison.OrdinalIgnoreCase));

    private async Task RunCharacterGameStartAcceptance(CancellationToken cancellationToken)
    {
        for (var frame = 0; frame < 12; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        var expectedOrigin = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_ORIGIN");
        if (string.IsNullOrWhiteSpace(expectedOrigin)) expectedOrigin = "city-elf";
        var expectedGender = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_GENDER");
        if (expectedGender is not ("male" or "female")) expectedGender = "female";
        var expectedName = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_NAME");
        if (string.IsNullOrWhiteSpace(expectedName)) expectedName = "Automation Warden";
        var expectedClass = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_CLASS");
        if (string.IsNullOrWhiteSpace(expectedClass)) expectedClass = character.Class;
        var expectedAppearance = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_APPEARANCE");
        if (string.IsNullOrWhiteSpace(expectedAppearance)) expectedAppearance = "preset-3";
        var expected = character.Name == expectedName && character.Gender == expectedGender &&
                       character.Origin.Equals(expectedOrigin, StringComparison.OrdinalIgnoreCase) &&
                       character.Class.Equals(expectedClass, StringComparison.OrdinalIgnoreCase) &&
                       character.Appearance.Equals(expectedAppearance, StringComparison.OrdinalIgnoreCase);
        var origin = OpenDAO.MainMenu.CharacterProfileRules.OriginFor(character.Origin);
        var correctArea = origin is not null && profile is not null &&
                          profile.AreaId.Equals(origin.AreaId, StringComparison.OrdinalIgnoreCase);
        var correctCutscenePolicy = origin is not null &&
            (origin.OpeningCutscene.Length == 0
                ? openingCutscene is null
                : openingCutscene?.CompletedSuccessfully == true);
        var gameplayCamera = player.GetNode<Camera3D>("Head/Camera3D");
        GD.Print($"OPENDAO_GAMEPLAY_CAMERA player={player.GlobalPosition} camera={gameplayCamera.GlobalPosition} " +
                 $"forward={-gameplayCamera.GlobalBasis.Z} spring={player.GetNode<SpringArm3D>("Head").SpringLength:F2}");
        var capturePath = System.Environment.GetEnvironmentVariable("OPENDAO_GAME_START_CAPTURE") ?? string.Empty;
        var capture = capturePath.Length == 0
            ? Error.Ok
            : GetViewport().GetTexture().GetImage().SavePng(capturePath);
        var locomotionPassed = System.Environment.GetEnvironmentVariable("OPENDAO_FLOW_LOCOMOTION") != "1" ||
                               await LocomotionSmoke.RunAsync(player, cancellationToken);
        var passed = expected && correctArea && correctCutscenePolicy && capture == Error.Ok &&
                     player.IsPhysicsProcessing() && locomotionPassed;
        GD.Print($"OPENDAO_CHARACTER_GAME_START_ACCEPTANCE status={(passed ? "pass" : "fail")} " +
                 $"character={character.Name} area={profile?.AreaId} player_control={(player.IsPhysicsProcessing() ? 1 : 0)} " +
                 $"opening_cutscene={(origin?.OpeningCutscene.Length > 0 ? origin.OpeningCutscene : "not-authored-for-area")} " +
                 $"locomotion={(locomotionPassed ? "pass" : "fail")} capture={capturePath}");
        await WaitForProcessFrames(ConfiguredFrameCount(GameplayHoldFramesVariable), cancellationToken);
        GetTree().Quit(passed ? 0 : 59);
    }

    private void RestoreSession()
    {
        if (services is null || profile is null ||
            System.Environment.GetEnvironmentVariable("OPENDAO_CONTINUE") != "1") return;
        var session = services.GetRequired<IPlayerSessionRepository>().Load();
        if (session is null || (session.AreaId.Length > 0 && !string.Equals(session.AreaId, profile.AreaId,
            StringComparison.OrdinalIgnoreCase))) return;
        player.GlobalPosition = session.Position;
        player.Rotation = player.Rotation with { Y = session.Yaw };
        var head = player.GetNode<Node3D>("Head");
        head.Rotation = head.Rotation with { X = Mathf.Clamp(session.Pitch, Mathf.DegToRad(-85), Mathf.DegToRad(85)) };
        GD.Print($"OPENDAO_SESSION restored area={session.AreaId} position={session.Position}");
    }

    private void SaveSession()
    {
        if (services is null || profile is null || !IsInstanceValid(player) ||
            System.Environment.GetEnvironmentVariable("OPENDAO_TEST_NO_PERSIST") == "1") return;
        var head = player.GetNodeOrNull<Node3D>("Head");
        services.GetRequired<IPlayerSessionRepository>().Save(new PlayerSession(string.Empty,
            profile.AreaId, player.GlobalPosition, player.Rotation.Y, head?.Rotation.X ?? 0,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    private async Task CaptureIfRequested(CancellationToken cancellationToken)
    {
        var path = System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE")?.Trim() ?? string.Empty;
        if (path.Length == 0) return;
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            GD.PushError("OPENDAO_CAPTURE status=fail reason=headless-display-server");
            if (System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE_EXIT") == "1") GetTree().Quit(1);
            return;
        }
        for (var i = 0; i < 24; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        var image = GetViewport().GetTexture().GetImage();
        var error = image.SavePng(path);
        GD.Print($"OPENDAO_CAPTURE path={path} status={(error == Error.Ok ? "pass" : "fail")}");
        if (System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE_EXIT") == "1")
            GetTree().Quit(error == Error.Ok ? 0 : 1);
    }
}
