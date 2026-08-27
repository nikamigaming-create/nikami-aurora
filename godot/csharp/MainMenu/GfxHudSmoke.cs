using Godot;
using OpenDAO.Infrastructure.Archives;

namespace OpenDAO.MainMenu;

internal static class GfxHudSmoke
{
    private const string EnvironmentVariable = "OPENDAO_GFX_HUD_SMOKE";

    private static readonly string[] Movies =
    [
        "chargen.gfx",
        "minimap.gfx",
        "navbar.gfx",
        "partypicker.gfx",
        "portraits.gfx",
        "quickbar.gfx"
    ];

    internal static bool Requested =>
        !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static void Run(SceneTree tree)
    {
        try
        {
            var archivePath = System.Environment.GetEnvironmentVariable(EnvironmentVariable)!.Trim();
            var archive = ErfArchive.Open(archivePath);
            foreach (var resourceName in Movies)
            {
                var movie = GfxMovie.Read(archive.Read(resourceName));
                var player = new GfxPlayer(movie);
                player.Advance();
                var quads = player.Collect();
                var instances = player.CollectInstances();
                GD.Print($"OPENDAO_GFX_HUD_MOVIE resource={resourceName} " +
                         $"stage={movie.Stage.Width:F2}x{movie.Stage.Height:F2} " +
                         $"origin={movie.Stage.XMin:F2},{movie.Stage.YMin:F2} " +
                         $"fps={movie.FrameRate:F2} root_frames={movie.Root.Frames.Count} " +
                         $"sprites={movie.Sprites.Count} shapes={movie.Shapes.Count} " +
                         $"images={movie.Images.Count} quads={quads.Count} " +
                         $"multiply_quads={quads.Count(quad => quad.Blend != 0)} " +
                         $"named_instances={instances.Count}");

                if (resourceName.Equals("chargen.gfx", StringComparison.OrdinalIgnoreCase))
                {
                    GD.Print("OPENDAO_GFX_CHARGEN_LABELS " + string.Join(',',
                        movie.Root.Labels.OrderBy(value => value.Value)
                            .Select(value => $"{value.Key}:{value.Value}")));
                    foreach (var label in movie.Root.Labels.OrderBy(value => value.Value))
                    {
                        var labelled = new GfxPlayer(movie);
                        if (!labelled.SeekRoot(label.Key)) continue;
                        GD.Print($"OPENDAO_GFX_CHARGEN_SCREEN label={label.Key} frame={label.Value} " +
                                 $"quads={labelled.Collect().Count} " +
                                 $"named_instances={labelled.CollectInstances().Count}");
                        if (label.Key is "RaceGender" or "SoundsetAppearance" or "Review")
                        {
                            foreach (var quad in labelled.Collect())
                            {
                                GD.Print($"OPENDAO_GFX_CHARGEN_SCREEN_QUAD label={label.Key} " +
                                         $"image={quad.Image.Name} size={quad.Image.Width}x{quad.Image.Height} " +
                                         $"transform={Matrix(quad.Transform)} alpha={quad.Alpha:F4}");
                            }
                            foreach (var instance in labelled.CollectInstances())
                            {
                                GD.Print($"OPENDAO_GFX_CHARGEN_SCREEN_INSTANCE label={label.Key} " +
                                         $"path={instance.Path} name={instance.Name} " +
                                         $"character={instance.CharacterId} transform={Matrix(instance.Transform)}");
                            }
                        }
                    }
                }

                foreach (var quad in quads.Take(80))
                {
                    GD.Print($"OPENDAO_GFX_HUD_QUAD resource={resourceName} key={quad.Key} " +
                             $"image={quad.Image.Name} size={quad.Image.Width}x{quad.Image.Height} " +
                             $"fill={Matrix(quad.Fill)} transform={Matrix(quad.Transform)} " +
                             $"alpha={quad.Alpha:F4} blend={quad.Blend}");
                }

                foreach (var instance in instances.Take(120))
                {
                    GD.Print($"OPENDAO_GFX_HUD_INSTANCE resource={resourceName} " +
                             $"path={instance.Path} name={instance.Name} " +
                             $"character={instance.CharacterId} transform={Matrix(instance.Transform)}");
                }
            }

            GD.Print($"OPENDAO_GFX_HUD_SMOKE status=pass movies={Movies.Length}");
            tree.Quit();
        }
        catch (Exception exception)
        {
            GD.PushError("OPENDAO_GFX_HUD_SMOKE status=fail error=" + exception.Message);
            tree.Quit(1);
        }
    }

    private static string Matrix(GfxMatrix value) =>
        $"{value.ScaleX:F5},{value.RotateSkew0:F5},{value.RotateSkew1:F5}," +
        $"{value.ScaleY:F5},{value.TranslateX:F2},{value.TranslateY:F2}";
}
