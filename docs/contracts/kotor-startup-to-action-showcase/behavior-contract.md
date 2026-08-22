# KOTOR startup-to-action showcase contract

## Purpose and source boundary

This deterministic route joins already isolated KOTOR vertical slices into one
boot-to-action proof. It is an assertion harness, not a prerecorded sequence.
Every line, choice, placement, model, animation, sound, effect, and script state
is still loaded from the user's owned installation at runtime.

Evidence baseline:

- Steam KOTOR 1.0.3.0 executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`;
- module `end_m01aa`;
- opening/corridor DLG `end_trask01` SHA-256:
  `F65EF68C0FB56382F07433151A0F75939AE33E69FBE102D1C1BB13B6476CFF41`;
- first-encounter DLG `end_room3` SHA-256:
  `93F902553551EF421C502C925E60A8A64958A49AB30545ADE890A8D9FED24FBB`.

The importer validates the exact route links, terminal nodes, voice resrefs,
opening-door control script, corridor continuation, and first-encounter graph.
Any installed DLG drift fails import rather than silently choosing a new path.

## Authored dialogue path

The opening starts at DLG starter 0 (`entry:54`). Its blank control executes
`k_pend_traskdl40`, opens `end_door01`, and reaches voiced `entry:55`.

The route waits for each voice-finished signal, exposes the real choices for 30
process frames, then selects index 0 at these source nodes:

| Node | Selected link | Alternate link | Purpose |
|---|---|---|---|
| `entry:55` | `reply:74` | `reply:72` | shortest first response |
| `entry:58` | `reply:79` | `reply:76` | shortest combat-tutorial response |
| `entry:71` | `reply:90` | `reply:88` | skip the optional explanation |
| `entry:73` | `reply:92` | `reply:91` | terminal acknowledgement |
| `reply:92` | generated Continue | none | close the opening conversation |

The selected opening branch plays nine installed voice/LIP records:

```text
entry:55 → 57 → 58 → 61 → 62 → 69 → 70 → 71 → 73
```

After corridor traversal, the existing Carth/Trask continuation plays four
more voice/LIP records and must perform exactly three blank-reply automatic
hops relative to the opening baseline. At `entry:35`, the route asserts
`END_CARTH_DLG=1`, `END_TRASK_DLG=11`, map revealed, and two choices. It selects
terminal `reply:50`, then its generated Continue.

The room-3 encounter contributes the final two voiced Trask entries, for 15
distinct installed voice payloads over the complete route. Player responses
are not fabricated or voiced by the runtime.

## Phase state machine

`-ShowcaseRoute` advances only after each phase's contract passes:

1. **OpeningDialogue** — five selections, door 01 open, conversation closed.
2. **Gear** — use the exact opening footlocker, award XP 50, equip Clothing and
   Short Sword through profile-owned inventory/equipment state.
3. **Corridor** — wait 60 frames for the equipment presentation, then submit the
   same 10 m walkmesh-constrained movement used by the isolated trigger proof.
4. **Transmission** — four voices, three automatic control hops, two terminal
   selections, both globals and map state asserted.
5. **EncounterLeadIn** — hold gameplay for 60 frames, then begin room 3.
6. **Encounter** — require the full door/camera/voice/music/projectile/effect/
   environment combat-ready assertion.
7. **FinalHold** — wait for gameplay release and standard area music, then hold
   120 frames before the final state assertion.
8. **Complete** — expose stage key `showcase:complete` for still or movie exit.

Final state requires both doors open, XP 50, map revealed, globals
`END_CARTH_DLG=1` and `END_TRASK_DLG=1`, Armor and RightHand equipped, opening
choices 5, transmission choices 2, at least 15 voices, first-encounter pass,
cinematic authority released, and `mus_theme_sith` restored.

## Presentation and capture behavior

Loading/status controls and proof telemetry use a separate CanvasLayer from the
dialogue panel. Clean showcase capture can hide the developer header and control
legend after module readiness while retaining subtitles and authored choices.

Choice, equipment, lead-in, and final holds are frame-counted. Voice progression
uses audio-finished signals. Outside Movie Maker, total frame count varies with
the renderer's real frame rate; under `--fixed-fps 60`, the non-audio holds are
deterministic and recorded audio remains the progression authority.

Desktop and active Meta XR Simulator runs both reached `showcase:complete` with
no engine error. Observed complete-route telemetry:

```text
choices=5+2
voices=15
xp=50
music=mus_theme_sith
route=boot->opening->gear->corridor->transmission->encounter->gameplay
```

The active-XR run additionally proved the shared-world spectator path, exact
three-hop transmission gate, first-encounter gate, and non-black final capture.

## Limits and video gate

- This is the shortest selected authored branch, not every opening dialogue
  option or tutorial explanation.
- General combat AI, damage resolution, saves, arbitrary inventory, and later
  modules remain outside this route.
- The active-XR final gameplay view follows the authored player waypoint bearing
  and tracked first-person camera; it is intentionally not the desktop
  third-person composition.
- Vulkan/OpenGL tone remains an explicit renderer delta and visual parity is not
  claimed without retail telemetry.
- No movie was produced while implementing or testing this route. The single
  final MP4 remains blocked until the exact final code passes the complete
  desktop/XR route plus facial, opacity, audio, fallback, and tracked-media
  audits.
