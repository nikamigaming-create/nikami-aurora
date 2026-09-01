using Godot;
using Nikami.Aurora.GodotRuntime.Domain.Abilities;
using Nikami.Aurora.GodotRuntime.Domain.Inventory;
using Nikami.Aurora.GodotRuntime.Domain.Quests;
using Nikami.Aurora.GodotRuntime.MainMenu;
using Nikami.Aurora.GodotRuntime.Infrastructure.Archives;
using Nikami.Aurora.GodotRuntime.Presentation;
using Nikami.Aurora.GodotRuntime.Presentation.Player;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;
using Nikami.Aurora.GodotRuntime.Rendering;
using Nikami.Aurora.GodotRuntime.Application.Characters;

namespace Nikami.Aurora.GodotRuntime.Presentation.World;

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
    private readonly Dictionary<View, RetailGfxCanvas> retailMenus = [];
    private readonly Dictionary<View, List<Control>> retailMenuOverlays = [];
    private readonly AbilityState abilities;
    private readonly InventoryState inventory;
    private readonly QuestJournal quests;
    private View current;

    public WorldHud(Node host, AbilityState abilities, InventoryState inventory, QuestJournal quests,
        WorldProfile world, IAreaPresentationProvider areaPresentation,
        Nikami.Aurora.GodotRuntime.Domain.Characters.CharacterProfile character,
        CharacterProgression progression, PlayerController player)
    {
        this.abilities = abilities;
        this.inventory = inventory;
        this.quests = quests;
        layer.Name = "WorldHud";
        layer.Layer = 20;
        BuildAuthoredHud(world, areaPresentation, character, progression, player);
        panel.Visible = false;
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Colors.Transparent,
            ContentMarginLeft = 10,
            ContentMarginTop = 8,
            ContentMarginRight = 10,
            ContentMarginBottom = 8
        });
        var layout = new VBoxContainer();
        title.AddThemeFontSizeOverride("font_size", 19);
        title.AddThemeColorOverride("font_color", new Color(0.16f, 0.11f, 0.07f));
        content.AddThemeColorOverride("default_color", new Color(0.19f, 0.14f, 0.09f));
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
        Nikami.Aurora.GodotRuntime.Domain.Characters.CharacterProfile character,
        CharacterProgression progression, PlayerController player)
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
            "atl_shared_dxt5_dat.xml",
            "atl_guiscreens_dxt5_dat.xml",
            "atl_itemupgrad_dxt5_dat.xml",
            "atl_chanters_dxt5_dat.xml");
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
        var inventoryMenu = AddRetailMenu(
            View.Inventory,
            "inventory.gfx",
            "equipment",
            archive,
            atlas,
            quad => quad.Image.Name.Equals("paperdoll.dds", StringComparison.OrdinalIgnoreCase)
                ? quad with { Alpha = 0 }
                : quad);
        AddInventoryPaperDoll(inventoryMenu, player);
        AddRetailMenu(View.Quests, "journal.gfx", "CurrentQuests", archive, atlas);
        AddRetailMenu(View.Abilities, "abilities.gfx", usesMana ? "spells" : "talents",
            archive, atlas);
        AddRetailMenu(View.Crafting, "crafting.gfx", "crafting", archive, atlas);
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
        var level = new Label
        {
            Name = "CharacterLevel",
            Text = $"Level {progression.Level}",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        level.AddThemeFontSizeOverride("font_size", 11);
        if (dragonText is not null) level.AddThemeFontOverride("font", dragonText);
        level.AddThemeColorOverride("font_color", new Color(0.86f, 0.77f, 0.43f));
        level.AddThemeColorOverride("font_outline_color", Colors.Black);
        level.AddThemeConstantOverride("outline_size", 1);
        stage.AddChild(level);
        Anchor(level, new Rect2(50, 704, 180, 16), RetailGfxAnchor.BottomLeft);
        progression.Changed += (_, currentLevel) => level.Text = $"Level {currentLevel}";
        stage.Resized += LayoutAnchoredControls;
        Callable.From(LayoutAnchoredControls).CallDeferred();
        GD.Print("OPENDAO_RETAIL_HUD status=ready source=gfx-display-lists " +
                 "stage=1024x768 reference=1920x1080 portraits=1 minimap=1 " +
                 "quickbar_slots=14 navbar=1");
    }

    private RetailGfxCanvas AddRetailMenu(
        View view,
        string resource,
        string rootLabel,
        ErfArchive archive,
        GfxAtlas atlas,
        Func<GfxQuad, GfxQuad?>? select = null)
    {
        var canvas = new RetailGfxCanvas(
            "Retail" + view,
            archive,
            atlas,
            resource,
            RetailGfxAnchor.TopCenter,
            select,
            rootLabel: rootLabel,
            scaleMode: RetailGfxScaleMode.FitStage);
        canvas.Visible = false;
        layer.AddChild(canvas);
        retailMenus.Add(view, canvas);
        GD.Print($"OPENDAO_RETAIL_MENU status=ready view={view.ToString().ToLowerInvariant()} " +
                 $"source={resource} quads={canvas.QuadCount} frames={canvas.RootFrameCount} " +
                 $"label={rootLabel} stage={canvas.StageSize}");
        return canvas;
    }

    private void AddInventoryPaperDoll(RetailGfxCanvas menu, PlayerController player)
    {
        if (menu.ReferenceBounds("paperdoll.dds") is not { } sourceBounds ||
            sourceBounds.Size.X <= 0 || sourceBounds.Size.Y <= 0)
        {
            GD.PushWarning("OPENDAO_RETAIL_INVENTORY_PAPERDOLL status=missing-source-bounds");
            return;
        }

        var viewport = new SubViewport
        {
            Name = "InventoryPaperDollViewport",
            Size = new Vector2I(512, 768),
            TransparentBg = true,
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = Viewport.Msaa.Msaa4X
        };
        var camera = new Camera3D { Current = true, Fov = 26, Near = 0.03f };
        viewport.AddChild(camera);
        viewport.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-30, -25, 0),
            LightColor = new Color(1.0f, 0.84f, 0.68f),
            LightEnergy = 1.7f
        });
        viewport.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-20, 155, 0),
            LightColor = new Color(0.36f, 0.46f, 0.72f),
            LightEnergy = 0.75f
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
        if (player.DuplicateAvatarForPortrait() is not { } avatar)
        {
            GD.PushWarning("OPENDAO_RETAIL_INVENTORY_PAPERDOLL status=missing-avatar");
            return;
        }
        avatar.Transform = Transform3D.Identity;
        viewport.AddChild(avatar);
        avatar.Rotation = new Vector3(0, Mathf.Pi, 0);
        var bounds = SceneBounds.Calculate(avatar);
        if (bounds.Size.IsZeroApprox())
            bounds = new Aabb(new Vector3(-0.5f, 0, -0.5f), new Vector3(1, 1.8f, 1));
        avatar.Position -= bounds.GetCenter();
        var height = Math.Max(1.0f, bounds.Size.Y);
        camera.Position = Vector3.Back * height * 2.45f;
        camera.LookAtFromPosition(camera.Position, Vector3.Zero, Vector3.Up);
        menu.AddChild(viewport);

        var paperDoll = new TextureRect
        {
            Name = "InventoryPaperDoll",
            Texture = viewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false
        };
        layer.AddChild(paperDoll);
        retailMenuOverlays[View.Inventory] = [paperDoll];
        void Layout()
        {
            var placement = RetailGfxLayout.FitStage(menu.Size,
                new GfxRect(0, menu.StageSize.X, 0, menu.StageSize.Y));
            paperDoll.Position = placement.Point(sourceBounds.Position);
            paperDoll.Size = placement.Size(sourceBounds.Size);
        }
        menu.Resized += Layout;
        Callable.From(Layout).CallDeferred();
        GD.Print($"OPENDAO_RETAIL_INVENTORY_PAPERDOLL status=ready source=player-avatar " +
                 $"reference={sourceBounds}");
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
    public void ToggleCrafting() => Toggle(View.Crafting);
    public void Close() => Toggle(View.None);

    private void Toggle(View requested)
    {
        current = current == requested ? View.None : requested;
        if (retailStage is not null) retailStage.Visible = current == View.None;
        foreach (var menu in retailMenus)
            menu.Value.Visible = menu.Key == current;
        foreach (var overlay in retailMenuOverlays)
            foreach (var control in overlay.Value)
                control.Visible = overlay.Key == current;
        panel.Visible = current != View.None;
        Input.MouseMode = panel.Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        LayoutMenuContent();
        Refresh();
    }

    private void LayoutMenuContent()
    {
        if (current == View.None || !retailMenus.TryGetValue(current, out var menu)) return;
        var reference = current switch
        {
            View.Inventory => new Rect2(675, 155, 300, 515),
            View.Quests => new Rect2(170, 150, 675, 505),
            View.Abilities => new Rect2(610, 155, 340, 500),
            View.Crafting => new Rect2(590, 155, 360, 505),
            _ => default
        };
        var placement = RetailGfxLayout.FitStage(menu.Size, new GfxRect(0, 1024, 0, 768));
        panel.Position = placement.Point(reference.Position);
        panel.Size = placement.Size(reference.Size);
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
            case View.Crafting:
                title.Text = "Crafting";
                content.Text = "[i]No learned recipes in the current character state[/i]";
                break;
        }
    }

    private static string Escape(string value) => value.Replace("[", "[​", StringComparison.Ordinal);
    private enum View { None, Inventory, Quests, Abilities, Crafting }
}
