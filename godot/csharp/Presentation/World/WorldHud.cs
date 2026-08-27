using Godot;
using OpenDAO.Domain.Abilities;
using OpenDAO.Domain.Inventory;
using OpenDAO.Domain.Quests;
using OpenDAO.MainMenu;
using OpenDAO.Infrastructure.Archives;
using OpenDAO.Presentation;
using OpenDAO.Presentation.Player;
using OpenDAO.Application.Abstractions;
using OpenDAO.Domain.World;
using OpenDAO.Rendering;

namespace OpenDAO.Presentation.World;

internal sealed class WorldHud
{
    private readonly CanvasLayer layer = new();
    private Control? retailStage;
    private GfxAtlas? retailAtlas;
    private Control? abilityIconLayer;
    private SubViewport? minimapViewport;
    private Camera3D? minimapCamera;
    private PlayerController? minimapPlayer;
    private TextureRect? minimapPlayerMarker;
    private float minimapNorthYaw;
    private readonly List<(Control Control, Rect2 Reference, RetailGfxAnchor Anchor)> anchored = [];
    private readonly PanelContainer panel = new();
    private readonly Label title = new();
    private readonly RichTextLabel content = new();
    private readonly AbilityState abilities;
    private readonly InventoryState inventory;
    private readonly QuestJournal quests;
    private View current;

    public WorldHud(Node host, AbilityState abilities, InventoryState inventory, QuestJournal quests,
        WorldProfile world, IAreaPresentationProvider areaPresentation,
        OpenDAO.Domain.Characters.CharacterProfile character, PlayerController player)
    {
        this.abilities = abilities;
        this.inventory = inventory;
        this.quests = quests;
        layer.Name = "WorldHud";
        layer.Layer = 20;
        BuildAuthoredHud(world, areaPresentation, character, player);
        panel.Position = new Vector2(48, 96);
        panel.Size = new Vector2(520, 620);
        panel.Visible = false;
        var layout = new VBoxContainer();
        title.AddThemeFontSizeOverride("font_size", 24);
        content.CustomMinimumSize = new Vector2(480, 540);
        content.BbcodeEnabled = true;
        layout.AddChild(title);
        layout.AddChild(content);
        panel.AddChild(layout);
        layer.AddChild(panel);
        host.AddChild(layer);
        inventory.Changed += _ => Refresh();
        quests.Changed += _ => Refresh();
        abilities.Changed += _ =>
        {
            RebuildAbilityIcons();
            Refresh();
        };
    }

    private void BuildAuthoredHud(WorldProfile world, IAreaPresentationProvider areaPresentation,
        OpenDAO.Domain.Characters.CharacterProfile character, PlayerController player)
    {
        var archivePath = Path.Combine(world.GameRoot, "packages", "core", "data", "guiexport.erf");
        if (!File.Exists(archivePath))
        {
            GD.PushWarning("OPENDAO_RETAIL_HUD status=missing archive=" + archivePath);
            return;
        }
        var archive = ErfArchive.Open(archivePath);
        var dragonText = RetailGuiFontLoader.LoadDragonText(archive);
        var atlas = new GfxAtlas(
            archive,
            "atl_shared_dxt1_dat.xml",
            "atl_shared_dxt5_dat.xml");
        retailAtlas = atlas;
        var stage = new Control
        {
            Name = "RetailHud",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        stage.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(stage);
        retailStage = stage;
        var presentation = areaPresentation.Resolve(world);

        var usesMana = character.Class.Equals("mage", StringComparison.OrdinalIgnoreCase);
        stage.AddChild(new RetailGfxCanvas(
            "Portraits",
            archive,
            atlas,
            "portraits.gfx",
            RetailGfxAnchor.TopLeft,
            quad => SelectPlayerPortrait(quad, usesMana)));
        AddCharacterPortrait(stage, player);

        AddMinimap(stage, player,
            presentation.Succeeded ? presentation.Presentation.NormalizedNorthQuarterTurns : 0);
        minimapPlayerMarker = AddAnchoredTexture(stage, atlas, "Minimap_ID.dds",
            new Rect2(916, 86, 16, 16), RetailGfxAnchor.TopRight, "PlayerMinimapMarker");
        AddAnchoredTexture(stage, atlas, "Minimap_I49.dds",
            new Rect2(824, 0, 200, 201), RetailGfxAnchor.TopRight, "MinimapFrame");
        stage.AddChild(new RetailGfxCanvas(
            "MinimapControls", archive, atlas, "minimap.gfx", RetailGfxAnchor.TopRight));
        stage.AddChild(new RetailGfxCanvas(
            "Navbar", archive, atlas, "navbar.gfx", RetailGfxAnchor.TopCenter));
        stage.AddChild(new RetailGfxCanvas(
            "Quickbar",
            archive,
            atlas,
            "quickbar.gfx",
            RetailGfxAnchor.BottomLeft,
            SelectQuickbar,
            QuickbarSourceRegion));

        abilityIconLayer = new Control { Name = "QuickbarAbilities", MouseFilter = Control.MouseFilterEnum.Ignore };
        abilityIconLayer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        stage.AddChild(abilityIconLayer);
        RebuildAbilityIcons();

        if (presentation.Succeeded)
        {
            var location = new Label
            {
                Name = "AreaName",
                Text = presentation.Presentation.DisplayName,
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            location.AddThemeFontSizeOverride("font_size", 12);
            if (dragonText is not null) location.AddThemeFontOverride("font", dragonText);
            location.AddThemeColorOverride("font_color", new Color(0.86f, 0.77f, 0.43f));
            location.AddThemeColorOverride("font_outline_color", Colors.Black);
            location.AddThemeConstantOverride("outline_size", 1);
            stage.AddChild(location);
            Anchor(location, new Rect2(824, 200, 200, 18), RetailGfxAnchor.TopRight);
            GD.Print($"OPENDAO_RETAIL_AREA_NAME status=ready " +
                     $"text={presentation.Presentation.DisplayName} " +
                     $"north={presentation.Presentation.NormalizedNorthQuarterTurns} " +
                     "source=installed-are+talktable");
        }
        else
        {
            GD.PushWarning("OPENDAO_RETAIL_AREA_NAME status=unavailable reason=" + presentation.Error);
        }

        var name = new Label
        {
            Name = "CharacterName",
            Text = character.DisplayName,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        name.AddThemeFontSizeOverride("font_size", 12);
        if (dragonText is not null) name.AddThemeFontOverride("font", dragonText);
        name.AddThemeColorOverride("font_color", new Color(0.86f, 0.77f, 0.43f));
        name.AddThemeColorOverride("font_outline_color", Colors.Black);
        name.AddThemeConstantOverride("outline_size", 1);
        stage.AddChild(name);
        Anchor(name, new Rect2(50, 686, 180, 18), RetailGfxAnchor.BottomLeft);
        stage.Resized += LayoutAnchoredControls;
        Callable.From(LayoutAnchoredControls).CallDeferred();
        GD.Print("OPENDAO_RETAIL_HUD status=ready source=gfx-display-lists " +
                 "stage=1024x768 reference=1920x1080 portraits=1 minimap=1 " +
                 "quickbar_slots=14 navbar=1");
    }

    private void AddCharacterPortrait(Control stage, PlayerController player)
    {
        var viewport = new SubViewport
        {
            Name = "CharacterPortraitViewport",
            Size = new Vector2I(128, 128),
            TransparentBg = true,
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = Viewport.Msaa.Msaa4X
        };
        var camera = new Camera3D
        {
            Current = true,
            Fov = 30,
            Near = 0.03f
        };
        var modelRoot = new Node3D { Name = "PortraitModel" };
        viewport.AddChild(modelRoot);
        viewport.AddChild(camera);
        viewport.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-35, -25, 0),
            LightColor = new Color(1.0f, 0.83f, 0.67f),
            LightEnergy = 1.6f
        });
        viewport.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-15, 150, 0),
            LightColor = new Color(0.38f, 0.48f, 0.75f),
            LightEnergy = 0.8f
        });
        viewport.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0, 0, 0, 0),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.42f, 0.38f, 0.34f),
                AmbientLightEnergy = 0.72f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic
            }
        });
        if (player.DuplicateAvatarForPortrait() is { } avatar)
        {
            // The gameplay avatar root is already offset to the character
            // body's ground capsule. Reset that world-composition offset before
            // independently centring the duplicate in the portrait world.
            avatar.Transform = Transform3D.Identity;
            modelRoot.AddChild(avatar);
            modelRoot.Rotation = new Vector3(0, Mathf.Pi, 0);
            var bounds = SceneBounds.Calculate(avatar);
            if (bounds.Size.IsZeroApprox())
            {
                bounds = new Aabb(new Vector3(-0.5f, 0, -0.5f), new Vector3(1, 1.8f, 1));
            }

            avatar.Position -= bounds.GetCenter();
            var height = Math.Max(1.0f, bounds.Size.Y);
            var target = Vector3.Up * height * 0.34f;
            camera.Position = target + Vector3.Back * height * 0.54f;
            camera.LookAtFromPosition(camera.Position, target, Vector3.Up);
        }
        else
        {
            GD.PushWarning("OPENDAO_RETAIL_PORTRAIT status=missing-avatar");
        }
        stage.AddChild(viewport);

        var shader = new Shader
        {
            Code = "shader_type canvas_item;\n" +
                   "void fragment(){ vec4 c=texture(TEXTURE,UV); " +
                   "float edge=1.0-smoothstep(0.46,0.5,distance(UV,vec2(0.5))); " +
                   "COLOR=vec4(c.rgb,c.a*edge); }"
        };
        var portrait = new TextureRect
        {
            Name = "CharacterPortrait",
            Texture = viewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = new ShaderMaterial { Shader = shader }
        };
        stage.AddChild(portrait);
        Anchor(portrait, new Rect2(28.3f, 23, 70, 70), RetailGfxAnchor.TopLeft);
        GD.Print("OPENDAO_RETAIL_PORTRAIT status=ready source=player-avatar");
    }

    private void AddMinimap(Control stage, PlayerController player, int northQuarterTurns)
    {
        minimapPlayer = player;
        minimapNorthYaw = -Mathf.Pi * 0.5f * northQuarterTurns;
        var viewport = new SubViewport
        {
            Name = "RetailMinimapViewport",
            Size = new Vector2I(256, 256),
            World3D = player.GetWorld3D(),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
            Msaa3D = Viewport.Msaa.Msaa2X
        };
        minimapViewport = viewport;
        var camera = new Camera3D
        {
            Name = "RetailMinimapCamera",
            Current = true,
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = 42,
            Far = 180,
            CullMask = WorldRenderLayers.Minimap
        };
        minimapCamera = camera;
        viewport.AddChild(camera);
        stage.AddChild(viewport);

        var shader = new Shader
        {
            Code = "shader_type canvas_item;\n" +
                   "void fragment(){ vec4 c=texture(TEXTURE,UV); " +
                   "float edge=1.0-smoothstep(0.48,0.5,distance(UV,vec2(0.5))); " +
                   "float shade=smoothstep(0.52,0.05,distance(UV,vec2(0.5))); " +
                   "vec3 mapped=pow(max(c.rgb,vec3(0.0)),vec3(0.55)); " +
                   "mapped*=vec3(2.35,1.90,0.80)*(0.78+0.22*shade); " +
                   "COLOR=vec4(mapped,c.a*edge); }"
        };
        var map = new TextureRect
        {
            Name = "AuthoredAreaMinimap",
            Texture = viewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TextureFilter = CanvasItem.TextureFilterEnum.Linear,
            Material = new ShaderMaterial { Shader = shader }
        };
        stage.AddChild(map);
        Anchor(map, new Rect2(840, 10, 168, 168), RetailGfxAnchor.TopRight);

        var refresh = new Godot.Timer
        {
            Name = "MinimapRefresh",
            WaitTime = 0.5,
            Autostart = true
        };
        refresh.Timeout += UpdateMinimap;
        stage.AddChild(refresh);
        UpdateMinimap();
        GD.Print($"OPENDAO_RETAIL_MINIMAP status=ready source=authored-world " +
                 $"north={northQuarterTurns} yaw_degrees={Mathf.RadToDeg(minimapNorthYaw):F0} " +
                 "render_layer=2 refresh_hz=2");
    }

    private void UpdateMinimap()
    {
        if (retailStage?.Visible != true || minimapViewport is null ||
            minimapCamera is null || minimapPlayer is null)
        {
            return;
        }

        minimapCamera.Position = minimapPlayer.GlobalPosition + Vector3.Up * 80;
        minimapCamera.Rotation = new Vector3(-Mathf.Pi * 0.5f, minimapNorthYaw, 0);
        if (minimapPlayerMarker is not null)
        {
            minimapPlayerMarker.PivotOffset = minimapPlayerMarker.Size * 0.5f;
            minimapPlayerMarker.Rotation = minimapNorthYaw - minimapPlayer.GlobalRotation.Y;
        }
        minimapViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    private static GfxQuad? SelectPlayerPortrait(GfxQuad quad, bool usesMana)
    {
        if (!quad.Key.StartsWith("1.310.", StringComparison.Ordinal))
        {
            return null;
        }

        if (quad.Image.Name is "levelUp_I143.dds" or "Portraits_I65.dds")
        {
            return null;
        }

        if (usesMana && quad.Image.Name is "Portraits_I55.dds" or "Portraits_I5B.dds")
        {
            return null;
        }

        if (!usesMana && quad.Image.Name is "Portraits_I58.dds" or "Portraits_I52.dds")
        {
            return null;
        }

        return quad;
    }

    private static GfxQuad? SelectQuickbar(GfxQuad quad)
    {
        if (quad.Image.Name.Equals("Quickbar_I52.dds", StringComparison.OrdinalIgnoreCase) &&
            quad.Transform.TranslateX > 631.01)
        {
            return null;
        }

        if (quad.Image.Name is "Quickbar_I7D.dds" or "Quickbar_I80.dds")
        {
            return quad with
            {
                Transform = quad.Transform with { TranslateX = 678 }
            };
        }

        return quad;
    }

    private static Rect2? QuickbarSourceRegion(GfxQuad quad) =>
        quad.Image.Name.Equals("Quickbar_I50.dds", StringComparison.OrdinalIgnoreCase)
            ? new Rect2(0, 0, 659, 55)
            : null;

    private TextureRect? AddAnchoredTexture(
        Control parent,
        GfxAtlas atlas,
        string resourceName,
        Rect2 reference,
        RetailGfxAnchor anchor,
        string nodeName)
    {
        if (atlas.Load(resourceName) is not { } texture)
        {
            GD.PushWarning("OPENDAO_RETAIL_HUD_ASSET status=missing name=" + resourceName);
            return null;
        }

        var rect = new TextureRect
        {
            Name = nodeName,
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TextureFilter = CanvasItem.TextureFilterEnum.Linear
        };
        parent.AddChild(rect);
        Anchor(rect, reference, anchor);
        return rect;
    }

    private void RebuildAbilityIcons()
    {
        if (abilityIconLayer is null || retailAtlas is null)
        {
            return;
        }

        var oldIcons = abilityIconLayer.GetChildren().OfType<Control>().ToArray();
        if (oldIcons.Length > 0)
        {
            anchored.RemoveAll(entry => oldIcons.Contains(entry.Control));
            foreach (var oldIcon in oldIcons)
            {
                abilityIconLayer.RemoveChild(oldIcon);
                oldIcon.QueueFree();
            }
        }

        for (var slot = 1; slot <= AbilityState.QuickSlotCount; slot++)
        {
            var ability = abilities.ForSlot(slot);
            if (ability is null || ability.Icon.Length == 0)
            {
                continue;
            }

            var icon = retailAtlas.Load(ability.Icon) ?? retailAtlas.Load(ability.Icon + ".dds");
            if (icon is null)
            {
                GD.PushWarning($"OPENDAO_RETAIL_HUD_ABILITY status=missing slot={slot} icon={ability.Icon}");
                continue;
            }

            var rect = new TextureRect
            {
                Name = "Ability" + slot,
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                TextureFilter = CanvasItem.TextureFilterEnum.Linear
            };
            abilityIconLayer.AddChild(rect);
            Anchor(rect, new Rect2(23 + (slot - 1) * 47, 723, 40, 40),
                RetailGfxAnchor.BottomLeft);
        }

        LayoutAnchoredControls();
    }

    private void Anchor(Control control, Rect2 reference, RetailGfxAnchor anchor)
    {
        anchored.Add((control, reference, anchor));
    }

    private void LayoutAnchoredControls()
    {
        if (retailStage is null || retailStage.Size.X <= 0 || retailStage.Size.Y <= 0)
        {
            return;
        }

        var stage = new GfxRect(0, 1024, 0, 768);
        foreach (var entry in anchored)
        {
            if (GodotObject.IsInstanceValid(entry.Control) && !entry.Control.IsQueuedForDeletion())
            {
                RetailGfxLayout.Place(
                    entry.Control, entry.Reference, retailStage.Size, stage, entry.Anchor);
            }
        }
    }

    public bool IsOpen => current != View.None;
    public bool Visible
    {
        get => retailStage?.Visible ?? false;
        set
        {
            if (retailStage is not null) retailStage.Visible = value;
            if (value) UpdateMinimap();
        }
    }
    public void ToggleInventory() => Toggle(View.Inventory);
    public void ToggleQuests() => Toggle(View.Quests);
    public void ToggleAbilities() => Toggle(View.Abilities);
    public void Close() => Toggle(View.None);

    private void Toggle(View requested)
    {
        current = current == requested ? View.None : requested;
        panel.Visible = current != View.None;
        Input.MouseMode = panel.Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        Refresh();
    }

    private void Refresh()
    {
        if (!panel.Visible) return;
        switch (current)
        {
            case View.Inventory:
                title.Text = "Inventory";
                content.Text = inventory.Items.Count == 0 ? "[i]Empty[/i]" : string.Join("\n",
                    inventory.Items.Select(x => $"[b]{Escape(x.Name)}[/b]  ×{x.Quantity}"));
                break;
            case View.Quests:
                title.Text = "Quest Journal";
                content.Text = quests.Entries.Count == 0 ? "[i]No journal entries[/i]" : string.Join("\n\n",
                    quests.Entries.Select(x => $"[b]{Escape(x.Title)}[/b]  [{x.Status}]\n{Escape(x.Description)}"));
                break;
            case View.Abilities:
                title.Text = $"Abilities  •  Resource {abilities.Resource:F0}/{AbilityState.MaximumResource:F0}";
                content.Text = abilities.Granted.Count == 0 ? "[i]No abilities[/i]" : string.Join("\n",
                    abilities.Granted.Values.Select(x => $"[b]{Escape(x.Label)}[/b]  Cost {x.Cost:F0}  Cooldown {x.Cooldown:F1}s"));
                break;
        }
    }

    private static string Escape(string value) => value.Replace("[", "[​", StringComparison.Ordinal);
    private enum View { None, Inventory, Quests, Abilities }
}
