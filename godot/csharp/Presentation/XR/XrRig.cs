using Godot;
using Nikami.Aurora.GodotRuntime.Presentation.Player;

namespace Nikami.Aurora.GodotRuntime.Presentation.XR;

public partial class XrRig : XROrigin3D
{
    private Camera3D? spectatorCamera;

    public override void _Ready()
    {
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE")) ||
            (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DAOPEN_TOUR")) &&
             System.Environment.GetEnvironmentVariable("DAOPEN_XR_CAPTURE") != "1"))
        {
            GD.Print("OPENDAO_XR disabled=true reason=deterministic_desktop_capture");
            return;
        }
        var xr = XRServer.FindInterface("OpenXR");
        if (xr is null || !xr.IsInitialized())
        {
            GD.Print("OPENDAO_XR fallback=desktop reason=no_initialized_runtime");
            return;
        }
        GetViewport().Set("use_xr", true);
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.PhysicsTicksPerSecond = 90;
        GetNode<Camera3D>("../Head/Camera3D").Current = false;
        var camera = GetNode<Camera3D>("XRCamera3D");
        camera.Current = true;
        GetNode<PlayerController>("..").SetXrActive(true, camera, GetNode<XRController3D>("LeftHand"));
        GD.Print("OPENDAO_XR ready=true interface=OpenXR");
        if (System.Environment.GetEnvironmentVariable("DAOPEN_XR_CAPTURE") == "1") CreateSpectatorMirror();
    }

    public override void _Process(double delta)
    {
        if (spectatorCamera is not null)
            spectatorCamera.GlobalTransform = GetNode<Camera3D>("XRCamera3D").GlobalTransform;
    }

    private void CreateSpectatorMirror()
    {
        GetTree().Root.GuiEmbedSubwindows = false;
        var window = new Window { Title = "Nikami.Aurora.GodotRuntime XR Spectator", Size = new(1280, 720), Unresizable = true };
        var container = new SubViewportContainer { Stretch = true };
        container.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var viewport = new SubViewport
        {
            Size = new(1280, 720),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            World3D = GetViewport().World3D
        };
        container.AddChild(viewport);
        spectatorCamera = new Camera3D { Fov = 78, Near = 0.05f, Far = 1000 };
        viewport.AddChild(spectatorCamera);
        spectatorCamera.Current = true;
        window.AddChild(container);
        window.AddChild(new Label { Text = "META XR SIMULATOR  •  OPENDAO", Position = new(18, 14) });
        AddChild(window);
        GD.Print("OPENDAO_XR_SPECTATOR ready=true title=Nikami.Aurora.GodotRuntime XR Spectator size=1280x720");
    }
}
