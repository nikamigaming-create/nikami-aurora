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
            KotorGameplayOwnsOpeningState();
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

    private static void KotorGameplayOwnsOpeningState()
    {
        var contracts = new[]
        {
            new KotorScriptContract(
                "k_pend_chest02",
                KotorScriptContractKind.PlotExperienceIfPlayerExperience,
                new string('A', 64),
                985,
                RequiredPlayerExperience: 0,
                PlotLabel: "end_tutorial",
                PlotPercentage: 5,
                PlotBaseExperience: 1000,
                AwardedExperience: 50),
            new KotorScriptContract(
                "k_pend_door1xp",
                KotorScriptContractKind.PlotExperienceIfPlayerExperience,
                new string('B', 64),
                753,
                RequiredPlayerExperience: 50,
                PlotLabel: "end_tutorial",
                PlotPercentage: 10,
                PlotBaseExperience: 1000,
                AwardedExperience: 100),
            new KotorScriptContract(
                "k_pend_traskdl40",
                KotorScriptContractKind.DialogueOpenDoor,
                new string('C', 64),
                25,
                DoorTag: "end_door01",
                PauseConversation: true,
                MoveTargetTag: "",
                MoveRun: true,
                MoveRange: 1.0f,
                ResumeConversation: true),
            new KotorScriptContract(
                "k_pend_trig02",
                KotorScriptContractKind.TriggerDialogue,
                "99C7AF5868DAEADD96C6027BF7912B6904855990308BAAFFFD6DFBF732AB67BA",
                991,
                TriggerDialogue: new KotorTriggerDialogueBehavior(
                    "end_trig02",
                    "END_TRASK_DLG",
                    10,
                    "end_trask",
                    50,
                    0.5f,
                    0.1f,
                    "end_trask01",
                    8,
                    "28CC82593A0133962B6D2AEC0BA1C1C6B0182918756ECDE533B9112D23F3029A",
                    1301,
                    "FCAA7779E5DA5D86C570ECDF6EB0AE488B571CA308816A955D288E5A659F38EB",
                    763)),
            new KotorScriptContract(
                "k_pend_cadlg_inc",
                KotorScriptContractKind.GlobalNumberAdd,
                "9A3AE15D07F4A1B81A2774553CAA7271403A73829AE76E8CF40518C880A5E360",
                14,
                GlobalName: "END_CARTH_DLG",
                GlobalValue: 1),
            new KotorScriptContract(
                "k_pend_traskdl47",
                KotorScriptContractKind.GlobalNumberSet,
                "66E79D8A179FC49721AC367822B393287E4E60034FD0F506767E6CE70E8C7D09",
                751,
                GlobalName: "END_TRASK_DLG",
                GlobalValue: 11),
            new KotorScriptContract(
                "k_pend_map",
                KotorScriptContractKind.RevealMap,
                "3AE3A04CBA861141A7F12D729DFED129442FDEB424CEEC253EB37B2C4E30DD2A",
                8)
        };
        const string baseItemsSha256 =
            "E9D031FAF0A5D3D4E9CCF33AEE5233FDA8F781A58B30FA722E7CF12B78C85C95";
        var medpac = new KotorItemDefinition(
            "g_i_medeqpmnt01", "Medpac", "g_i_medeqpmnt01",
            "A6449C3EA78042B3E0B09440EAFEAA209C5AA207DE0AFA0CFBCC9296583D9972",
            baseItemsSha256,
            55, 0, 1, 2, 0, 0, 0, "I_MedEqpmnt", 0, "I_Null", "ii_device");
        var clothing = new KotorItemDefinition(
            "g_a_clothes01", "Clothing", "G_A_CLOTHES01",
            "FC8AB4485644BEC2FAE71C99BBD8853170C1A5D739953B62EB95266173443CF1",
            baseItemsSha256,
            85, 0, 1, 0, 2, 1, 0x00002, "a_cloths", 1, "I_Null", "ia_armor");
        var shortSword = new KotorItemDefinition(
            "g_w_shortswrd01", "Short Sword", "G_w_Shortswrd01",
            "9EC88EBA45CB0ED430483362121672F48CDD9C541ADFE4CF7442F76C14BFD652",
            baseItemsSha256,
            4, 0, 1, 1, 0, 0, 0x00030, "w_Shortswrd", 0,
            "w_Shortswrd_001", "iw_sword");
        var simulation = new KotorGameplaySimulation(
            contracts,
            [
                new KotorDoorDefinition("door:0000", "end_door01", "k_pend_door1xp"),
                new KotorDoorDefinition("door:0001", "end_door01", null)
            ],
            [new KotorPlaceableDefinition(
                "placeable:0000", "end_locker01", "k_pend_chest02",
                [
                    new KotorItemStack(medpac, 2, false, false),
                    new KotorItemStack(clothing, 1, false, false),
                    new KotorItemStack(shortSword, 1, false, false)
                ])],
            triggers: [new KotorTriggerDefinition(
                "trigger:0000",
                "end_trig02",
                [
                    new System.Numerics.Vector3(4, -1, 0),
                    new System.Numerics.Vector3(5, -1, 0),
                    new System.Numerics.Vector3(5, 1, 0),
                    new System.Numerics.Vector3(4, 1, 0)
                ],
                "k_pend_trig02")]);

        var locker = simulation.UsePlaceable("PLACEABLE:0000");
        Expect(locker.Before.PlayerExperience == 0 && locker.After.PlayerExperience == 50,
            "profile-owned locker transition did not award 0->50 XP");
        Expect(locker.After.PlaceableStates["placeable:0000"],
            "profile-owned locker state was not persisted");
        Expect(locker.Events.OfType<KotorExperienceAwarded>().Single().Awarded == 50,
            "locker transition did not expose its XP presentation event");
        Expect(locker.After.PlayerInventory["g_i_medeqpmnt01"] == 2 &&
               locker.After.PlayerInventory["g_a_clothes01"] == 1 &&
               locker.After.PlayerInventory["g_w_shortswrd01"] == 1,
            "authored footlocker items were not transferred exactly once");
        Expect(locker.Events.OfType<KotorItemsTransferred>().Single().Items
                   .Sum(stack => stack.Quantity) == 4,
            "footlocker transition did not expose its item presentation event");

        var repeatedLocker = simulation.UsePlaceable("placeable:0000");
        Expect(repeatedLocker.After.PlayerExperience == 50 &&
               repeatedLocker.Events.Single() is KotorPlaceableAlreadyOpened,
            "repeated locker interaction was not idempotent");
        Expect(repeatedLocker.After.PlayerInventory["g_i_medeqpmnt01"] == 2,
            "repeated locker interaction duplicated its inventory");

        var fullHealthUse = simulation.UseMedpac("g_i_medeqpmnt01");
        Expect(fullHealthUse.Events.Count == 0 &&
               fullHealthUse.After.PlayerInventory["g_i_medeqpmnt01"] == 2 &&
               fullHealthUse.After.PlayerCurrentVitality == 20,
            "full-health medpac use consumed an item or changed vitality");

        var healingSimulation = new KotorGameplaySimulation(
            [],
            [],
            [new KotorPlaceableDefinition(
                "placeable:healing", "healing_locker", null,
                [
                    new KotorItemStack(medpac, 2, false, false),
                    new KotorItemStack(clothing, 1, false, false)
                ])],
            initialCurrentVitality: 5,
            initialMaximumVitality: 20,
            initialDefense: 10,
            initialCredits: 0);
        healingSimulation.UsePlaceable("placeable:healing");
        var healed = healingSimulation.UseMedpac(
            "g_i_medeqpmnt01", wisdomModifier: 1, treatInjurySkill: 2);
        var used = healed.Events.OfType<KotorItemUsed>().Single();
        Expect(healed.After.PlayerCurrentVitality == 18 &&
               healed.After.PlayerInventory["g_i_medeqpmnt01"] == 1 &&
               used.QuantityBefore == 2 && used.QuantityAfter == 1 &&
               used.VitalityBefore == 5 && used.VitalityAfter == 18,
            "medpac use did not apply the 10 + WIS + Treat Injury contract");
        var nonMedicalRejected = false;
        try
        {
            _ = healingSimulation.UseMedpac("g_a_clothes01");
        }
        catch (InvalidOperationException)
        {
            nonMedicalRejected = true;
        }
        Expect(nonMedicalRejected &&
               healingSimulation.CaptureSnapshot().PlayerInventory["g_a_clothes01"] == 1,
            "inventory Use Item accepted or consumed non-medical equipment");

        var invalidEquipRejected = false;
        try
        {
            _ = simulation.EquipItems([
                new KotorEquipRequest("g_i_medeqpmnt01", KotorEquipmentSlot.Armor)
            ]);
        }
        catch (InvalidOperationException)
        {
            invalidEquipRejected = true;
        }
        Expect(invalidEquipRejected &&
               simulation.CaptureSnapshot().PlayerInventory["g_i_medeqpmnt01"] == 2,
            "invalid-slot equipment request mutated profile inventory");

        var equipped = simulation.EquipItems([
            new KotorEquipRequest("g_a_clothes01", KotorEquipmentSlot.Armor),
            new KotorEquipRequest("g_w_shortswrd01", KotorEquipmentSlot.RightHand)
        ]);
        Expect(equipped.After.Equipment[KotorEquipmentSlot.Armor] == "g_a_clothes01" &&
               equipped.After.Equipment[KotorEquipmentSlot.RightHand] == "g_w_shortswrd01",
            "opening clothing and short sword did not enter their authored equipment slots");
        Expect(!equipped.After.PlayerInventory.ContainsKey("g_a_clothes01") &&
               !equipped.After.PlayerInventory.ContainsKey("g_w_shortswrd01") &&
               equipped.After.PlayerInventory["g_i_medeqpmnt01"] == 2,
            "equipped items were not separated from unequipped inventory");
        Expect(equipped.Events.OfType<KotorEquipmentChanged>().Count() == 2,
            "equipment transaction did not expose both presentation events");

        var repeatedEquip = simulation.EquipItems([
            new KotorEquipRequest("g_a_clothes01", KotorEquipmentSlot.Armor),
            new KotorEquipRequest("g_w_shortswrd01", KotorEquipmentSlot.RightHand)
        ]);
        Expect(repeatedEquip.Events.Count == 0 &&
               repeatedEquip.After.Equipment.Count == 2,
            "repeated equipment transaction was not idempotent");

        var corridorTrigger = simulation.UpdateTriggers(
            new System.Numerics.Vector3(0, 0, 0),
            new System.Numerics.Vector3(6, 0, 0));
        Expect(corridorTrigger.After.TriggerStates["trigger:0000"],
            "profile did not persist the crossed corridor trigger");
        Expect(corridorTrigger.After.GlobalNumbers["END_TRASK_DLG"] == 10,
            "corridor trigger did not set its validated dialogue global");
        var dialogueRequest = corridorTrigger.Events.OfType<KotorDialogueRequested>().Single();
        Expect(dialogueRequest.ActorTag == "end_trask" &&
               dialogueRequest.Conversation == "end_trask01" &&
               dialogueRequest.StarterIndex == 8 && dialogueRequest.UserEvent == 50 &&
               Math.Abs(dialogueRequest.InputLockSeconds - 0.5f) < 0.00001f &&
               Math.Abs(dialogueRequest.DelaySeconds - 0.1f) < 0.00001f,
            "corridor trigger dialogue request drifted from the bytecode contract");
        var repeatedTrigger = simulation.UpdateTriggers(
            new System.Numerics.Vector3(6, 0, 0),
            new System.Numerics.Vector3(4.5f, 0, 0));
        Expect(repeatedTrigger.Events.Count == 0,
            "one-shot corridor trigger fired more than once");

        var carthLine = simulation.ExecuteScript("k_pend_cadlg_inc");
        Expect(carthLine.After.GlobalNumbers["END_CARTH_DLG"] == 1,
            "Carth transmission did not increment its source global");
        var traskResponse = simulation.ExecuteScript("k_pend_traskdl47");
        Expect(traskResponse.After.GlobalNumbers["END_TRASK_DLG"] == 11,
            "Trask response did not advance its source global");
        var unavailableCrossModuleScript = simulation.ExecuteScript("k_pend_carth11");
        Expect(unavailableCrossModuleScript.Events.OfType<KotorScriptUnsupported>().Single().Resref ==
               "k_pend_carth11" &&
               unavailableCrossModuleScript.After.GlobalNumbers["END_TRASK_DLG"] == 11,
            "module-scoped dialogue execution imported a cross-module script");
        var revealedMap = simulation.ExecuteScript("k_pend_map");
        Expect(revealedMap.After.MapRevealed &&
               revealedMap.Events.OfType<KotorMapRevealed>().Single().After,
            "journal line did not reveal the module map");

        var dialogue = simulation.ExecuteScript("k_pend_traskdl40");
        Expect(dialogue.Before.PlayerExperience == 50 && dialogue.After.PlayerExperience == 150,
            "profile-owned dialogue/door chain did not award 50->150 XP");
        Expect(dialogue.After.DoorStates["door:0000"],
            "dialogue contract did not persist the authored door-open state");
        Expect(!dialogue.After.DoorStates["door:0001"],
            "duplicate-tag door placements incorrectly shared state");
        Expect(dialogue.Events.OfType<KotorDoorStateChanged>().Single().Open,
            "dialogue transition did not expose its door presentation event");
        Expect(dialogue.Events.OfType<KotorExperienceAwarded>().Single().Awarded == 100,
            "door OnOpen contract did not expose its XP presentation event");
        Expect(dialogue.Events.OfType<KotorScriptExecuted>().Single().Contract.Resref ==
               "k_pend_traskdl40",
            "dialogue contract execution was not reported");

        var closeDoor = simulation.ToggleDoor("door:0000");
        Expect(!closeDoor.After.DoorStates["door:0000"] &&
               closeDoor.After.PlayerExperience == 150,
            "direct door toggle did not preserve the profile-owned story state");
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
