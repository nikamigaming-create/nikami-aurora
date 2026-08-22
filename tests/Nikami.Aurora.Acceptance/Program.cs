using System.Security.Cryptography;
using Nikami.Aurora.Core;
using Nikami.Aurora.Profiles.DragonAgeOrigins;
using Nikami.Aurora.Profiles.Kotor;

namespace Nikami.Aurora.Acceptance;

internal static class Program
{
    public static int Main()
    {
        var suiteRoot = Path.Combine(Path.GetTempPath(), "nikami-aurora-tests",
            Guid.NewGuid().ToString("N"));
        var passed = 0;
        try
        {
            Directory.CreateDirectory(suiteRoot);
            KotorProbeAcceptsCompleteSyntheticInstall(suiteRoot);
            passed++;
            KotorProbeRejectsMissingMarker(suiteRoot);
            passed++;
            DragonAgeProfileRemainsIndependent(suiteRoot);
            passed++;
            RegistryRejectsDuplicateProfiles();
            passed++;
            MarkerRejectsTraversal();
            passed++;
            KotorMovementUsesProfileSpeedsAndFacing();
            passed++;
            KotorMovementRejectsClosedDoor();
            passed++;
            Console.WriteLine($"NIKAMI_AURORA_ACCEPTANCE_PASS tests={passed}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"NIKAMI_AURORA_ACCEPTANCE_FAIL tests={passed} error={exception.Message}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(suiteRoot))
                Directory.Delete(suiteRoot, recursive: true);
        }
    }

    private static void KotorProbeAcceptsCompleteSyntheticInstall(string suiteRoot)
    {
        var profile = new KotorGameProfile();
        var root = Path.Combine(suiteRoot, "kotor-complete");
        MaterializeMarkers(root, profile.Descriptor);
        var executableBytes = new byte[] { 0x4d, 0x5a, 0x01, 0x03 };
        File.WriteAllBytes(Resolve(root, profile.Descriptor.ExecutableRelativePath), executableBytes);

        var result = GameInstallProber.Probe(profile, root);
        Expect(result.IsValid, "complete KOTOR fixture was rejected");
        Expect(result.SchemaVersion == GameInstallProber.CurrentSchemaVersion, "schema version drifted");
        Expect(result.ExecutableSha256 == Convert.ToHexString(SHA256.HashData(executableBytes)),
            "executable hash was not source-bound");
    }

    private static void KotorProbeRejectsMissingMarker(string suiteRoot)
    {
        var profile = new KotorGameProfile();
        var root = Path.Combine(suiteRoot, "kotor-incomplete");
        MaterializeMarkers(root, profile.Descriptor);
        File.Delete(Resolve(root, "chitin.key"));

        var result = GameInstallProber.Probe(profile, root);
        Expect(!result.IsValid, "incomplete KOTOR fixture passed");
        Expect(result.Markers.Single(marker => marker.RelativePath == "chitin.key").Present == false,
            "missing marker was not reported");
    }

    private static void DragonAgeProfileRemainsIndependent(string suiteRoot)
    {
        var profile = new DragonAgeOriginsGameProfile();
        var root = Path.Combine(suiteRoot, "dao-complete");
        MaterializeMarkers(root, profile.Descriptor);

        var result = GameInstallProber.Probe(profile, root);
        Expect(result.IsValid, "complete DAO fixture was rejected");
        Expect(result.ProfileId == DragonAgeOriginsGameProfile.ProfileId, "DAO profile identity changed");
        Expect(result.EngineFamily == "Eclipse", "DAO engine-family boundary changed");
    }

    private static void RegistryRejectsDuplicateProfiles()
    {
        var threw = false;
        try
        {
            _ = new GameProfileRegistry(new IGameProfile[] { new KotorGameProfile(), new KotorGameProfile() });
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Expect(threw, "duplicate profile IDs were accepted");
    }

    private static void MarkerRejectsTraversal()
    {
        var threw = false;
        try
        {
            _ = InstallationMarker.File("../outside.bin");
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Expect(threw, "traversing marker path was accepted");
    }

    private static void KotorMovementUsesProfileSpeedsAndFacing()
    {
        var navigation = new[]
        {
            new KotorNavigationTriangle(
                new System.Numerics.Vector3(-10, -10, 2),
                new System.Numerics.Vector3(10, -10, 2),
                new System.Numerics.Vector3(0, 10, 2))
        };
        var simulation = new KotorMovementSimulation(
            navigation, new KotorMovementConfiguration(2, 6));
        var start = new System.Numerics.Vector3(0, 0, 2);
        var walk = simulation.Step(
            start, 0, new KotorMovementIntent(0, 1, false), 0.5f, []);
        var run = simulation.Step(
            start, MathF.PI / 2, new KotorMovementIntent(0, 1, true), 0.5f, []);
        var deadZone = simulation.Step(
            start, 0, KotorMovementIntent.FromAxes(0.1f, 0.1f, false), 0.5f, []);

        Expect(walk.Accepted && walk.Moved && walk.Mode == KotorLocomotionMode.Walk,
            "profile walk intent was rejected");
        Expect(System.Numerics.Vector3.Distance(
                   walk.Position, new System.Numerics.Vector3(0, 1, 2)) < 0.00001f,
            "profile walk speed or zero-facing basis drifted");
        Expect(System.Numerics.Vector3.Distance(
                   run.Position, new System.Numerics.Vector3(-3, 0, 2)) < 0.00001f,
            "profile run speed or rotated-facing basis drifted");
        Expect(deadZone.Accepted && !deadZone.Moved &&
               deadZone.Mode == KotorLocomotionMode.Idle,
            "XR-style radial dead zone produced movement");
    }

    private static void KotorMovementRejectsClosedDoor()
    {
        var navigation = new[]
        {
            new KotorNavigationTriangle(
                new System.Numerics.Vector3(-10, -10, 0),
                new System.Numerics.Vector3(10, -10, 0),
                new System.Numerics.Vector3(0, 10, 0))
        };
        var simulation = new KotorMovementSimulation(
            navigation, new KotorMovementConfiguration(2, 6));
        var start = System.Numerics.Vector3.Zero;
        var closed = simulation.Step(
            start, 0, new KotorMovementIntent(0, 1, false), 0.5f,
            [new KotorDoorObstacle(new System.Numerics.Vector3(0, 1, 0), false)]);
        var open = simulation.Step(
            start, 0, new KotorMovementIntent(0, 1, false), 0.5f,
            [new KotorDoorObstacle(new System.Numerics.Vector3(0, 1, 0), true)]);

        Expect(!closed.Accepted && !closed.Moved && closed.Position == start,
            "closed authored door did not block movement");
        Expect(open.Accepted && open.Moved,
            "open authored door still blocked movement");
    }

    private static void MaterializeMarkers(string root, GameProfileDescriptor descriptor)
    {
        Directory.CreateDirectory(root);
        foreach (var marker in descriptor.Markers)
        {
            var path = Resolve(root, marker.RelativePath);
            if (marker.Kind == InstallationMarkerKind.Directory)
            {
                Directory.CreateDirectory(path);
                continue;
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllBytes(path, Array.Empty<byte>());
        }
    }

    private static string Resolve(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
