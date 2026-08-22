using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class KotorModuleBoot : Node3D
{
    private static readonly Shader OdysseyLightmapShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode unshaded;
            uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic;
            uniform sampler2D lightmap_texture : source_color, filter_linear_mipmap_anisotropic;
            uniform float lightmap_gain = 2.0;
            void fragment() {
                vec4 base = texture(albedo_texture, UV);
                vec3 lightmap = texture(lightmap_texture, UV2).rgb;
                ALBEDO = base.rgb * lightmap * lightmap_gain;
                ALPHA = base.a;
                if (ALPHA < 0.05) discard;
            }
            """
    };
    private CharacterBody3D playerBody = null!;
    private Camera3D camera = null!;
    private Label status = null!;
    private Label details = null!;
    private ColorRect loadingBackdrop = null!;
    private PanelContainer dialoguePanel = null!;
    private Label dialogueSpeaker = null!;
    private Label dialogueText = null!;
    private VBoxContainer dialogueChoices = null!;
    private Label interactionPrompt = null!;
    private readonly List<Button> activeChoiceButtons = [];
    private readonly List<NavigationTriangle> navigationTriangles = [];
    private readonly List<InteractiveDoor> interactiveDoors = [];
    private int capturedFrames;
    private int readyFrames;
    private bool moduleReady;
    private bool automatedChoiceApplied;
    private bool automatedMoveApplied;
    private bool automatedDoorApplied;
    private string lastDialogueSpeaker = "TRASK ULGO";
    private float yaw;
    private float pitch;

    public override void _Ready()
    {
        CreateEnvironment();
        CreateCamera();
        CreateOverlay();
        Input.MouseMode = Input.MouseModeEnum.Captured;

        var manifestPath = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_MODULE_MANIFEST");
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            manifestPath = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"),
                "..", "local", "kotor", "end_m01aa", "module-manifest.json"));
        }
        Callable.From(() => LoadModuleAsync(manifestPath)).CallDeferred();
    }

    public override void _Process(double delta)
    {
        var basis = new Basis(Vector3.Up, yaw);
        var movement = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) movement -= basis.Z;
        if (Input.IsKeyPressed(Key.S)) movement += basis.Z;
        if (Input.IsKeyPressed(Key.A)) movement -= basis.X;
        if (Input.IsKeyPressed(Key.D)) movement += basis.X;
        if (movement.LengthSquared() > 0.001f)
        {
            var speed = Input.IsKeyPressed(Key.Shift) ? 12.0f : 5.0f;
            MovePlayer(movement.Normalized() * speed * (float)delta);
        }
        UpdateInteractionPrompt();

        if (moduleReady)
        {
            readyFrames++;
            if (!automatedDoorApplied && readyFrames >= 10 &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_OPEN_DOOR") == "1" &&
                interactiveDoors.Count > 0)
            {
                automatedDoorApplied = true;
                ToggleDoor(interactiveDoors[0]);
            }
            var configuredChoice = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_DIALOGUE_CHOICE");
            if (!automatedChoiceApplied && readyFrames >= 20 &&
                int.TryParse(configuredChoice, out var choice) &&
                choice >= 0 && choice < activeChoiceButtons.Count)
            {
                automatedChoiceApplied = true;
                activeChoiceButtons[choice].EmitSignal(BaseButton.SignalName.Pressed);
                GD.Print($"NIKAMI_AURORA_DIALOGUE_CHOICE status=selected index={choice}");
            }
            var configuredMove = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_MOVE_METERS");
            if (!automatedMoveApplied && readyFrames >= 30 &&
                double.TryParse(configuredMove, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var meters) && Math.Abs(meters) > 0.001)
            {
                automatedMoveApplied = true;
                var start = playerBody.GlobalPosition;
                var accepted = MovePlayer(-basis.Z * (float)meters);
                GD.Print($"NIKAMI_AURORA_NAV_TEST status={(accepted ? "accepted" : "rejected")} " +
                         $"from={start} to={playerBody.GlobalPosition} requested={meters:F3}");
            }
        }

        var capturePath = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE");
        if (moduleReady && !string.IsNullOrWhiteSpace(capturePath) && ++capturedFrames == 60)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath))!);
            var error = GetViewport().GetTexture().GetImage().SavePng(capturePath);
            GD.Print($"NIKAMI_AURORA_CAPTURE status={error} path={capturePath}");
            if (System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_EXIT") == "1")
                GetTree().Quit(error == Error.Ok ? 0 : 1);
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            yaw -= motion.Relative.X * 0.0025f;
            pitch = Mathf.Clamp(pitch - motion.Relative.Y * 0.0025f, -1.45f, 1.45f);
            playerBody.Rotation = new Vector3(0, yaw, 0);
            camera.Rotation = new Vector3(pitch, 0, 0);
        }
        else if (inputEvent is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                ? Input.MouseModeEnum.Visible
                : Input.MouseModeEnum.Captured;
        }
        else if (inputEvent is InputEventKey interact && interact.Pressed && interact.Keycode == Key.E)
        {
            var door = NearestDoor(2.6f);
            if (door is not null && !dialoguePanel.Visible)
                ToggleDoor(door);
        }
    }

    private async void LoadModuleAsync(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("KOTOR module manifest is missing", manifestPath);
            var manifest = JsonSerializer.Deserialize<ModuleManifest>(
                await File.ReadAllTextAsync(manifestPath), JsonOptions())
                ?? throw new InvalidDataException("KOTOR module manifest is empty");
            if (manifest.Schema != "nikami-aurora-kotor-module-v1")
                throw new InvalidDataException($"Unsupported module manifest schema: {manifest.Schema}");

            status.Text = $"LOADING {manifest.Module.ToUpperInvariant()}";
            details.Text = "Resolving owned Odyssey geometry...";
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            var loadedRooms = 0;
            foreach (var room in manifest.Rooms)
            {
                if (string.IsNullOrWhiteSpace(room.Glb)) continue;
                var glbPath = Path.GetFullPath(Path.Combine(manifestDirectory,
                    room.Glb.Replace('/', Path.DirectorySeparatorChar)));
                var document = new GltfDocument();
                var state = new GltfState();
                if (document.AppendFromFile(glbPath, state) != Error.Ok ||
                    document.GenerateScene(state) is not Node3D imported)
                    throw new InvalidDataException($"Godot could not import room {room.Model}: {glbPath}");
                imported.Name = room.Model;
                imported.Position = ToGodot(room.Position);
                MakeDiffuseProofReadable(imported);
                AddChild(imported);
                loadedRooms++;
                details.Text = $"Rooms {loadedRooms}/{manifest.Rooms.Count}  •  {room.Model}";
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            var materializedActors = LoadActorModels(manifest.Creatures, manifestDirectory);
            AddCreatureMarkers(manifest.Creatures);
            var materializedDoors = LoadDoorModels(manifest.Doors, manifestDirectory);
            BuildNavigation(manifest.Rooms);
            var entry = ToGodot(manifest.Entry.Position);
            if (!TryProjectToWalkmesh(entry, out var entryGround))
                throw new InvalidDataException($"Authored entry point is not on the imported walkmesh: {entry}");
            entry.Y = entryGround;
            var trask = manifest.Creatures.FirstOrDefault(creature =>
                creature.Template.Equals("end_trask", StringComparison.OrdinalIgnoreCase));
            playerBody.GlobalPosition = entry;
            if (trask is not null)
            {
                var target = ToGodot(trask.Position) + Vector3.Up * 1.2f;
                var direction = (target - camera.GlobalPosition).Normalized();
                yaw = Mathf.Atan2(-direction.X, -direction.Z);
                pitch = -Mathf.Asin(direction.Y);
                playerBody.Rotation = new Vector3(0, yaw, 0);
                camera.Rotation = new Vector3(pitch, 0, 0);
            }

            loadingBackdrop.Visible = false;
            status.Text = $"{manifest.Module.ToUpperInvariant()}  •  ENDAR SPIRE";
            details.Text = $"{manifest.Rooms.Count} authored / {loadedRooms} visual rooms  •  " +
                           $"{materializedActors} actor / {manifest.Counts.Creatures} creature placements  •  " +
                           $"{manifest.Counts.WalkmeshTriangles} nav triangles  •  " +
                           $"{materializedDoors} door / {manifest.Counts.Doors} placements  •  " +
                           $"source {manifest.Target.ExecutableSha256[..12]}";
            GD.Print($"NIKAMI_AURORA_KOTOR_BOOT status=pass module={manifest.Module} " +
                     $"rooms={loadedRooms} authoredRooms={manifest.Rooms.Count} creatures={manifest.Counts.Creatures} " +
                     $"sha256={manifest.Target.ExecutableSha256}");
            GD.Print($"NIKAMI_AURORA_NAV status=ready triangles={navigationTriangles.Count} " +
                     $"entry={playerBody.GlobalPosition}");
            var openingActor = manifest.Creatures.FirstOrDefault(creature => creature.Dialogue is not null);
            if (openingActor is not null)
                LoadOpeningDialogue(openingActor, manifestDirectory);
            capturedFrames = 0;
            readyFrames = 0;
            moduleReady = true;
        }
        catch (Exception exception)
        {
            status.Text = "KOTOR MODULE LOAD FAILED";
            details.Text = exception.Message;
            GD.PushError($"NIKAMI_AURORA_KOTOR_BOOT status=fail error={exception}");
        }
    }

    private void CreateEnvironment()
    {
        var worldEnvironment = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.004f, 0.008f, 0.018f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.48f, 0.56f, 0.68f),
                AmbientLightEnergy = 1.25f
            }
        };
        AddChild(worldEnvironment);
        var key = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-58, -32, 0),
            LightColor = new Color(0.72f, 0.82f, 1.0f),
            LightEnergy = 0.65f,
            ShadowEnabled = true
        };
        AddChild(key);
    }

    private void CreateCamera()
    {
        playerBody = new CharacterBody3D { Name = "Player" };
        AddChild(playerBody);
        var playerCollision = new CollisionShape3D
        {
            Position = Vector3.Up * 0.85f,
            Shape = new CapsuleShape3D { Radius = 0.32f, Height = 1.7f }
        };
        playerBody.AddChild(playerCollision);
        camera = new Camera3D
        {
            Current = true,
            Near = 0.05f,
            Far = 1000.0f,
            Fov = 72.0f
        };
        camera.Position = Vector3.Up * 1.65f;
        playerBody.AddChild(camera);
    }

    private void CreateOverlay()
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        loadingBackdrop = new ColorRect
        {
            Color = new Color(0.005f, 0.012f, 0.025f, 0.94f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        loadingBackdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(loadingBackdrop);

        var panel = new VBoxContainer
        {
            Position = new Vector2(36, 32),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        layer.AddChild(panel);
        var brand = new Label { Text = "NIKAMI / AURORA", MouseFilter = Control.MouseFilterEnum.Ignore };
        brand.AddThemeFontSizeOverride("font_size", 18);
        brand.AddThemeColorOverride("font_color", new Color(0.42f, 0.78f, 1.0f));
        panel.AddChild(brand);
        status = new Label { Text = "INITIALIZING ODYSSEY PROFILE" };
        status.AddThemeFontSizeOverride("font_size", 30);
        panel.AddChild(status);
        details = new Label { Text = "Waiting for module manifest..." };
        details.AddThemeFontSizeOverride("font_size", 16);
        details.AddThemeColorOverride("font_color", new Color(0.72f, 0.8f, 0.9f));
        panel.AddChild(details);

        var controls = new Label
        {
            Text = "WASD move  •  mouse look  •  Shift sprint  •  E interact  •  Esc release mouse",
            Position = new Vector2(36, 680),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        controls.AddThemeFontSizeOverride("font_size", 14);
        controls.AddThemeColorOverride("font_color", new Color(0.62f, 0.7f, 0.8f));
        layer.AddChild(controls);

        interactionPrompt = new Label
        {
            Position = new Vector2(460, 610),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        interactionPrompt.AddThemeFontSizeOverride("font_size", 20);
        interactionPrompt.AddThemeColorOverride("font_color", new Color(0.45f, 0.88f, 1.0f));
        layer.AddChild(interactionPrompt);

        dialoguePanel = new PanelContainer
        {
            AnchorLeft = 0.12f,
            AnchorTop = 0.66f,
            AnchorRight = 0.88f,
            AnchorBottom = 0.96f,
            OffsetLeft = 0,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0,
            Visible = false
        };
        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.015f, 0.025f, 0.045f, 0.94f),
            BorderColor = new Color(0.2f, 0.62f, 0.9f, 0.9f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 22,
            ContentMarginTop = 16,
            ContentMarginRight = 22,
            ContentMarginBottom = 16
        };
        dialoguePanel.AddThemeStyleboxOverride("panel", panelStyle);
        layer.AddChild(dialoguePanel);
        var dialogueLayout = new VBoxContainer();
        dialoguePanel.AddChild(dialogueLayout);
        dialogueSpeaker = new Label();
        dialogueSpeaker.AddThemeFontSizeOverride("font_size", 18);
        dialogueSpeaker.AddThemeColorOverride("font_color", new Color(0.4f, 0.82f, 1.0f));
        dialogueLayout.AddChild(dialogueSpeaker);
        dialogueText = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 64)
        };
        dialogueText.AddThemeFontSizeOverride("font_size", 22);
        dialogueLayout.AddChild(dialogueText);
        dialogueChoices = new VBoxContainer();
        dialogueLayout.AddChild(dialogueChoices);
    }

    private void LoadOpeningDialogue(CreatureRecord actor, string manifestDirectory)
    {
        if (actor.Dialogue is null) return;
        var path = Path.GetFullPath(Path.Combine(manifestDirectory,
            actor.Dialogue.Path.Replace('/', Path.DirectorySeparatorChar)));
        var graph = JsonSerializer.Deserialize<DialogueGraph>(File.ReadAllText(path), JsonOptions())
                    ?? throw new InvalidDataException($"Dialogue graph is empty: {path}");
        if (graph.Schema != "nikami-aurora-kotor-dialogue-v1" || graph.Starters.Count == 0)
            throw new InvalidDataException($"Unsupported dialogue graph: {path}");
        var starterIndex = Math.Clamp(graph.OpeningStarter, 0, graph.Starters.Count - 1);
        PresentDialogueNode(graph, graph.Starters[starterIndex].Target, new HashSet<string>(), 0);
    }

    private void PresentDialogueNode(DialogueGraph graph, string key, HashSet<string> visited, int depth)
    {
        if (depth > 32 || !visited.Add(key) || !graph.Nodes.TryGetValue(key, out var node))
        {
            dialoguePanel.Visible = false;
            return;
        }
        if (string.IsNullOrWhiteSpace(node.Text))
        {
            if (node.Links.Count > 0)
                PresentDialogueNode(graph, node.Links[0].Target, visited, depth + 1);
            return;
        }

        dialoguePanel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (node.Kind == "reply")
            lastDialogueSpeaker = "PLAYER";
        else if (node.Speaker.Equals("end_trask", StringComparison.OrdinalIgnoreCase))
            lastDialogueSpeaker = "TRASK ULGO";
        else if (!string.IsNullOrWhiteSpace(node.Speaker))
            lastDialogueSpeaker = node.Speaker.ToUpperInvariant();
        dialogueSpeaker.Text = lastDialogueSpeaker;
        dialogueText.Text = node.Text;
        foreach (var child in dialogueChoices.GetChildren())
            child.QueueFree();
        activeChoiceButtons.Clear();

        var choices = new List<(DialogueNode Node, string Target)>();
        foreach (var link in node.Links)
        {
            var target = ResolveVisibleNode(graph, link.Target, new HashSet<string>(), 0);
            if (target is not null)
                choices.Add((target, link.Target));
        }
        if (choices.Count == 0)
        {
            var close = CreateChoiceButton("Continue");
            close.Pressed += () => dialoguePanel.Visible = false;
            dialogueChoices.AddChild(close);
            activeChoiceButtons.Add(close);
            return;
        }
        foreach (var choice in choices)
        {
            var label = choice.Node.Kind == "reply" ? choice.Node.Text : "Continue";
            var button = CreateChoiceButton(label);
            var targetKey = choice.Target;
            button.Pressed += () => FollowDialogueChoice(graph, targetKey);
            dialogueChoices.AddChild(button);
            activeChoiceButtons.Add(button);
        }
    }

    private void FollowDialogueChoice(DialogueGraph graph, string key)
    {
        var visible = ResolveVisibleNode(graph, key, new HashSet<string>(), 0);
        if (visible is null)
        {
            dialoguePanel.Visible = false;
            return;
        }
        if (visible.Kind == "reply" && visible.Links.Count > 0)
            PresentDialogueNode(graph, visible.Links[0].Target, new HashSet<string>(), 0);
        else
            PresentDialogueNode(graph, key, new HashSet<string>(), 0);
    }

    private static DialogueNode? ResolveVisibleNode(DialogueGraph graph, string key,
        HashSet<string> visited, int depth)
    {
        if (depth > 32 || !visited.Add(key) || !graph.Nodes.TryGetValue(key, out var node)) return null;
        if (!string.IsNullOrWhiteSpace(node.Text)) return node;
        return node.Links.Count > 0
            ? ResolveVisibleNode(graph, node.Links[0].Target, visited, depth + 1)
            : null;
    }

    private static Button CreateChoiceButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Alignment = HorizontalAlignment.Left,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 34)
        };
        button.AddThemeFontSizeOverride("font_size", 16);
        return button;
    }

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

    private int LoadActorModels(IEnumerable<CreatureRecord> creatures, string manifestDirectory)
    {
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
            actor.Name = $"Actor_{creature.Template}";
            actor.Position = ToGodot(creature.Position);
            actor.Rotation = new Vector3(0, -creature.Bearing, 0);
            MakeDiffuseProofReadable(actor);
            AddChild(actor);
            loaded++;
        }
        return loaded;
    }

    private int LoadDoorModels(IEnumerable<DoorRecord> doors, string manifestDirectory)
    {
        interactiveDoors.Clear();
        foreach (var door in doors)
        {
            if (string.IsNullOrWhiteSpace(door.Glb)) continue;
            var path = Path.GetFullPath(Path.Combine(manifestDirectory,
                door.Glb.Replace('/', Path.DirectorySeparatorChar)));
            var document = new GltfDocument();
            var state = new GltfState();
            if (document.AppendFromFile(path, state) != Error.Ok ||
                document.GenerateScene(state) is not Node3D model)
                throw new InvalidDataException($"Godot could not import door {door.Tag}: {path}");
            model.Name = $"Door_{door.Tag}";
            model.Position = ToGodot(door.Position);
            model.Rotation = new Vector3(0, -door.Bearing, 0);
            MakeDiffuseProofReadable(model);
            AddChild(model);
            interactiveDoors.Add(new InteractiveDoor(door, model, model.Position));
            GD.Print($"NIKAMI_AURORA_DOOR status=ready tag={door.Tag} model={door.Model} " +
                     $"conversation={door.Conversation} nativeOnOpen={door.OnOpen}");
        }
        return interactiveDoors.Count;
    }

    private void UpdateInteractionPrompt()
    {
        if (dialoguePanel.Visible)
        {
            interactionPrompt.Visible = false;
            return;
        }
        var door = NearestDoor(2.6f);
        interactionPrompt.Visible = door is not null;
        if (door is not null)
            interactionPrompt.Text = door.Open ? "E  CLOSE LOCKDOWN DOOR" : "E  OPEN LOCKDOWN DOOR";
    }

    private InteractiveDoor? NearestDoor(float maximumDistance)
    {
        InteractiveDoor? nearest = null;
        var best = maximumDistance;
        foreach (var door in interactiveDoors)
        {
            var delta = door.ClosedPosition - playerBody.GlobalPosition;
            delta.Y = 0;
            var distance = delta.Length();
            if (distance >= best) continue;
            best = distance;
            nearest = door;
        }
        return nearest;
    }

    private void ToggleDoor(InteractiveDoor door)
    {
        door.Open = !door.Open;
        var destination = door.Open ? door.ClosedPosition + Vector3.Up * 2.8f : door.ClosedPosition;
        CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut)
            .TweenProperty(door.Model, "position", destination, 0.7);
        GD.Print($"NIKAMI_AURORA_DOOR status={(door.Open ? "opened" : "closed")} " +
                 $"tag={door.Source.Tag} model={door.Source.Model} conversation={door.Source.Conversation} " +
                 $"nativeOnOpen={door.Source.OnOpen}");
    }

    private static void MakeDiffuseProofReadable(Node node)
    {
        if (node is MeshInstance3D instance && instance.Mesh is not null)
        {
            for (var surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
            {
                if (instance.GetActiveMaterial(surface) is not BaseMaterial3D source) continue;
                if (source.AlbedoTexture is not null && source.EmissionTexture is not null)
                {
                    var lightmapped = new ShaderMaterial { Shader = OdysseyLightmapShader };
                    lightmapped.SetShaderParameter("albedo_texture", source.AlbedoTexture);
                    lightmapped.SetShaderParameter("lightmap_texture", source.EmissionTexture);
                    lightmapped.SetShaderParameter("lightmap_gain", 2.0f);
                    instance.SetSurfaceOverrideMaterial(surface, lightmapped);
                    continue;
                }
                var material = (BaseMaterial3D)source.Duplicate();
                material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                material.Metallic = 0;
                material.Roughness = 1;
                instance.SetSurfaceOverrideMaterial(surface, material);
            }
        }
        foreach (var child in node.GetChildren())
            MakeDiffuseProofReadable(child);
    }

    private void BuildNavigation(IEnumerable<RoomRecord> rooms)
    {
        navigationTriangles.Clear();
        foreach (var room in rooms)
        {
            if (room.WalkmeshTriangles is null) continue;
            foreach (var triangle in room.WalkmeshTriangles)
            {
                if (triangle.Count != 3) continue;
                navigationTriangles.Add(new NavigationTriangle(
                    ToGodotWithOffset(triangle[0], room.Position),
                    ToGodotWithOffset(triangle[1], room.Position),
                    ToGodotWithOffset(triangle[2], room.Position)));
            }
        }
    }

    private bool MovePlayer(Vector3 displacement)
    {
        if (navigationTriangles.Count == 0) return false;
        var candidate = playerBody.GlobalPosition + new Vector3(displacement.X, 0, displacement.Z);
        foreach (var door in interactiveDoors)
        {
            if (door.Open) continue;
            var obstruction = candidate - door.ClosedPosition;
            obstruction.Y = 0;
            if (obstruction.LengthSquared() < 0.65f * 0.65f) return false;
        }
        if (!TryProjectToWalkmesh(candidate, out var ground)) return false;
        candidate.Y = ground;
        playerBody.GlobalPosition = candidate;
        return true;
    }

    private bool TryProjectToWalkmesh(Vector3 position, out float ground)
    {
        ground = 0;
        var bestDistance = float.PositiveInfinity;
        var found = false;
        foreach (var triangle in navigationTriangles)
        {
            var a = new Vector2(triangle.A.X, triangle.A.Z);
            var b = new Vector2(triangle.B.X, triangle.B.Z);
            var c = new Vector2(triangle.C.X, triangle.C.Z);
            var p = new Vector2(position.X, position.Z);
            var denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Mathf.Abs(denominator) < 0.000001f) continue;
            var wa = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / denominator;
            var wb = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / denominator;
            var wc = 1.0f - wa - wb;
            if (wa < -0.002f || wb < -0.002f || wc < -0.002f) continue;
            var candidateGround = wa * triangle.A.Y + wb * triangle.B.Y + wc * triangle.C.Y;
            var distance = Mathf.Abs(candidateGround - position.Y);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            ground = candidateGround;
            found = true;
        }
        return found;
    }

    private static Vector3 ToGodot(IReadOnlyList<float> source) =>
        new(source[0], source[2], -source[1]);

    private static Vector3 ToGodotWithOffset(IReadOnlyList<float> source, IReadOnlyList<float> offset) =>
        ToGodot(new[] { source[0] + offset[0], source[1] + offset[1], source[2] + offset[2] });

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ModuleManifest(
        string Schema,
        string Module,
        EntryRecord Entry,
        TargetRecord Target,
        IReadOnlyList<RoomRecord> Rooms,
        IReadOnlyList<CreatureRecord> Creatures,
        IReadOnlyList<DoorRecord> Doors,
        CountRecord Counts);

    private sealed record EntryRecord(IReadOnlyList<float> Position, float DirectionRadians);
    private sealed record TargetRecord(string ExecutableSha256);
    private sealed record RoomRecord(string Model, string? Glb, IReadOnlyList<float> Position,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<float>>>? WalkmeshTriangles);
    private sealed record CreatureRecord(string Template, IReadOnlyList<float> Position, float Bearing,
        string? Glb, string? Conversation, DialogueReference? Dialogue);
    private sealed record DoorRecord(string Template, string Tag, IReadOnlyList<float> Position, float Bearing,
        string LinkedToModule, string? Glb, string? Model, string? Conversation, string? OnOpen,
        bool Locked, bool KeyRequired);
    private sealed record DialogueReference(string Path, string SourceSha256, int StarterCount,
        int NodeCount, int OpeningStarter);
    private sealed record DialogueGraph(string Schema, int OpeningStarter,
        IReadOnlyList<DialogueLink> Starters, IReadOnlyDictionary<string, DialogueNode> Nodes);
    private sealed record DialogueNode(string Kind, string Text, string Speaker,
        IReadOnlyList<DialogueLink> Links);
    private sealed record DialogueLink(string Target, string Condition1, bool Condition1Not,
        string Condition2, bool Condition2Not, int Logic);
    private sealed record CountRecord(int Rooms, int Creatures, int Doors, int Waypoints, int Cameras,
        int Placeables, int Triggers, int WalkmeshTriangles);
    private readonly record struct NavigationTriangle(Vector3 A, Vector3 B, Vector3 C);
    private sealed class InteractiveDoor(DoorRecord source, Node3D model, Vector3 closedPosition)
    {
        public DoorRecord Source { get; } = source;
        public Node3D Model { get; } = model;
        public Vector3 ClosedPosition { get; } = closedPosition;
        public bool Open { get; set; }
    }
}
