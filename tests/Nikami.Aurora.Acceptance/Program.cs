using System.Security.Cryptography;
using Nikami.Aurora.Core;
using Nikami.Aurora.Profiles.DragonAgeOrigins;
using Nikami.Aurora.Profiles.Kotor;
using Nikami.Aurora.Profiles.Kotor2;

namespace Nikami.Aurora.Acceptance;

internal static partial class Program
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
            Kotor2ProbeAcceptsCompleteSyntheticInstall(suiteRoot);
            passed++;
            Kotor2ProfileDoesNotAcceptKotorInstall(suiteRoot);
            passed++;
            DragonAgeProfileRemainsIndependent(suiteRoot);
            passed++;
            DragonAgeCharacterCreationCatalogCoversEveryUiSelection();
            passed++;
            DragonAgeOriginCatalogOwnsEveryRetailRoute();
            passed++;
            DragonAgeExperienceTableOwnsLevelBoundaries();
            passed++;
            DragonAgeCreaturePropertyOwnsExperienceMutation();
            passed++;
            DragonAgeNcsDecoderOwnsInstalledInstructionLayout();
            passed++;
            DragonAgeNcsExecutorPreservesActionArgumentOrder();
            passed++;
            DragonAgeCharacterCreationReadinessSeparatesLegacyAndFresh();
            passed++;
            DragonAgeCharacterCreationClearsUnsupportedSelection();
            passed++;
            DragonAgeCharacterGlbAssemblerPreservesContracts(suiteRoot);
            passed++;
            DragonAgeCharacterGlbAssemblerFailsClosed(suiteRoot);
            passed++;
            DragonAgeCharacterModelHierarchyDecoderIsSourceBound();
            passed++;
            DragonAgeMshDecoderIsSourceBound();
            passed++;
            DragonAgeMshDecoderFailsClosed();
            passed++;
            DragonAgeCityElfEffectCatalogIsSourceBound();
            passed++;
            DragonAgeCityElfEffectCatalogRejectsUnknownDefinition();
            passed++;
            DragonAgeGenericEffectGraphDecoderIsSourceBound();
            passed++;
            DragonAgeEffectReadabilityGateFailsClosed();
            passed++;
            DragonAgeGenericEffectGraphDecoderFailsClosed();
            passed++;
            DragonAgeNavigationGridDecoderIsSourceBound();
            passed++;
            DragonAgeCoordinateBasisPreservesAsymmetricRotation();
            passed++;
            DragonAgeRenderPolicyIgnoresLayoutIdentity();
            passed++;
            DragonAgePbrCoverageRequiresExactIdentityBoundSurfaces();
            passed++;
            EnhancedRenderingQualityIsApplicationWide();
            passed++;
            SourceRenderingQualityRemainsSeparateFromEnhancement();
            passed++;
            RenderingQualityRejectsSceneKeyedSelection();
            passed++;
            RegistryRejectsDuplicateProfiles();
            passed++;
            MarkerRejectsTraversal();
            passed++;
            KotorMovementUsesProfileSpeedsAndFacing();
            passed++;
            KotorMovementRejectsClosedDoor();
            passed++;
            KotorMovementRejectsDegenerateNavigation();
            passed++;
            KotorGameplayOwnsOpeningState();
            passed++;
            KotorCombatOwnsDamageDeathAndRetailExperience();
            passed++;
            KotorInventoryProjectionStaysLinear();
            passed++;
            KotorEnvironmentMaterialPolicyPreservesSourceContract();
            passed++;
            KotorLightmapTransferKeepsSourceAndEnhancedDistinct();
            passed++;
            CinematicFramingAcceptsVisibleSubject();
            passed++;
            CinematicFramingRejectsWallOcclusion();
            passed++;
            KotorDialogueCameraKeepsSpeakerObjectivelyFramed();
            passed++;
            KotorDialogueCameraRejectsCoincidentParticipants();
            passed++;
            KotorFirstEncounterCameraBeatsRemainSourceBound();
            passed++;
            KotorCameraCollisionSelectsOnlySourceOpaqueSurfaces();
            passed++;
            KotorGenericModulePresentationRemainsStoryNeutral();
            passed++;
            KotorGenericVisualInventoryFailsClosed();
            passed++;
            KotorCreaturePresentationRequiresEveryModelAndWeaponEffect();
            KotorCreatureEffectsPreserveBurstOverlapAndAtlasBounds();
            passed++;
            KotorGlobalPbrCoverageRequiresEveryEligibleSurface();
            passed++;
            KotorRigIdentityRecognizesSourceBodyFamilies();
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

    private static void KotorCombatOwnsDamageDeathAndRetailExperience()
    {
        var experience = new KotorCombatExperienceTable(
            new string('A', 64),
            [new KotorCombatExperienceRow(1,
                Enumerable.Range(0, 21).Select(challenge => challenge == 1 ? 75 : 0).ToArray())]);
        var sword = new KotorCombatWeaponDefinition(
            "g_w_shortswrd01", 0, 1, 2, false,
            [new KotorDamageComponent(1, 6, 0, 2, true)]);
        var oneDamageBlaster = new KotorCombatWeaponDefinition(
            "end_1damblast", -5, 2, 2, true,
            [new KotorDamageComponent(0, 0, 1, 12)]);
        var combat = new KotorCombatSimulation(
        [
            new KotorCombatantDefinition(
                "player", 0, 12, 12, 12, 4, 0, false, false, sword),
            new KotorCombatantDefinition(
                "end_sith", 1, 10, 10, 6, -3, 1, false, true, oneDamageBlaster)
        ], experience);

        combat.QueueAttack("player", "end_sith");
        var kill = combat.ResolveNextAttack(1, 20, [6]);
        var resolved = kill.Events.OfType<KotorAttackResolved>().Single();
        Expect(resolved.Hit && resolved.Critical && resolved.Damage == 12 &&
               resolved.HitPointsAfter == 0 && kill.AwardedExperience == 75 &&
               kill.Events.OfType<KotorCombatantDied>().Single().ExperienceReward == 75,
            "KOTOR combat death or retail CR1 experience drifted");

        var counter = new KotorCombatSimulation(
        [
            new KotorCombatantDefinition(
                "player", 0, 12, 12, 12, 4, 0, false, false, sword),
            new KotorCombatantDefinition(
                "end_sith", 1, 1, 1, 6, -3, 1, false, true, oneDamageBlaster)
        ], experience);
        counter.QueueAttack("end_sith", "player");
        var criticalBonus = counter.ResolveNextAttack(1, 20, []);
        Expect(criticalBonus.Events.OfType<KotorAttackResolved>().Single().Damage == 1,
            "KOTOR critical incorrectly multiplied item-property bonus damage");

        var tslRewards = Enumerable.Range(0, 51).Select(value => value * 25).ToArray();
        var tslExperience = new KotorCombatExperienceTable(
            new string('B', 64),
            [new KotorCombatExperienceRow(1, tslRewards)]);
        Expect(tslExperience.RewardFor(1, 50) == 1250,
            "KOTOR II combat XP challenge range collapsed to KOTOR I");
    }

    private static void DragonAgeOriginCatalogOwnsEveryRetailRoute()
    {
        var routes = DragonAgeOriginsOriginCatalog.Routes;
        Expect(routes.Count == 6, "DAO origin route count drifted");
        Expect(routes.Select(route => route.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6,
            "DAO origin route ids are not unique");
        Expect(routes.All(route => route.AreaId.Length > 0 && route.Archive.Length > 0 &&
                                   route.Waypoint.Length > 0 && route.OpeningCutscene.Length > 0),
            "DAO origin route lost a source start identity");
        Expect(DragonAgeOriginsOriginCatalog.For("human", "warrior")
                   .Select(route => route.Id).SequenceEqual(["human-noble"]),
            "human martial origin selection drifted");
        Expect(DragonAgeOriginsOriginCatalog.For("elf", "rogue")
                   .Select(route => route.Id).SequenceEqual(["city-elf", "dalish-elf"]),
            "elf martial origin selection drifted");
        Expect(DragonAgeOriginsOriginCatalog.For("dwarf", "warrior")
                   .Select(route => route.Id).SequenceEqual(["dwarf-commoner", "dwarf-noble"]),
            "dwarf martial origin selection drifted");
        Expect(DragonAgeOriginsOriginCatalog.For("human", "mage").Single().Id == "circle-mage" &&
               DragonAgeOriginsOriginCatalog.For("elf", "mage").Single().Id == "circle-mage" &&
               DragonAgeOriginsOriginCatalog.For("dwarf", "mage").Count == 0,
            "circle mage race selection drifted");
        Expect(DragonAgeOriginsOriginCatalog.Resolve("CITY-ELF")?.OpeningDialogue ==
               "bec110cr_shianni", "city elf opening dialogue identity drifted");
    }

    private static void DragonAgeExperienceTableOwnsLevelBoundaries()
    {
        var table = new DragonAgeOriginsExperienceTable(
        [
            new(0, 0), new(1, 1), new(2, 2001), new(3, 4501)
        ]);
        Expect(table.ResolveLevel(0) == 0 && table.ResolveLevel(1) == 1 &&
               table.ResolveLevel(2000) == 1 && table.ResolveLevel(2001) == 2,
            "DAO level resolution drifted from exptable semantics");
        Expect(table.MinimumExperienceFor(2) == 2001,
            "DAO level-two experience boundary drifted");
    }

    private static void DragonAgeCreaturePropertyOwnsExperienceMutation()
    {
        Expect(DragonAgeOriginsCreatureProperty.TryApplyExperience(
                   DragonAgeOriginsCreatureProperty.SetAction,
                   DragonAgeOriginsCreatureProperty.Experience, 2001,
                   DragonAgeOriginsCreatureProperty.BaseValue, 50,
                   out var setExperience, out _) && setExperience == 2001,
            "DAO SetCreatureProperty did not set source experience property");
        Expect(DragonAgeOriginsCreatureProperty.TryApplyExperience(
                   DragonAgeOriginsCreatureProperty.UpdateAction,
                   DragonAgeOriginsCreatureProperty.Experience, 1951,
                   DragonAgeOriginsCreatureProperty.CurrentValue, 50,
                   out var updatedExperience, out _) && updatedExperience == 2001,
            "DAO UpdateCreatureProperty did not update source experience property");
        Expect(!DragonAgeOriginsCreatureProperty.TryApplyExperience(
                   DragonAgeOriginsCreatureProperty.GetAction,
                   DragonAgeOriginsCreatureProperty.Experience, 0,
                   DragonAgeOriginsCreatureProperty.TotalValue, 50,
                   out _, out var actionReason) &&
               actionReason == "creature-property-action-unsupported",
            "DAO read action was accepted as an experience mutation");
        Expect(!DragonAgeOriginsCreatureProperty.TryApplyExperience(
                   DragonAgeOriginsCreatureProperty.SetAction, 18, 2001,
                   DragonAgeOriginsCreatureProperty.BaseValue, 50,
                   out _, out var propertyReason) &&
               propertyReason == "creature-property-not-experience",
            "DAO non-experience property leaked into shared progression");
    }

    private static void DragonAgeNcsDecoderOwnsInstalledInstructionLayout()
    {
        byte[] script =
        [
            0x4e, 0x43, 0x53, 0x20, 0x56, 0x31, 0x2e, 0x30, 0x42,
            0x00, 0x00, 0x00, 0x1a,
            0x04, 0x03, 0x00, 0x00, 0x00, 0x13,
            0x05, 0x00, 0x02, 0xe4, 0x04,
            0x20, 0x00
        ];
        var decoded = DragonAgeOriginsNcsDecoder.Decode(script);
        Expect(decoded.Succeeded && decoded.Instructions.Count == 3,
            "DAO NCS instruction stream did not decode");
        Expect(decoded.Instructions[0].Address == 13 &&
               decoded.Instructions[0].Opcode == 0x04 &&
               Convert.ToInt32(decoded.Instructions[0].Arguments.Single()) == 19,
            "DAO NCS big-endian constant layout drifted");
        Expect(decoded.Instructions[1].Address == 19 &&
               decoded.Instructions[1].Opcode == 0x05 &&
               Convert.ToInt32(decoded.Instructions[1].Arguments[0]) == 740 &&
               Convert.ToInt32(decoded.Instructions[1].Arguments[1]) == 4,
            "DAO NCS action layout drifted");
        var truncated = DragonAgeOriginsNcsDecoder.Decode(script[..^1]);
        Expect(!truncated.Succeeded && truncated.Error == "ncs-size-mismatch",
            "DAO NCS size mismatch did not fail closed");
    }

    private static void DragonAgeNcsExecutorPreservesActionArgumentOrder()
    {
        byte[] script =
        [
            0x4e, 0x43, 0x53, 0x20, 0x56, 0x31, 0x2e, 0x30, 0x42,
            0x00, 0x00, 0x00, 0x2c,
            0x04, 0x03, 0x00, 0x00, 0x00, 0x02,
            0x04, 0x04, 0x44, 0xfa, 0x20, 0x00,
            0x04, 0x03, 0x00, 0x00, 0x00, 0x13,
            0x04, 0x06, 0x00, 0x00, 0x00, 0x00,
            0x05, 0x00, 0x02, 0xe4, 0x04,
            0x20, 0x00
        ];
        var invoked = false;
        var result = DragonAgeOriginsNcsExecutor.Execute(script, (action, values) =>
        {
            invoked = true;
            Expect(action == DragonAgeOriginsCreatureProperty.SetAction && values.Count == 4,
                "DAO NCS action dispatch identity drifted");
            Expect(Convert.ToInt32(values[0].Value) == 0 &&
                   Convert.ToInt32(values[1].Value) == 19 &&
                   Convert.ToSingle(values[2].Value) == 2001f &&
                   Convert.ToInt32(values[3].Value) == 2,
                "DAO NCS right-to-left action argument order drifted");
            return DragonAgeNcsActionResult.Complete();
        });
        Expect(result.Succeeded && invoked && result.InvokedActions.SequenceEqual([740]),
            "DAO NCS action program did not execute");
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

    private static void Kotor2ProbeAcceptsCompleteSyntheticInstall(string suiteRoot)
    {
        var profile = new Kotor2GameProfile();
        var root = Path.Combine(suiteRoot, "kotor2-complete");
        MaterializeMarkers(root, profile.Descriptor);
        var executableBytes = new byte[] { 0x4d, 0x5a, 0x02, 0x00 };
        File.WriteAllBytes(Resolve(root, profile.Descriptor.ExecutableRelativePath), executableBytes);

        var result = GameInstallProber.Probe(profile, root);
        Expect(result.IsValid, "complete KOTOR II fixture was rejected");
        Expect(result.ProfileId == Kotor2GameProfile.ProfileId,
            "KOTOR II profile identity changed");
        Expect(result.EngineFamily == "Odyssey", "KOTOR II left the Odyssey family");
        Expect(result.ExecutableSha256 == Convert.ToHexString(SHA256.HashData(executableBytes)),
            "KOTOR II executable hash was not source-bound");
    }

    private static void Kotor2ProfileDoesNotAcceptKotorInstall(string suiteRoot)
    {
        var kotor = new KotorGameProfile();
        var root = Path.Combine(suiteRoot, "kotor-is-not-kotor2");
        MaterializeMarkers(root, kotor.Descriptor);

        var result = GameInstallProber.Probe(new Kotor2GameProfile(), root);
        Expect(!result.IsValid, "KOTOR II profile accepted a KOTOR installation");
        Expect(result.Markers.Single(marker => marker.RelativePath == "swkotor2.exe").Present == false,
            "KOTOR II probe did not report its missing executable");
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

    private static void DragonAgeCharacterCreationCatalogCoversEveryUiSelection()
    {
        var appearances = DragonAgeOriginsCharacterCreationCatalog.Appearances;
        Expect(appearances.Count ==
               DragonAgeOriginsCharacterCreationCatalog.ExpectedSelectionCount,
            "DAO character-creation catalog does not cover all 24 UI selections");
        var expectedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var race in new[] { "human", "elf", "dwarf" })
            foreach (var gender in new[] { "female", "male" })
                for (var preset = 1; preset <= 4; preset++)
                    expectedKeys.Add($"{race}:{gender}:preset-{preset}");

        foreach (var appearance in appearances)
        {
            Expect(expectedKeys.Remove(appearance.SelectionKey),
                "DAO character-creation catalog contains a duplicate or unexpected selection: " +
                appearance.SelectionKey);
            var expectedStem = appearance.MorphResource[..^4];
            Expect(IsSha256(appearance.MorphSha256) &&
                   appearance.StandingRelativePath ==
                   $"quickplay-characters/{expectedStem}.glb" &&
                   appearance.BedRelativePath ==
                   $"quickplay-characters/{expectedStem}-bed.glb" &&
                   appearance.ImportManifestRelativePath ==
                   $"quickplay-characters/{expectedStem}.import.json",
                "DAO character-creation selection lost its deterministic source/output join: " +
                appearance.SelectionKey);
            Expect(!appearance.StandingRelativePath.Contains("areas/", StringComparison.Ordinal) &&
                   !appearance.StandingRelativePath.Contains("playable-characters/",
                       StringComparison.Ordinal) &&
                   !appearance.BedRelativePath.Contains("playable-character-bed/",
                       StringComparison.Ordinal),
                "DAO character-creation catalog contains an area-NPC fallback path");
        }
        Expect(expectedKeys.Count == 0,
            "DAO character-creation catalog is missing UI selections: " +
            string.Join(',', expectedKeys));
        Expect(IsSha256(DragonAgeOriginsCharacterCreationCatalog.CatalogContainerSha256) &&
               IsSha256(DragonAgeOriginsCharacterCreationCatalog.CatalogResourceSha256) &&
               IsSha256(DragonAgeOriginsCharacterCreationCatalog.SourceContainerSha256),
            "DAO character-creation source catalog identity is not SHA-256 bound");
        Expect(DragonAgeOriginsCharacterCreationCatalog.Resolve(
                   "qunari", "female", "preset-1") is null,
            "unsupported DAO character race did not fail closed");
    }

    private static void DragonAgeCharacterCreationReadinessSeparatesLegacyAndFresh()
    {
        var appearances = DragonAgeOriginsCharacterCreationCatalog.Appearances;
        var legacyReady = 0;
        var freshReady = 0;
        foreach (var appearance in appearances)
        {
            var readiness = DragonAgeOriginsCharacterCreationCatalog.ClassifyImport(
                appearance,
                manifest: null,
                appearance.LegacyStandingSha256,
                appearance.LegacyBedSha256);
            if (readiness == DragonAgeCharacterImportReadiness.LegacyEvidence)
                legacyReady++;
            if (readiness == DragonAgeCharacterImportReadiness.FreshImport)
                freshReady++;
        }
        Expect(legacyReady == 24 && freshReady == 0,
            $"DAO character readiness tiers drifted: legacy={legacyReady} fresh={freshReady}");

        var selected = appearances.Single(value => value.SelectionKey ==
                                                   "human:female:preset-1");
        const string standingHash =
            "1111111111111111111111111111111111111111111111111111111111111111";
        const string bedHash =
            "2222222222222222222222222222222222222222222222222222222222222222";
        var manifest = new DragonAgeCharacterCreationImportManifest(
            DragonAgeOriginsCharacterCreationCatalog.ManifestSchema,
            DragonAgeOriginsCharacterCreationCatalog.ImporterId,
            selected.SelectionKey,
            DragonAgeOriginsCharacterCreationCatalog.CatalogContainerRelativePath,
            DragonAgeOriginsCharacterCreationCatalog.CatalogContainerSha256,
            DragonAgeOriginsCharacterCreationCatalog.CatalogResource,
            DragonAgeOriginsCharacterCreationCatalog.CatalogResourceSha256,
            DragonAgeOriginsCharacterCreationCatalog.SourceContainerRelativePath,
            DragonAgeOriginsCharacterCreationCatalog.SourceContainerSha256,
            selected.MorphResource,
            selected.MorphSha256,
            selected.StandingRelativePath,
            standingHash,
            selected.BedRelativePath,
            bedHash);
        Expect(DragonAgeOriginsCharacterCreationCatalog.ClassifyImport(
                   selected, manifest, standingHash, bedHash) ==
               DragonAgeCharacterImportReadiness.FreshImport,
            "valid source-bound character import manifest was not fresh-import ready");
        ExpectThrows<InvalidDataException>(() =>
                DragonAgeOriginsCharacterCreationCatalog.ClassifyImport(
                    selected,
                    manifest with { SelectionKey = "elf:female:preset-1" },
                    standingHash,
                    bedHash),
            "character import manifest with a different selection was accepted");
    }

    private static void DragonAgeCharacterCreationClearsUnsupportedSelection()
    {
        var readyAppearance = DragonAgeOriginsCharacterCreationCatalog.Resolve(
            "elf", "female", "preset-1")!;
        var state = DragonAgeOriginsCharacterCreationCatalog.TransitionSelection(
            readyAppearance, DragonAgeCharacterImportReadiness.LegacyEvidence);
        Expect(state.ActorVisible &&
               DragonAgeOriginsCharacterCreationCatalog.CanStart(state, readyAppearance),
            "ready source-bound character selection could not start");

        state = DragonAgeOriginsCharacterCreationCatalog.TransitionSelection(
            DragonAgeOriginsCharacterCreationCatalog.Resolve(
                "human", "female", "preset-1"),
            DragonAgeCharacterImportReadiness.Missing);
        Expect(!state.ActorVisible && state.SelectionKey.Length == 0,
            "switching to a missing character import retained the previous preview actor");
        Expect(!DragonAgeOriginsCharacterCreationCatalog.CanStart(state, readyAppearance),
            "missing character import retained permission to start with the previous actor");
    }

    private static void DragonAgeCityElfEffectCatalogIsSourceBound()
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fxe_fire_cnd_p", "fxe_fire_m_ns_p", "fxe_dirtywater_p",
            "fxe_fire_small_p", "fxe_tree_beam_blur", "fxe_water_ripples"
        };
        var definitions = DragonAgeOriginsEffectCatalog.SupportedDefinitions.ToArray();
        Expect(definitions.Length == expected.Count,
            "City Elf effect definition inventory drifted");
        foreach (var definition in definitions)
        {
            Expect(expected.Remove(definition.ResRef),
                $"unexpected or duplicate City Elf effect definition: {definition.ResRef}");
            Expect(IsSha256(definition.ModelHierarchySha256),
                $"effect MMH identity is invalid: {definition.ResRef}");
            Expect(definition.Emitters.Count > 0,
                $"effect has no source-supported emitter: {definition.ResRef}");
            foreach (var emitter in definition.Emitters)
            {
                Expect(IsSha256(emitter.MaterialSha256) && IsSha256(emitter.TextureSha256),
                    $"effect material/texture identity is invalid: {definition.ResRef}/{emitter.Name}");
                Expect(emitter.Columns > 0 && emitter.Rows > 0 &&
                       emitter.FramesPerSecond >= 0 && float.IsFinite(emitter.FramesPerSecond),
                    $"effect contact-sheet contract is invalid: {definition.ResRef}/{emitter.Name}");
                Expect(emitter.BirthRate > 0 && emitter.Lifetime > 0 &&
                       emitter.BirthRateRange >= 0 && emitter.LifetimeRange >= 0,
                    $"effect timing/range contract is invalid: {definition.ResRef}/{emitter.Name}");
                Expect(emitter.SourceDirection.LengthSquared() > .99f &&
                       emitter.LocalRotation.LengthSquared() > .99f,
                    $"effect basis/direction contract is invalid: {definition.ResRef}/{emitter.Name}");
            }
            Expect(DragonAgeOriginsEffectCatalog.TryResolve(
                       $"models/{definition.ResRef}.glb", out var resolved) &&
                   ReferenceEquals(resolved, definition),
                $"effect route resolution failed: {definition.ResRef}");
        }
        Expect(expected.Count == 0, "City Elf effect inventory is incomplete");
    }

    private static void DragonAgeCityElfEffectCatalogRejectsUnknownDefinition()
    {
        Expect(!DragonAgeOriginsEffectCatalog.TryResolve(
                "models/fxe_unknown_fallback.glb", out _),
            "unknown DAO effect definition did not fail closed");
    }

    private static void DragonAgeCoordinateBasisPreservesAsymmetricRotation()
    {
        var sourceDirection = System.Numerics.Vector3.Normalize(
            new System.Numerics.Vector3(.23f, -.61f, .74f));
        var sourceRotation = System.Numerics.Quaternion.Normalize(
            System.Numerics.Quaternion.CreateFromYawPitchRoll(.71f, -.38f, 1.13f));
        var expected = DragonAgeOriginsCoordinateSystem.Convert(
            System.Numerics.Vector3.Transform(sourceDirection, sourceRotation));
        var actual = System.Numerics.Vector3.Transform(
            DragonAgeOriginsCoordinateSystem.Convert(sourceDirection),
            DragonAgeOriginsCoordinateSystem.Convert(sourceRotation));
        Expect(System.Numerics.Vector3.Distance(expected, actual) < .00001f,
            "DAO emitter local rotation was not conjugated through the coordinate basis");
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void DragonAgeRenderPolicyIgnoresLayoutIdentity()
    {
        var first = DragonAgeOriginsRenderFidelityPolicy.Evaluate(
            "synthetic_desert_l01", "enhanced", "forward_plus", validatedAtmosphere: true);
        var second = DragonAgeOriginsRenderFidelityPolicy.Evaluate(
            "unrelated_station_x9", "enhanced", "forward_plus", validatedAtmosphere: true);
        Expect(first.Layout != second.Layout &&
               first.Tier == second.Tier &&
               first.RenderingMethod == second.RenderingMethod &&
               first.ValidatedAtmosphere == second.ValidatedAtmosphere &&
               first.EnhancedFeatures == second.EnhancedFeatures &&
               first.Status == second.Status,
            "DAO presentation behavior was selected by layout identity");

        var source = DragonAgeOriginsRenderFidelityPolicy.Evaluate(
            "third_arbitrary_layout", "source", "mobile", validatedAtmosphere: false);
        Expect(source.Tier == DragonAgePresentationTier.Source &&
               !source.EnhancedFeatures && source.Status == "unsupported",
            "DAO source tier did not remain source on an arbitrary layout");

        var rejected = false;
        try
        {
            _ = DragonAgeOriginsRenderFidelityPolicy.Evaluate(
                "fourth_arbitrary_layout", "enhanced", "mobile", validatedAtmosphere: true);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Expect(rejected, "DAO enhanced presentation accepted the mobile renderer");
    }

    private static void DragonAgePbrCoverageRequiresExactIdentityBoundSurfaces()
    {
        DragonAgeOriginsRenderFidelityPolicy.RequirePbrCoverage(
            new DragonAgePbrCoverage(
                RenderableSurfaces: 81278,
                BoundSurfaces: 81278,
                IdentityReadySurfaces: 81278,
                PbrReadySurfaces: 81278));
        Expect(DragonAgeOriginsRenderFidelityPolicy.RequirePbrContract(
                   "kind=installed-gltf-pbr;pbr_status=ready;mao_status=unsupported") ==
               DragonAgePbrContractKind.ImportedGltf &&
               DragonAgeOriginsRenderFidelityPolicy.RequirePbrContract(
                   "kind=installed-terrain-contract;pbr_status=source-shader") ==
               DragonAgePbrContractKind.SourceShader &&
               DragonAgeOriginsRenderFidelityPolicy.RequirePbrContract(
                   "kind=installed-water-contract;pbr_status=enhanced-shader") ==
               DragonAgePbrContractKind.EnhancedShader,
            "DAO known PBR identity statuses did not remain distinct");

        ExpectThrows<InvalidDataException>(
            () => DragonAgeOriginsRenderFidelityPolicy.RequirePbrCoverage(
                new DragonAgePbrCoverage(81278, 81278, 81278, 81277)),
            "DAO global census accepted one non-PBR visible surface");
        ExpectThrows<InvalidDataException>(
            () => DragonAgeOriginsRenderFidelityPolicy.RequirePbrContract(
                "pbr_status=ready-but-partial"),
            "DAO PBR identity accepted a prefix-compatible partial status");
        ExpectThrows<InvalidDataException>(
            () => DragonAgeOriginsRenderFidelityPolicy.RequirePbrContract(
                "pbr_status=ready;pbr_status=enhanced-shader"),
            "DAO PBR identity accepted duplicate status tokens");
        ExpectThrows<InvalidDataException>(
            () => DragonAgeOriginsRenderFidelityPolicy.RequirePbrContract(
                "mao_status=unsupported"),
            "DAO material identity accepted an absent PBR status");
    }

    private static void EnhancedRenderingQualityIsApplicationWide()
    {
        var decision = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
            RenderingPresentationTier.Enhanced,
            RenderingBackend.ForwardPlus,
            RenderingSelectionScope.Application,
            SelectionKey: null,
            RenderingQualityPolicy.AllCapabilities,
            SourceAuthorizedRenderFeature.Reflections |
            SourceAuthorizedRenderFeature.IndirectLighting |
            SourceAuthorizedRenderFeature.Volumetrics));

        Expect(decision.EnabledEnhancedCapabilities == RenderingQualityPolicy.AllCapabilities &&
               decision.Reflections is
               {
                   Enabled: true,
                   Status: ConditionalRenderFeatureStatus.Enabled
               } &&
               decision.Sdfgi is
               {
                   Enabled: true,
                   Status: ConditionalRenderFeatureStatus.Enabled
               } &&
               decision.Volumetrics is
               {
                   Enabled: true,
                   Status: ConditionalRenderFeatureStatus.Enabled
               },
            "application-wide enhanced rendering did not enable the full authorized capability set");
        var values = decision.QualityValues;
        Expect(values is
        {
            TemporalAntialiasing: false,
            MultisampleAntialiasingSamples: 4,
            Debanding: true,
            AnisotropicFilteringSamples: 16,
            TrilinearMipmapFiltering: true,
            DirectionalShadowMapSize: 8192,
            PositionalShadowAtlasSize: 8192,
            SoftShadowFilterQuality: 5,
            SsaoQuality: 4,
            SsaoHalfSize: false,
            SsaoAdaptiveTarget: 1.0f,
            SsilQuality: 4,
            SsilHalfSize: false,
            SsilAdaptiveTarget: 1.0f,
            ScreenSpaceReflectionHalfSize: false,
            GiHalfResolution: false,
            SdfgiProbeRayCount: 5,
            SdfgiFramesToConverge: 5,
            SdfgiFramesToUpdateLights: 0,
            VolumetricFogFilter: 2
        },
            "full-blast quality values drifted");
        Expect(decision.EvidenceIntent == RenderingQualityPolicy.EnhancedEvidenceIntent &&
               decision.ParityClaim == RenderingQualityPolicy.NoParityClaim &&
               decision.ToTelemetryMarker().Contains(
                   "scope=application tier=enhanced backend=forward_plus", StringComparison.Ordinal) &&
               decision.ToTelemetryMarker().Contains(
                   "reflection_policy=source_bound_probes_maps_ssr", StringComparison.Ordinal) &&
               decision.ToTelemetryMarker().Contains(
                   "sdfgi_gate=enabled volumetrics=1 volumetrics_gate=enabled",
                   StringComparison.Ordinal) &&
               decision.ToTelemetryMarker().EndsWith("parity_claim=none", StringComparison.Ordinal),
            "enhanced rendering telemetry made an unearned parity claim");

        var gated = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
            RenderingPresentationTier.Enhanced,
            RenderingBackend.ForwardPlus,
            RenderingSelectionScope.Application,
            SelectionKey: string.Empty,
            RenderingQualityPolicy.AllCapabilities,
            SourceAuthorizedRenderFeature.None));
        Expect(gated.Reflections is
        {
            Enabled: false,
            Status: ConditionalRenderFeatureStatus.SourceEvidenceRequired
        } &&
               gated.Volumetrics is
               {
                   Enabled: false,
                   Status: ConditionalRenderFeatureStatus.SourceEvidenceRequired
               } &&
               gated.Sdfgi is
               {
                   Enabled: false,
                   Status: ConditionalRenderFeatureStatus.SourceEvidenceRequired
               },
            "reflection, SDFGI, or volumetric enhancement bypassed its source-evidence gate");

        var unavailable = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
            RenderingPresentationTier.Enhanced,
            RenderingBackend.ForwardPlus,
            RenderingSelectionScope.Application,
            SelectionKey: null,
            RenderingQualityPolicy.RequiredEnhancedCapabilities,
            RenderingQualityPolicy.AllSourceAuthorizedFeatures));
        Expect(unavailable.Reflections.Status ==
                   ConditionalRenderFeatureStatus.CapabilityUnavailable &&
               unavailable.Sdfgi.Status ==
                   ConditionalRenderFeatureStatus.CapabilityUnavailable &&
               unavailable.Volumetrics.Status ==
                   ConditionalRenderFeatureStatus.CapabilityUnavailable,
            "unavailable optional rendering capabilities did not fail closed");

        var missingCapabilityRejected = false;
        try
        {
            _ = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
                RenderingPresentationTier.Enhanced,
                RenderingBackend.ForwardPlus,
                RenderingSelectionScope.Application,
                SelectionKey: null,
                RenderingQualityPolicy.RequiredEnhancedCapabilities &
                ~EnhancedRenderingCapability.ScreenSpaceIndirectLighting,
                SourceAuthorizedRenderFeature.None));
        }
        catch (InvalidDataException)
        {
            missingCapabilityRejected = true;
        }
        Expect(missingCapabilityRejected,
            "enhanced rendering accepted an incomplete mandatory capability set");

        var mobileRejected = false;
        try
        {
            _ = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
                RenderingPresentationTier.Enhanced,
                RenderingBackend.Mobile,
                RenderingSelectionScope.Application,
                SelectionKey: null,
                RenderingQualityPolicy.AllCapabilities,
                RenderingQualityPolicy.AllSourceAuthorizedFeatures));
        }
        catch (InvalidDataException)
        {
            mobileRejected = true;
        }
        Expect(mobileRejected,
            "enhanced rendering accepted the mobile backend");
    }

    private static void SourceRenderingQualityRemainsSeparateFromEnhancement()
    {
        var decision = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
            RenderingPresentationTier.Source,
            RenderingBackend.Compatibility,
            RenderingSelectionScope.Application,
            SelectionKey: null,
            RenderingQualityPolicy.AllCapabilities,
            SourceAuthorizedRenderFeature.Reflections |
            SourceAuthorizedRenderFeature.Volumetrics));

        Expect(decision.EnabledEnhancedCapabilities == EnhancedRenderingCapability.None &&
               decision.Reflections.Status == ConditionalRenderFeatureStatus.OwnedBySourceTier &&
               decision.Volumetrics.Status == ConditionalRenderFeatureStatus.OwnedBySourceTier &&
               decision.QualityValues is null &&
               decision.EvidenceIntent == RenderingQualityPolicy.SourceComparisonEvidenceIntent &&
               decision.ParityClaim == RenderingQualityPolicy.NoParityClaim,
            "source comparison inherited enhanced rendering or an automatic parity claim");

        var forwardPlusSource = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
            RenderingPresentationTier.Source,
            RenderingBackend.ForwardPlus,
            RenderingSelectionScope.Application,
            SelectionKey: null,
            RenderingQualityPolicy.AllCapabilities,
            RenderingQualityPolicy.AllSourceAuthorizedFeatures));
        Expect(forwardPlusSource.EnabledEnhancedCapabilities ==
                   EnhancedRenderingCapability.None &&
               ReferenceEquals(forwardPlusSource.QualityValues,
                   RenderingQualityPolicy.FullBlastValues) &&
               forwardPlusSource.ToTelemetryMarker().Contains(
                   "tier=source backend=forward_plus agx=0 shadows=1 shadow_size=8192 " +
                   "anisotropy=1 anisotropy_samples=16 ssao=0 ssil=0 msaa=4x taa=0 " +
                   "debanding=1",
                   StringComparison.Ordinal) &&
               forwardPlusSource.ToTelemetryMarker().EndsWith(
                   "parity_claim=none", StringComparison.Ordinal),
            "source Forward+ telemetry hid global sampling budgets or enabled enhanced lighting");
    }

    private static void RenderingQualityRejectsSceneKeyedSelection()
    {
        foreach (var (scope, key) in new[]
                 {
                     (RenderingSelectionScope.Profile, "synthetic-profile"),
                     (RenderingSelectionScope.Area, "synthetic-area"),
                     (RenderingSelectionScope.Module, "synthetic-module"),
                     (RenderingSelectionScope.Layout, "synthetic-layout")
                 })
        {
            var rejected = false;
            try
            {
                _ = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
                    RenderingPresentationTier.Enhanced,
                    RenderingBackend.ForwardPlus,
                    scope,
                    key,
                    RenderingQualityPolicy.AllCapabilities,
                    SourceAuthorizedRenderFeature.None));
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Expect(rejected, $"rendering presentation accepted a {scope} selector");
        }

        var applicationKeyRejected = false;
        try
        {
            _ = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
                RenderingPresentationTier.Enhanced,
                RenderingBackend.ForwardPlus,
                RenderingSelectionScope.Application,
                "synthetic-layout-smuggled-as-application-key",
                RenderingQualityPolicy.AllCapabilities,
                SourceAuthorizedRenderFeature.None));
        }
        catch (InvalidDataException)
        {
            applicationKeyRejected = true;
        }
        Expect(applicationKeyRejected,
            "application rendering selection accepted a hidden scene key");

        var unknownBackendRejected = false;
        try
        {
            _ = RenderingQualityPolicy.ParseBackend("synthetic_renderer");
        }
        catch (InvalidDataException)
        {
            unknownBackendRejected = true;
        }
        Expect(unknownBackendRejected,
            "unknown rendering backend did not fail closed");

        var unknownCapabilityRejected = false;
        try
        {
            _ = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
                RenderingPresentationTier.Enhanced,
                RenderingBackend.ForwardPlus,
                RenderingSelectionScope.Application,
                SelectionKey: null,
                RenderingQualityPolicy.AllCapabilities |
                (EnhancedRenderingCapability)(1 << 20),
                SourceAuthorizedRenderFeature.None));
        }
        catch (InvalidDataException)
        {
            unknownCapabilityRejected = true;
        }
        Expect(unknownCapabilityRejected,
            "unknown rendering capability did not fail closed");
    }

    private static void CinematicFramingAcceptsVisibleSubject()
    {
        var result = CinematicFramingGate.Evaluate(
            new CinematicFramingSample(
                System.Numerics.Vector3.Zero,
                System.Numerics.Vector3.UnitZ,
                System.Numerics.Vector3.UnitY,
                60,
                16.0f / 9.0f,
                0.05f,
                new System.Numerics.Vector3(0, 0, 3),
                0.32f,
                LineOfSightClear: true),
            new CinematicFramingRequirements(0.05f, 0.05f, 0.75f));

        Expect(result.Accepted && result.Failures == CinematicFramingFailure.None,
            "visible centered cinematic subject failed the framing gate");
    }

    private static void CinematicFramingRejectsWallOcclusion()
    {
        var result = CinematicFramingGate.Evaluate(
            new CinematicFramingSample(
                System.Numerics.Vector3.Zero,
                System.Numerics.Vector3.UnitZ,
                System.Numerics.Vector3.UnitY,
                60,
                16.0f / 9.0f,
                0.05f,
                new System.Numerics.Vector3(0, 0, 3),
                0.32f,
                LineOfSightClear: false),
            new CinematicFramingRequirements(0.05f, 0.05f, 0.75f));

        Expect(!result.Accepted &&
               result.Failures.HasFlag(CinematicFramingFailure.Occluded),
            "wall-occluded cinematic subject passed the framing gate");
    }

    private static void KotorDialogueCameraKeepsSpeakerObjectivelyFramed()
    {
        var listener = new System.Numerics.Vector3(0, 1.61f, 0);
        var speaker = new System.Numerics.Vector3(0, 1.60f, -2.4f);
        var shot = KotorDialogueCameraComposer.ComposeSpeakerShot(
            listener, speaker, cameraAngle: 1, verticalFieldOfViewDegrees: 55);
        var result = CinematicFramingGate.Evaluate(
            new CinematicFramingSample(
                shot.Position,
                shot.Target - shot.Position,
                shot.Up,
                shot.VerticalFieldOfViewDegrees,
                16.0f / 9.0f,
                0.05f,
                speaker,
                0.16f,
                LineOfSightClear: true),
            new CinematicFramingRequirements(0.01f, 0.12f, 0.62f));

        Expect(shot.Kind == KotorDialogueShotKind.SpeakerTight,
            "KOTOR CameraAngle=1 did not select a tight-speaker beat");
        Expect(result.Accepted,
            $"KOTOR tight-speaker beat failed framing: {result.Failures}");
        Expect(result.NormalizedViewportCenter.X is > -0.4f and < 0.1f,
            "KOTOR tight-speaker beat drifted outside the retail left-third composition");
        Expect(System.Numerics.Vector3.Distance(shot.Position, speaker) < 1.0f,
            "KOTOR tight-speaker beat drifted into a distant/first-person gameplay camera");

        var automaticShot = KotorDialogueCameraComposer.ComposeSpeakerShot(
            listener,
            new System.Numerics.Vector3(0, 1.60f, -8.0f),
            cameraAngle: 0,
            verticalFieldOfViewDegrees: 55);
        Expect(automaticShot.Kind == KotorDialogueShotKind.SpeakerTight &&
               System.Numerics.Vector3.Distance(
                   automaticShot.Position,
                   new System.Numerics.Vector3(0, 1.60f, -8.0f)) < 1.0f,
            "KOTOR automatic dialogue framing drifted into a distant midpoint shot");
    }

    private static void KotorDialogueCameraRejectsCoincidentParticipants()
    {
        var threw = false;
        try
        {
            _ = KotorDialogueCameraComposer.ComposeSpeakerShot(
                System.Numerics.Vector3.One,
                System.Numerics.Vector3.One,
                cameraAngle: 1,
                verticalFieldOfViewDegrees: 55);
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Expect(threw,
            "KOTOR dialogue camera guessed a shot for coincident participants");
    }

    private static void KotorFirstEncounterCameraBeatsRemainSourceBound()
    {
        var beats = KotorFirstEncounterCameraContract.Beats;
        Expect(beats.Select(beat => beat.CameraId).SequenceEqual([26, 19, 20]),
            "KOTOR first-encounter authored camera order drifted");
        Expect(beats[0].SubjectTag == "PLAYER" &&
               beats.Skip(1).All(beat => beat.SubjectTag == "end_soldier2"),
            "KOTOR first-encounter source-event targets drifted");
        Expect(beats.All(beat => beat.SubjectRadius > 0 &&
                                beat.MinimumProjectedHeight > 0 &&
                                beat.MaximumProjectedHeight > beat.MinimumProjectedHeight),
            "KOTOR first-encounter framing requirements are invalid");
    }

    private static void KotorCameraCollisionSelectsOnlySourceOpaqueSurfaces()
    {
        var selected = KotorCameraCollisionPolicy.RequireBlockingSurfaceIndices(
        [
            KotorCameraSurfaceOpacity.SourceOpaque,
            KotorCameraSurfaceOpacity.SourceTransparent,
            KotorCameraSurfaceOpacity.SourceOpaque,
            KotorCameraSurfaceOpacity.SourceTransparent
        ]);
        Expect(selected.SequenceEqual([0, 2]),
            "KOTOR mixed-surface camera collision included a transparent surface");
        Expect(KotorCameraCollisionPolicy.RequireBlockingSurfaceIndices(
                [KotorCameraSurfaceOpacity.SourceTransparent]).Count == 0,
            "KOTOR transparent-only mesh produced camera collision");

        var failedClosed = false;
        try
        {
            _ = KotorCameraCollisionPolicy.RequireBlockingSurfaceIndices(
            [
                KotorCameraSurfaceOpacity.SourceOpaque,
                KotorCameraSurfaceOpacity.Unsupported
            ]);
        }
        catch (InvalidDataException)
        {
            failedClosed = true;
        }
        Expect(failedClosed,
            "KOTOR camera collision accepted an unknown surface opacity");
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

    private static void KotorMovementRejectsDegenerateNavigation()
    {
        var rejected = false;
        try
        {
            _ = new KotorMovementSimulation(
                [new KotorNavigationTriangle(
                    System.Numerics.Vector3.Zero,
                    System.Numerics.Vector3.One,
                    new System.Numerics.Vector3(2, 2, 2))],
                new KotorMovementConfiguration(2, 6));
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Expect(rejected, "degenerate navigation triangle was accepted");
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
                8),
            new KotorScriptContract(
                "a_room_anim",
                KotorScriptContractKind.RoomAnimationFromParameters,
                new string('D', 64),
                184),
            new KotorScriptContract(
                "a_start",
                KotorScriptContractKind.ModuleStartPresentation,
                new string('E', 64),
                56,
                MoveTargetTag: "WP_player_start",
                FadeInWaitSeconds: 1.0f,
                FadeInLengthSeconds: 2.0f,
                MusicRestartDelaySeconds: 10.0f),
            new KotorScriptContract(
                "a_playsndobj",
                KotorScriptContractKind.PlaySoundObjectFromParameters,
                new string('F', 64),
                31),
            new KotorScriptContract(
                "a_soundobject",
                KotorScriptContractKind.SoundObjectPlayDelayedFromParameters,
                new string('1', 64),
                31),
            new KotorScriptContract(
                "a_stop_sound",
                KotorScriptContractKind.SoundObjectStopFromParameters,
                new string('2', 64),
                31),
            new KotorScriptContract(
                "a_video_effect",
                KotorScriptContractKind.VideoEffectFromParameters,
                new string('3', 64),
                31),
            new KotorScriptContract(
                "a_local_set",
                KotorScriptContractKind.LocalBooleanSetFromParameters,
                new string('4', 64),
                31),
            new KotorScriptContract(
                "a_intro_autosave",
                KotorScriptContractKind.NoOp,
                new string('0', 64),
                3)
        };
        const string baseItemsSha256 =
            "E9D031FAF0A5D3D4E9CCF33AEE5233FDA8F781A58B30FA722E7CF12B78C85C95";
        var medpac = new KotorItemDefinition(
            "g_i_medeqpmnt01", "Medpac", "g_i_medeqpmnt01",
            "A6449C3EA78042B3E0B09440EAFEAA209C5AA207DE0AFA0CFBCC9296583D9972",
            baseItemsSha256,
            KotorBaseItemIds.MedicalEquipment,
            0, 1, 2, 0, 0, 0, "I_MedEqpmnt", 0, "I_Null", "ii_device");
        var clothing = new KotorItemDefinition(
            "g_a_clothes01", "Clothing", "G_A_CLOTHES01",
            "FC8AB4485644BEC2FAE71C99BBD8853170C1A5D739953B62EB95266173443CF1",
            baseItemsSha256,
            85, 0, 1, 0, 2, 1, (int)KotorEquipmentSlot.Armor,
            "a_cloths", 1, "I_Null", "ia_armor");
        var shortSword = new KotorItemDefinition(
            "g_w_shortswrd01", "Short Sword", "G_w_Shortswrd01",
            "9EC88EBA45CB0ED430483362121672F48CDD9C541ADFE4CF7442F76C14BFD652",
            baseItemsSha256,
            4, 0, 1, 1, 0, 0,
            (int)(KotorEquipmentSlot.RightHand | KotorEquipmentSlot.LeftHand),
            "w_Shortswrd", 0,
            "w_Shortswrd_001", "iw_sword");
        var experienceTable = new KotorExperienceTable(
            new string('A', 64),
            [
                new KotorLevelThreshold(1, 0),
                new KotorLevelThreshold(2, 1000),
                new KotorLevelThreshold(3, 3000)
            ]);
        Expect(experienceTable.ResolveLevel(999) == 1 &&
               experienceTable.ResolveLevel(1000) == 2 &&
               experienceTable.MinimumExperienceFor(2) == 1000,
            "Odyssey level-two threshold drifted from exptable semantics");
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
            new KotorGameplayInitialState(
                0,
                0,
                [
                    new KotorPartyMemberDefinition(
                        "player", "Player", 20, 20, 10, IsPlayer: true),
                    new KotorPartyMemberDefinition(
                        "end_trask", "Trask", 30, 36, 12, IsPlayer: false)
                ]),
            experienceTable,
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
            new KotorGameplayInitialState(
                0,
                0,
                [new KotorPartyMemberDefinition(
                    "healing-player", "Healing Player", 5, 20, 10, IsPlayer: true)]),
            experienceTable);
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

        var unequipped = simulation.UnequipItem(KotorEquipmentSlot.Armor);
        Expect(!unequipped.After.Equipment.ContainsKey(KotorEquipmentSlot.Armor) &&
               unequipped.After.Equipment[KotorEquipmentSlot.RightHand] ==
               "g_w_shortswrd01" &&
               unequipped.After.PlayerInventory["g_a_clothes01"] == 1 &&
               unequipped.Events.Single() is KotorEquipmentRemoved removed &&
               removed.Slot == KotorEquipmentSlot.Armor &&
               removed.Item.Resref == "g_a_clothes01",
            "unequip did not return Clothing to inventory atomically");
        var repeatedUnequip = simulation.UnequipItem(KotorEquipmentSlot.Armor);
        Expect(repeatedUnequip.Events.Count == 0 &&
               repeatedUnequip.After.PlayerInventory["g_a_clothes01"] == 1,
            "repeated unequip duplicated the returned item");
        _ = simulation.EquipItems([
            new KotorEquipRequest("g_a_clothes01", KotorEquipmentSlot.Armor)
        ]);
        var rightHandRemoved = simulation.UnequipItem(KotorEquipmentSlot.RightHand);
        var leftHandEquipped = simulation.EquipItems([
            new KotorEquipRequest("g_w_shortswrd01", KotorEquipmentSlot.LeftHand)
        ]);
        Expect(rightHandRemoved.After.PlayerInventory["g_w_shortswrd01"] == 1 &&
               leftHandEquipped.After.Equipment[KotorEquipmentSlot.LeftHand] ==
               "g_w_shortswrd01" &&
               !leftHandEquipped.After.Equipment.ContainsKey(
                   KotorEquipmentSlot.RightHand),
            "source-valid Short Sword left-hand transaction was not preserved");
        _ = simulation.UnequipItem(KotorEquipmentSlot.LeftHand);
        _ = simulation.EquipItems([
            new KotorEquipRequest("g_w_shortswrd01", KotorEquipmentSlot.RightHand)
        ]);

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

        var roomAnimation = simulation.ExecuteScript(
            "a_room_anim",
            new KotorScriptInvocation(2, 0, 0, 0, 0, "101per2b"));
        var roomAnimationRequest = roomAnimation.Events
            .OfType<KotorRoomAnimationRequested>().Single();
        Expect(roomAnimationRequest.RoomModel == "101per2b" &&
               roomAnimationRequest.AnimationIndex == 2 &&
               roomAnimation.Events.OfType<KotorScriptExecuted>().Single().Contract.Resref ==
               "a_room_anim",
            "parameterized room-animation script did not preserve its source invocation");

        var moduleStart = simulation.ExecuteScript("a_start");
        var startEvents = moduleStart.Events;
        Expect(startEvents.Count == 6 &&
               startEvents[0] is KotorGlobalFadeRequested { FadeIn: false,
                   DelaySeconds: 0, LengthSeconds: 0 } &&
               startEvents[1] is KotorPlayerMoveRequested {
                   WaypointTag: "WP_player_start" } &&
               startEvents[2] is KotorGlobalFadeRequested { FadeIn: true,
                   DelaySeconds: 1.0f, LengthSeconds: 2.0f } &&
               startEvents[3] is KotorBackgroundMusicRequested {
                   Playing: false, DelaySeconds: 0 } &&
               startEvents[4] is KotorBackgroundMusicRequested {
                   Playing: true, DelaySeconds: 10.0f } &&
               startEvents[5] is KotorScriptExecuted,
            "module-start presentation did not preserve fade, move, and music order");

        var soundObject = simulation.ExecuteScript(
            "a_playsndobj",
            new KotorScriptInvocation(1, 0, 0, 0, 0, "FloorMonitors"));
        Expect(soundObject.Events.Count == 2 &&
               soundObject.Events[0] is KotorSoundObjectPlayRequested {
                   Tag: "FloorMonitors", DelaySeconds: 1.0f } &&
               soundObject.Events[1] is KotorScriptExecuted,
            "parameterized sound-object script did not preserve tag and delay");

        var delayedSoundObject = simulation.ExecuteScript(
            "a_soundobject",
            new KotorScriptInvocation(0, 3, 0, 0, 0, "ComputerVoice"));
        Expect(delayedSoundObject.Events[0] is KotorSoundObjectPlayRequested {
                   Tag: "ComputerVoice", DelaySeconds: 3.0f },
            "KOTOR II delayed sound-object parameters drifted");
        var stoppedSoundObject = simulation.ExecuteScript(
            "a_stop_sound",
            new KotorScriptInvocation(2, 4, 0, 0, 0, "ComputerVoice"));
        Expect(stoppedSoundObject.Events[0] is KotorSoundObjectStopRequested {
                   Tag: "ComputerVoice", DelaySeconds: 4.0f, FadeSeconds: 2.0f },
            "KOTOR II sound-object stop parameters drifted");

        var enabledVideoEffect = simulation.ExecuteScript(
            "a_video_effect", new KotorScriptInvocation(1, 0, 0, 0, 0, ""));
        var disabledVideoEffect = simulation.ExecuteScript(
            "a_video_effect", new KotorScriptInvocation(0, 0, 0, 0, 0, ""));
        Expect(enabledVideoEffect.Events[0] is KotorVideoEffectRequested {
                   Enabled: true, EffectId: 1 } &&
               disabledVideoEffect.Events[0] is KotorVideoEffectRequested {
                   Enabled: false, EffectId: 1 },
            "KOTOR II T3 video-effect transitions drifted");

        var localSet = simulation.ExecuteScript(
            "a_local_set", new KotorScriptInvocation(40, 0, 0, 0, 0, "tr_journal"));
        Expect(localSet.Events[0] is KotorLocalBooleanChanged {
                   ObjectTag: "tr_journal", Index: 40,
                   Before: false, After: true } &&
               localSet.After.LocalBooleans?["tr_journal:40"] == true,
            "KOTOR II tutorial local-boolean transition drifted");

        var noOp = simulation.ExecuteScript("a_intro_autosave");
        Expect(noOp.Events.Single() is KotorScriptExecuted executedNoOp &&
               executedNoOp.Contract.Kind == KotorScriptContractKind.NoOp,
            "verified Odyssey no-op script did not execute as a no-op contract");

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

        var playerPartyMemberId = simulation.CaptureSnapshot().PlayerPartyMemberId;
        var selectTrask = simulation.SelectPartyMember("END_TRASK");
        Expect(selectTrask.Before.SelectedPartyMemberId ==
               playerPartyMemberId &&
               selectTrask.After.SelectedPartyMemberId == "end_trask" &&
               selectTrask.After.PartyMembers["end_trask"].CurrentVitality == 30 &&
               selectTrask.Events.Single() is KotorPartyMemberSelected selected &&
               selected.BeforeId == playerPartyMemberId &&
               selected.AfterId == "end_trask",
            "inventory party selection did not persist the profile-owned member");
        var healTrask = simulation.UseMedpac("g_i_medeqpmnt01");
        var partyUse = healTrask.Events.OfType<KotorItemUsed>().Single();
        Expect(healTrask.After.PartyMembers["end_trask"].CurrentVitality == 36 &&
               healTrask.After.PlayerCurrentVitality == 20 &&
               healTrask.After.PlayerInventory["g_i_medeqpmnt01"] == 1 &&
               partyUse.PartyMemberId == "end_trask" &&
               partyUse.VitalityBefore == 30 && partyUse.VitalityAfter == 36,
            "inventory Medpac did not target the selected party member");
        var selectPlayer = simulation.SelectPartyMember(
            playerPartyMemberId);
        Expect(selectPlayer.After.SelectedPartyMemberId ==
               playerPartyMemberId &&
               selectPlayer.After.PartyMembers["end_trask"].CurrentVitality == 36,
            "inventory party selection did not preserve companion vitality");
        var repeatedPlayerSelection = simulation.SelectPartyMember(
            playerPartyMemberId.ToUpperInvariant());
        Expect(repeatedPlayerSelection.Events.Count == 0 &&
               repeatedPlayerSelection.After.SelectedPartyMemberId ==
               playerPartyMemberId,
            "repeated inventory party selection was not idempotent");
    }

    private static void KotorInventoryProjectionStaysLinear()
    {
        var configuration = KotorRuntimeConfiguration.Load(Path.Combine(
            AppContext.BaseDirectory, "config", "kotor-runtime.json"));
        var samples = new List<(int Input, long Work)>();
        foreach (var size in configuration.Complexity.InventoryProjectionSampleSizes)
        {
            var definitions = Enumerable.Range(0, size)
                .Select(index => $"item-{index}")
                .ToArray();
            var inventory = definitions.ToDictionary(
                item => item,
                _ => 1,
                StringComparer.OrdinalIgnoreCase);
            var projection = KotorInventoryProjection.Project(
                definitions,
                inventory,
                item => item,
                _ => false,
                questItemsOnly: false,
                configuration.Presentation.Inventory.OverflowAcceptanceRepeat);
            Expect(
                projection.Items.Count ==
                size * configuration.Presentation.Inventory.OverflowAcceptanceRepeat,
                "inventory complexity fixture did not materialize the configured rows");
            samples.Add((size, projection.WorkUnits));
        }

        var measuredMaximumExponent = 0.0;
        foreach (var (before, after) in samples.Zip(samples.Skip(1)))
        {
            var exponent = Math.Log(after.Work / (double)before.Work) /
                           Math.Log(after.Input / (double)before.Input);
            measuredMaximumExponent = Math.Max(measuredMaximumExponent, exponent);
            Expect(exponent <= configuration.Complexity.MaximumExponent,
                $"inventory projection exceeded configured O(N) curve: {exponent:F4}");
        }
        Console.WriteLine(
            "NIKAMI_AURORA_COMPLEXITY_PASS component=inventory-projection curve=O(N) " +
            $"samples={string.Join(',', samples.Select(sample =>
                $"{sample.Input}:{sample.Work}"))} " +
            $"measuredExponent={measuredMaximumExponent:F4} " +
            $"limit={configuration.Complexity.MaximumExponent:F4}");
    }

    private static void KotorEnvironmentMaterialPolicyPreservesSourceContract()
    {
        Expect(KotorEnvironmentMaterialPolicy.FaceOrder.SequenceEqual(
                new[]
                {
                    "positive-x", "negative-x", "positive-y",
                    "negative-y", "positive-z", "negative-z"
                }),
            "KOTOR cubemap face order drifted");
        var odysseyForward = KotorEnvironmentMaterialPolicy.ToOdysseySampleDirection(
            new System.Numerics.Vector3(0, 0, -1));
        Expect(odysseyForward == System.Numerics.Vector3.UnitY,
            "Godot forward did not map to Odyssey forward for cubemap sampling");
        Expect(KotorEnvironmentMaterialPolicy.EnvironmentMapResref(
                "metal__aurora_envmap_CM_Baremetal__aurora_additive") ==
               "CM_Baremetal",
            "environment-map material marker was not parsed deterministically");
        const string scaledMaterial =
            "metal__aurora_envmap_CM_Baremetal__aurora_normal_scale_1.3";
        Expect(KotorEnvironmentMaterialPolicy.AuthoredNormalScale(scaledMaterial) == 1.3f &&
               KotorEnvironmentMaterialPolicy.EnvironmentMapResref(scaledMaterial) ==
               "CM_Baremetal" &&
               KotorEnvironmentMaterialPolicy.AuthoredNormalScale("plain") is null,
            "TXI bumpmapscaling marker did not preserve its exact material value");
        Expect(KotorEnvironmentMaterialPolicy.IsSourceDecal(
                   "floor_mark__aurora_decal") &&
               !KotorEnvironmentMaterialPolicy.IsSourceDecal("floor_mark") &&
               KotorEnvironmentMaterialPolicy.SourceDecalRenderPriority == 1,
            "TXI decal marker did not preserve no-depth-write render ordering");
        var cycle = KotorEnvironmentMaterialPolicy.CycleTexture(
            "EBO_AScrn__aurora_additive__aurora_decal__aurora_cycle_4_4_35");
        Expect(cycle == new KotorCycleTexture(4, 4, 35) &&
               KotorEnvironmentMaterialPolicy.CycleTexture("plain") is null,
            "TXI cycle marker did not preserve atlas dimensions and timing");
        ExpectThrows<InvalidDataException>(
            () => KotorEnvironmentMaterialPolicy.AuthoredNormalScale(
                "wall__aurora_normal_scale_nan"),
            "non-finite normal-scale marker was accepted");
        Expect(KotorEnvironmentMaterialPolicy.ReflectionStrength(enhanced: false) == 0.0f &&
               KotorEnvironmentMaterialPolicy.ReflectionStrength(enhanced: true) > 0.0f &&
               KotorEnvironmentMaterialPolicy.MaximumReflectionWeight(enhanced: true) < 1.0f,
            "source and enhanced reflection-strength policies collapsed");
    }

    private static void KotorLightmapTransferKeepsSourceAndEnhancedDistinct()
    {
        var source = KotorEnvironmentMaterialPolicy.LightmapTransfer(enhanced: false);
        var enhanced = KotorEnvironmentMaterialPolicy.LightmapTransfer(enhanced: true);
        Expect(source.Formula == "surface-times-clamped-lightmap" &&
               source.DynamicLightAlbedoWeight == 0 &&
               source.BakedEmissionWeight == 1 &&
               source.DynamicAmbientEmissionWeight == 0 &&
               !source.DynamicLightsEnabled,
            "KOTOR source lightmap transfer can double-light a baked surface");

        var surface = new System.Numerics.Vector3(.8f, .4f, 1.2f);
        var lightmap = new System.Numerics.Vector3(1.4f, .5f, -.25f);
        var sourceDark = source.ComputeEmission(
            surface, lightmap, System.Numerics.Vector3.Zero);
        var sourceBrightAmbient = source.ComputeEmission(
            surface, lightmap, new System.Numerics.Vector3(.7f, .7f, .7f));
        Expect(System.Numerics.Vector3.Distance(
                   sourceDark, new System.Numerics.Vector3(.8f, .2f, 0)) < .000001f &&
               System.Numerics.Vector3.Distance(sourceBrightAmbient, sourceDark) < .000001f,
            "KOTOR source transfer allowed area ambient to flatten a baked lightmap");

        Expect(enhanced.Formula == "baked-preserving-bounded-dynamic" &&
               enhanced.DynamicLightAlbedoWeight == .12f &&
               enhanced.BakedEmissionWeight == 1.0f &&
               enhanced.DynamicAmbientEmissionWeight == .15f &&
               enhanced.DynamicLightsEnabled,
            "KOTOR enhanced transfer no longer preserves its bounded dynamic response");
        var enhancedDark = enhanced.ComputeEmission(
            surface, new System.Numerics.Vector3(.1f), System.Numerics.Vector3.Zero);
        var enhancedAmbient = enhanced.ComputeEmission(
            surface, new System.Numerics.Vector3(.1f), new System.Numerics.Vector3(.8f));
        Expect(enhancedAmbient.X > enhancedDark.X && enhancedAmbient.Y > enhancedDark.Y,
            "KOTOR enhanced ambient response is not independently bounded");
    }

    private static void KotorGenericModulePresentationRemainsStoryNeutral()
    {
        var mode = KotorModulePresentationPolicy.RequireContentMode(
            "TAR_M02AA", KotorModulePresentationPolicy.GenericWorldMode,
            hasFirstEncounter: false);
        Expect(mode == KotorModuleContentMode.GenericWorld,
            "non-Endar KOTOR module did not select generic-world presentation");
        Expect(KotorEnvironmentMaterialPolicy.EnhancedAuthorizedRenderFeatures ==
               SourceAuthorizedRenderFeature.Reflections,
            "generic KOTOR enhancement authorized unproven indirect or atmosphere semantics");
        Expect(KotorEnvironmentMaterialPolicy.DielectricSpecular(enhanced: false) == 0 &&
               KotorEnvironmentMaterialPolicy.DielectricSpecular(enhanced: true) > 0 &&
               KotorEnvironmentMaterialPolicy.FallbackRoughness(enhanced: false) == 1 &&
               KotorEnvironmentMaterialPolicy.FallbackRoughness(enhanced: true) < 1,
            "KOTOR source/enhanced material response collapsed");

        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireEndarAutomation(mode, requested: true),
            "generic module accepted Endar-only story/camera automation");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireContentMode(
                "tar_m02aa", KotorModulePresentationPolicy.EndarOpeningMode,
                hasFirstEncounter: true),
            "generic module accepted Endar content identity");
    }

    private static void KotorGenericVisualInventoryFailsClosed()
    {
        var complete = new KotorModuleVisualInventory(
            AuthoredRooms: 17,
            VisualRooms: 17,
            AuthoredMaterialSurfaces: 574,
            ConfiguredMaterialSurfaces: 574,
            AuthoredEmitters: 0,
            MaterializedEmitters: 0,
            EnvironmentMaps: 3,
            BoundEnvironmentMaps: 3,
            MissingSourceAssets: 3,
            ReportedMissingSourceAssets: 3,
            UnsupportedSourceSemantics: 0);
        KotorModulePresentationPolicy.RequireVisualInventory(complete);

        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireVisualInventory(
                complete with { ConfiguredMaterialSurfaces = 573 }),
            "generic module accepted incomplete material coverage");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireVisualInventory(
                complete with { ReportedMissingSourceAssets = 2 }),
            "generic module hid a missing source asset from its report");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireVisualInventory(
                complete with { UnsupportedSourceSemantics = 1 }),
            "generic module accepted unsupported source presentation semantics");
    }

    private static void KotorGlobalPbrCoverageRequiresEveryEligibleSurface()
    {
        var enhanced = new KotorPbrCoverage(
            RenderableSurfaces: 574,
            SourceUnshadedSurfaces: 12,
            PbrSurfaces: 562,
            EnhancedPresentation: true);
        KotorModulePresentationPolicy.RequirePbrCoverage(enhanced);
        KotorModulePresentationPolicy.RequirePbrCoverage(enhanced with
        {
            PbrSurfaces = 0,
            EnhancedPresentation = false
        });

        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequirePbrCoverage(
                enhanced with { PbrSurfaces = 561 }),
            "KOTOR enhanced presentation accepted one non-PBR eligible surface");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequirePbrCoverage(
                enhanced with { SourceUnshadedSurfaces = 575 }),
            "KOTOR PBR census accepted more exclusions than renderable surfaces");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequirePbrCoverage(
                enhanced with { EnhancedPresentation = false }),
            "KOTOR source tier accepted enhanced PBR surface counts");
    }

    private static void KotorCreaturePresentationRequiresEveryModelAndWeaponEffect()
    {
        var complete = new KotorCreaturePresentationInventory(
            SourceCreatures: 8,
            RenderedCreatures: 8,
            UnsupportedCreatures: 0,
            SourceModelParts: 21,
            MaterializedModelParts: 21,
            EquippedWeapons: 6,
            MaterializedEquippedWeapons: 6,
            WeaponAdditiveSurfaces: 8,
            ConfiguredWeaponAdditiveSurfaces: 8,
            SourceEmitters: 3,
            MaterializedEmitters: 3,
            SourceLights: 1,
            MaterializedLights: 1,
            SourceEffectAnimations: 7,
            MaterializedEffectAnimations: 7,
            UnsupportedEffectSemantics: 0);
        KotorModulePresentationPolicy.RequireCreaturePresentation(complete);
        Expect(KotorModulePresentationPolicy.AdditiveGlowMultiplier(false) == 1.0f &&
               KotorModulePresentationPolicy.AdditiveGlowMultiplier(true) ==
               KotorModulePresentationPolicy.EnhancedAdditiveGlowMultiplier,
            "KOTOR source/enhanced additive glow policy collapsed");

        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireCreaturePresentation(
                complete with { RenderedCreatures = 7, UnsupportedCreatures = 1 }),
            "KOTOR presentation accepted an unsupported creature");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireCreaturePresentation(
                complete with { MaterializedModelParts = 20 }),
            "KOTOR presentation dropped a source model part");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireCreaturePresentation(
                complete with { MaterializedEquippedWeapons = 5 }),
            "KOTOR presentation dropped an equipped weapon");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireCreaturePresentation(
                complete with { ConfiguredWeaponAdditiveSurfaces = 7 }),
            "KOTOR presentation flattened a weapon additive surface");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireCreaturePresentation(
                complete with { MaterializedEmitters = 2 }),
            "KOTOR presentation ignored an actor emitter");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireCreaturePresentation(
                complete with { MaterializedLights = 0 }),
            "KOTOR presentation ignored an actor light");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireCreaturePresentation(
                complete with { MaterializedEffectAnimations = 6 }),
            "KOTOR presentation ignored an actor effect animation");
        ExpectThrows<InvalidDataException>(
            () => KotorModulePresentationPolicy.RequireCreaturePresentation(
                complete with { UnsupportedEffectSemantics = 1 }),
            "KOTOR presentation accepted unsupported actor effect semantics");
    }

    private static void KotorCreatureEffectsPreserveBurstOverlapAndAtlasBounds()
    {
        var poolSize = KotorCreatureEffectPolicy.RequiredBurstPoolSize(
            [new KotorCreatureEffectSchedule(0.3, [0.0, 0.1, 0.2])],
            lifetime: 0.25);
        Expect(poolSize == 3,
            $"KOTOR overlapping creature bursts require three instances, got {poolSize}");

        var fixedFrame = KotorCreatureEffectPolicy.RequireAtlasPlayback(
            columns: 2, rows: 2, frameStart: 1, frameEnd: 1,
            framesPerSecond: 16, lifetime: 1, loop: 0);
        Expect(Math.Abs(fixedFrame.Offset - 0.25f) < 0.0001f &&
               fixedFrame.Cycles == 0 && !fixedFrame.Loop,
            "KOTOR fixed-frame spark atlas acquired unintended playback");

        var fullAtlas = KotorCreatureEffectPolicy.RequireAtlasPlayback(
            columns: 2, rows: 2, frameStart: 0, frameEnd: 3,
            framesPerSecond: 16, lifetime: 0.1f, loop: 0);
        Expect(Math.Abs(fullAtlas.Cycles - 0.4f) < 0.0001f,
            "KOTOR full actor-effect atlas lost source FPS/lifetime transfer");

        ExpectThrows<InvalidDataException>(
            () => KotorCreatureEffectPolicy.RequireAtlasPlayback(
                columns: 4, rows: 4, frameStart: 2, frameEnd: 7,
                framesPerSecond: 16, lifetime: 1, loop: 0),
            "KOTOR actor effects accepted an unsupported partial atlas range");
    }

    private static void KotorRigIdentityRecognizesSourceBodyFamilies()
    {
        foreach (var sourceName in new[]
                 {
                     "mesh__PMBAM_LArm_2",
                     "mesh__PMBAM_RArm_3",
                     "mesh__PMBBM_armL_2",
                     "mesh__PMBBM_armR_3"
                 })
            Expect(KotorRigIdentityPolicy.IsArmMeshName(sourceName),
                $"KOTOR source arm mesh identity was rejected: {sourceName}");

        foreach (var nonArm in new[]
                 {
                     "mesh__PMBAM_Torso_1",
                     "mesh__PMHA01_head_11",
                     "mesh__w_Shortswrd_001_w_Shortsword_12"
                 })
            Expect(!KotorRigIdentityPolicy.IsArmMeshName(nonArm),
                $"KOTOR non-arm mesh identity was misclassified: {nonArm}");
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

    private static void ExpectThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
