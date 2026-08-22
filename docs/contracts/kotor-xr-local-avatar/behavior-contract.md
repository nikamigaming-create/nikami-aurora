# KOTOR first-person XR local-avatar contract

## Scope

First-person XR gameplay must not render the local player's own face around the
tracked camera. It must preserve the installed full-body avatar so looking down
still presents the torso, arms, hand rig, Clothing, and attached Short Sword.
Desktop presentation and authored dialogue/cinematic cameras continue to render
the complete player, including the head.

This is a presentation-only rule. It does not change player appearance,
animation, collision, equipment, simulation, or source data.

## Source boundary

The deterministic proof player remains appearance row 137, `P_MAL_A_MED_01`,
with head `PMHA01`. After the opening equipment transaction it uses:

- Clothing body `PMBBM` / texture `PMBBM01`;
- head `PMHA01`; and
- right-hand weapon `w_Shortswrd_001`.

The imported manifest binds those owned models to MDL/MDX SHA-256 pairs:

```text
PMBBM           873DD2B3275D0C846FFAECF4E51BC685AFE3E865D5B266DD5F184AA28A9ECC12
                6CCAB8D56537506142FA1E69F54F899A0B1D37CC1602C2BD9227E02AEC0C1DC0
PMHA01          BAFA3CECA6F3440FAF5687271CE78C1D90E7C5580D1A8E533A70F0E50F040A94
                D18A5521E795F3721B3FE37878E6A70CBCA2335BE22AA229166ACA14F085123E
w_Shortswrd_001 0E6DA2E5CD4EF7569D1909B8867CD4930CEE2D767A9C1FE90B0359753C0E2E4C
                9EFC89709DE14ABA0B9C7C62B99BC399026EDD4FBA520B8F76E3051569195E05
```

Generated GLBs and every installed asset remain ignored local data.

## Runtime mesh boundary

Godot's glTF scene generation flattens part of the Odyssey hook hierarchy. The
authored `headhook` is therefore not a safe runtime visibility parent: hiding a
guessed ancestor could remove the torso, hands, or weapon, while hiding only one
child could leave eyes, teeth, or tongue around the camera.

The runtime instead classifies and toggles the eight generated `MeshInstance3D`
nodes whose names begin `mesh__PMHA`. It independently requires:

- exactly eight head meshes;
- at least three `mesh__PMB` body meshes;
- both `lhand` and `rhand` nodes; and
- the separately attached `weapon__*` hierarchy when equipment is present.

The visibility rule is:

```text
showLocalHead = !openXrActive || dialogueCameraActive
```

Only the eight head mesh `Visible` flags change. The player root, body meshes,
skeleton, hand nodes, and weapon hierarchy are never hidden by this rule.

## Acceptance gate

`Start-KotorGodot.ps1 -OpenXRSimulator -XrBodyLookDown` creates an active XR
look-down pose only for deterministic QA. The gate equips the opening gear,
centers the proof view between the live source hand transforms, and requires
telemetry equivalent to:

```text
NIKAMI_AURORA_XR_LOCAL_AVATAR status=gameplay-head-hidden headMeshes=8 bodyMeshes=3 hands=left,right weapon=present
NIKAMI_AURORA_XR_BODY_VIEW status=ready ... head=hidden body=visible hands=left,right
NIKAMI_AURORA_CAPTURE status=Ok source=xr-spectator ...
```

The active-XR showcase completion gate also fails if the local head is not
hidden after returning to gameplay. Dialogue camera entry invalidates the cache
and restores all eight meshes; camera release hides them again.

The source `pause1` clip retains its authored neutral hand position. Driving the
avatar arms and hands from tracked controllers requires a separate IK contract
and physical-headset comfort/occlusion acceptance; it is not claimed here.
