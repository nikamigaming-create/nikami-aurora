# KOTOR first Sith encounter contract

## Scope and evidence baseline

- Game: Steam KOTOR 1.0.3.0.
- Executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- Module: `end_m01aa`.
- Door `end_door02` UTD SHA-256:
  `61084D946CEF726EA1DCE04635B78FE478CC465EF2EF778456761F9F7F24FA4A`.
- Scene object `end01_sceneobj01` UTP SHA-256:
  `B5F5872FA09C9DD4857043ED11680EF950F75667FE8CD8FCF6E5F8A4AAC07952`.
- Dialogue `end_room3` SHA-256:
  `93F902553551EF421C502C925E60A8A64958A49AB30545ADE890A8D9FED24FBB`.
- Static cameras: 26, 19, and 20, imported from the module GIT with source
  position, height, orientation, pitch, and FOV.

The importer validates the reachable DLG chain, source GFF records, NCS action
signatures and constants, model pairs, emitter controllers, audio tables, and
all hashes below. Decompiled source text is not implementation input.

## Encounter participants and environment

The staged fight requires these UTC-bound participants:

| Tag | UTC template | Source SHA-256 | Initial behavior |
|---|---|---|---|
| `end_sith2` | `end_repsol004` | `3DDF45C6D240ED9C14AF0A737F7DD2F1814B5C14761882EF712D99B9EAEE9129` | Sith rifle attacker |
| `end_sith3` | `end_repsol005` | `511A18B75AA2A0D8285C9B28DCFAAC9FD2712164DCBEC8B1D494D75884DD2D84` | Sith rifle attacker |
| `end_soldier2` | `n_repsold002` | `1FDE29038FA1BA05594DFD259FD83A8FAB9F9C8E3CD230F8FED6B6FAAD17F59D` | Republic target, then dead |

The Sith body is `N_SithSoldier`; the rifle is `w_BlstrRfl_002`. Exported
animation sources provide `c3d4`, `b7a1`, `die`, and `dead`, in addition to
locomotion. Runtime actor lookup aliases both template and GIT tag so dialogue,
animation, projectile hooks, and placement resolve the same instance.

Camera 20 must also include six existing GIT placeables, not an empty hallway:

| Template | Count | UTP SHA-256 | Model |
|---|---:|---|---|
| `rsldcrps001` | 2 | `E7509A6F17C55891483BDEC8A5017B9AF1138C4A77C6C34F4C327F73BD01318A` | `PLC_RSldCrps` |
| `plc_rsldcrps` | 1 | `B54B116F555D1D01D9055DDBB5165C8B1D6C8B2486D610D6F7450C9A6C6C3702` | `PLC_RSldCrps` |
| `plc_brokndrd` | 1 | `B25E85DA920133E88EEC49F32755D7FBA76B083A5078B757DF88D8CD1968FE4A` | `PLC_BroknDrd` |
| `plc_pwrcond` | 2 | `843000B2929EA755BA9A6962CFBF053942194CD875A57DD0DF6CA15ABD67641C` | `PLC_PwrCond` |

The manifest embeds these placements and model hashes. Runtime boot and the
combat-ready assertion both fail if a template/position pair is absent.

## Script and timeline behavior

The source scripts are retained as behavioral evidence:

| Script | Instructions | SHA-256 | Contract used here |
|---|---:|---|---|
| `k_pend_door18` | 1,176 | `2125F185A05D13C6B917A21137FEC35BC70AF114F945CCE2EC7A534FA3BAC441` | Door opens and begins the room event |
| `k_pend_camera` | 197 | `290A81A0ACFE295049075F2160479134A23B010BA88DE7D14B6633C0FD98D154` | Initial staged-camera control |
| `k_pend_cut1_1` | 63 | `39FB4B702B4EABCFFD55618556A67EDB805D42D61ECA2CC6B6F22167D5CA55EC` | Pause dialogue and issue `CutsceneAttack` |
| `k_pend_traskdl49` | 751 | `6DDF090FB0FA78005B66BEAB32F9B72EB38F0336176C648DDAEB81466F41874D` | Set `END_TRASK_DLG=1` |
| `k_pend_cut1_end` | 879 | `CF1B327BB9944A78610F628F2B9103674635E8F9ED3F8F8FBD356A1674FB0072` | End staging and release the party to combat |

The independently written runtime performs this observable sequence:

1. open `end_door02`, place the player and Trask at the two room-3 waypoints,
   and activate camera 26;
2. switch to camera 19, stage the first two source cutscene attacks, and start
   battle music after the source area delay;
3. switch to camera 20, stage the third attack, and transition the Republic
   soldier through `die` to `dead`;
4. traverse the two voiced Trask entries only after each voice finishes, apply
   their LIP tracks and authored gestures, and set `END_TRASK_DLG=1`; and
5. enter combat-ready staging with both Sith active, then restore gameplay and
   standard area music after the hold.

## Audio contract

`ammunitiontypes.2da` row 1 and the ARE music IDs bind these resrefs:

| Role | Resref | Source encoding | Source SHA-256 | Playable duration |
|---|---|---|---|---:|
| Shot | `cb_sh_blast1` | mono IMA ADPCM WAV | `6CEC36A516E110925A32D4B4DDE2B3F4D49CF7CE75A6846CBCB73AE2B69C5534` | 0.509 s |
| Impact | `cb_ht_blastleth1` | mono PCM16 WAV | `C0F67B77536ECD9CBD7231C4F03AABDE9A0280CDB77F90887D8C66E14A75CECD` | 1.663 s |
| Standard music | `mus_theme_sith` | KOTOR-wrapped MP3 | `84E758242095351F095B4055A0E46837C0B1F4AC82B00B6B896932A5D40A26A8` | 82.939 s |
| Battle music | `mus_bat_sithbs` | KOTOR-wrapped MP3 | `081E9BDBE0F3AF0E5D4C3098DC63D674638971B8FE2BABD705358FFA046431F1` | 78.733 s |

The importer decodes the supported mono IMA layout to PCM16 and strips only
the wrapper preceding each MP3. It records original and playable hashes
separately. Runtime rehashes every playable payload and rejects a null or
zero-duration stream before the encounter can start.

The two DLG voice records and LIP tracks are also hash-bound. Dialogue advances
from each voiced entry only on the audio-finished signal, so animation, facial
pose, and subsequent staging cannot outrun the spoken line.

## Projectile and effect contract

The source projectile emitter `w_laserfire_r` has MDL SHA-256
`01DFB4FECFF9286E2E9194324A0EDE63A5FA2C8D4CEAA25F1567EE69682A735C`.
It uses `Fx_laser_01`, Lighten blending, motion blur, source color
`(0.929412, 0.109804, 0)`, and size 0.09.

The muzzle emitter `v_muzflash_01` has MDL SHA-256
`10501A23FE8DBEF9A03F17929DC88F83AC165DCCC4CBAF70A7E065B5FEDA8A76`.
Its five emitters use `fx_muzflash` and `fx_flare02`, Lighten blending,
billboard rendering, size 0.30, and lifetime 0.02 seconds.

The importer exports those three owned textures as private PNG payloads with
source and payload hashes. Runtime validates them before use. Projectiles begin
at the actor's authored `bullethook`, travel to the target talk/body offset,
play the shot at launch and impact at arrival, and use source-sized additive
textures instead of an opaque procedural sphere. The verifier counts four
launches, four muzzle effects, and at least three completed impacts.

Aurora does not add a dynamic point light or an emissive-energy multiplier to
the muzzle flash. Neither behavior is identified by the bound source emitter,
and the former proof mapping produced full-frame white flashes from the staged
camera. The source Lighten blend is represented by the unshaded additive
texture itself. Exact five-emitter topology remains required before this row
can be parity-accepted.

Port-side fallback muzzle/target heights, projectile length/speed, minimum
travel time, effect colors, flare scale, impact size/lifetime, and shot/impact
levels are explicit validated fields in `config/kotor-runtime.json`; none
remain buried in encounter code. They remain retail-unaccepted mapping values
until a matched temporal row closes them.

The corridor geometry also owns persistent MDL emitters; these are separate
from weapon projectile models. Camera 20 must have all five `M01aa_03a`
emitters, including `Object107/smoke044` at the damaged corridor end. The full
module gate requires nine `fx_Smoke` and three `fx_Spark` systems before the
encounter can pass. See the room-emitter contract for controller transfer and
source hashes.

## Alpha, depth, and lighting transfer

Solid room and furniture surfaces remain in the opaque depth-writing path.
Known low-alpha source FX textures such as `LHR_blst02` are imported as blended,
double-sided surfaces and use a transparent lightmap shader with no depth
write. This removes the former black decal quads without reintroducing the sofa
and floor see-through regression.

Lightmapped room materials retain the baked atlas as emission and also accept
the ARE dynamic ambient plus imported point lights. A source-ambient floor is
applied only where the baked atlas is near black, making the camera-20 end cap
read as existing geometry rather than a missing section. This transfer is a
port-side lighting contract, not a claim of retail photometric parity.

## Deterministic verification and limits

The owned-runtime gate is launched with `-TestFirstEncounter`. At
`encounter:combat-ready` it requires:

- `END_TRASK_DLG=1`, door 02 open, and dialogue hidden;
- cameras 26, 19, and 20 reached in order;
- both voice resrefs played to completion;
- battle music active;
- four shot/projectile/muzzle events and at least three impacts;
- nine smoke and three spark room emitters, including the damaged-end binding;
- the Republic soldier in `dead` and both Sith in active staging; and
- all six source environment placements materialized.

Desktop capture logs currently satisfy every assertion with no Godot error or
exception. OpenXR without a runtime falls back through the same deterministic
path. Meta XR Simulator 205.0 also completes the route through Godot's Vulkan
mobile renderer with `OPENXR status=ready`; the shared-world spectator copies
the tracked HMD camera for non-black still and final-movie validation.

Known limits:

- the current route models this cutscene and transition, not general KOTOR
  combat AI, damage resolution, inventory, or encounter spawning;
- the current muzzle presentation binds the two source textures and source
  maximum size/lifetime but does not yet reproduce all five emitter nodes;
- impact presentation is an independently written flare approximation because
  this ammunition row does not identify a separate authored impact emitter;
- retail camera, placement, lighting, animation, and sub-frame timing telemetry
  has not yet been captured, so visual parity is not claimed; and
- the final showcase MP4 remains gated on a clean merged-main full route and
  successful one-output movie finalization.
