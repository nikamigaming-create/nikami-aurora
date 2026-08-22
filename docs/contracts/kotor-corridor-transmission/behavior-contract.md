# KOTOR corridor transmission continuation contract

## Target and module scope

- Game: Steam KOTOR 1.0.3.0.
- Executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- Module: `end_m01aa` plus its own `end_m01aa_s.rim` story archive only.
- Dialogue: `end_trask01`, SHA-256
  `F65EF68C0FB56382F07433151A0F75939AE33E69FBE102D1C1BB13B6476CFF41`.

Script lookup is module-scoped. A referenced blank-reply script,
`k_pend_carth11`, is absent from `end_m01aa_s.rim` and appears only in the
next module's story archive. It therefore remains an explicit unsupported
event here; the importer does not silently borrow code from another module.

## Authored graph and media

Starter 8 enters this source sequence:

```text
entry:32 Carth voice
  -> reply:43 (empty)
  -> entry:33 Trask voice
  -> reply:44 (empty)
  -> entry:34 Trask voice
  -> reply:45 (empty, unavailable module-local script)
  -> entry:35 Trask journal voice
  -> reply:50 "Let's move out."
     reply:46 "How do I use my journal?"
```

The three empty reply records are control nodes, not player prompts. Aurora
advances through them only after the preceding installed voice finishes. The
blank node is still visited, so any module-local scripts and camera directives
would execute instead of being skipped.

| Node | Sound | Audio bytes / SHA-256 | LIP frames / SHA-256 |
|---|---|---|---|
| `entry:32` | `nm01aatras02057_` | 45,144 / `F9FD9BC2306476F33575EEE4571179565EBE7C6664045C69D39FD706B81BBE35` | 58 / `A5414EA825DE77A5E8D3C358B8204D375B0993B4044B3F96F4A234F66880B777` |
| `entry:33` | `nm01aatras02058_` | 49,896 / `59FA95B831CCD2882B1B440B5B263C9E9D16DD7682A2DDA7486CC19F5E10DB4A` | 78 / `AB84D55486948D302F3F19D5E4A7CE28834AB57F4B88FAABDED03B44673EA411` |
| `entry:34` | `nm01aatras02059_` | 30,240 / `34C398210BC4D2C59325EAEA5BDFB5AC548EABACA2C8C1349CC57BDB8DDDC868` | 43 / `8BAC67FB32447637BF62605E1FBB830894EF907DB98A011ED352DC164C6C593F` |
| `entry:35` | `nm01aatras02243_` | 33,696 / `C66D925EDB856DF253B0EEE29DB2710FE9CC89E49EB1AC56AB22AFF3B56FD6B7` | 59 / `E5BDFA7038B1CC30F397276C7218B01E5BCE14E683263AC954378D17E1B87F30` |

## Module-local script outcome

Raw NCS instructions, constants, action IDs, and source hashes establish:

| Script | Instructions / SHA-256 | Profile effect |
|---|---|---|
| `k_pend_cadlg_inc` | 14 / `9A3AE15D07F4A1B81A2774553CAA7271403A73829AE76E8CF40518C880A5E360` | `END_CARTH_DLG += 1` |
| `k_pend_traskdl47` | 751 / `66E79D8A179FC49721AC367822B393287E4E60034FD0F506767E6CE70E8C7D09` | `END_TRASK_DLG = 11` |
| `k_pend_map` | 8 / `3AE3A04CBA861141A7F12D729DFED129442FDEB424CEEC253EB37B2C4E30DD2A` | reveal the full module map |

The profile snapshot owns both global numbers and the map-revealed bit. Godot
only presents the typed transitions; it does not invent the values.

## Camera and retail oracle

Dynamic dialogue turns Trask and the player model toward each other before
framing. `CameraAngle=1` uses a deterministic tight-speaker shot; the following
angle-0 journal node preserves that shot because the speaker has not changed.
The camera remains independent from the gameplay SpringArm on desktop and uses
the same HMD-relative cinematic base under OpenXR.

The [recorded retail walkthrough](https://www.youtube.com/watch?v=UN7xzhqFzp0&t=159s)
shows no pause between Carth and Trask at approximately 02:39.87 and holds a
tight, left-of-center Trask shot through the journal line. A local ignored
640x360 oracle frame at approximately 02:44 has SHA-256
`53AD66BE101A29A0BB3D96DF2C74707083F0F9A0170CDB38168E5E2CE553CD73`.
The shape-0 node-keyed 1280x720 Aurora capture has SHA-256
`EE3AE2D43D37B64C7243BE46BAC8D59FBA0BB38EB3E6C02A0A169FEC10BAD769`;
the 1.93-second mid-voice capture is
`13F31059C9D112C963DCCCE5F39DB69904CD30D306E6836244BBFA9E21336425`.
The side-by-side confirms matching standing posture, crop, left/right actor
The retail comparison also caught and now rejects generic-supermodel mouth
collapse: LIP shapes are applied as shape-0-relative deltas over the unique
head rest, without changing the attached head hierarchy.

## Runtime acceptance

The deterministic `entry:35` probe verifies:

- automatic node sequence `32 -> 33 -> 34 -> 35`, exactly three transitions;
- Carth and all three Trask voice/LIP tracks begin on their assembled actors;
- `END_CARTH_DLG=1` and `END_TRASK_DLG=11` persist;
- the module map is revealed;
- angle 1 selects the tight Trask camera and angle 0 preserves it;
- Trask is the visible speaker and exactly two authored responses are offered;
- no T-pose, detached head, malformed hand, or opaque-room regression occurs.

The actual map-screen presentation, the optional journal explanation branch,
physical-HMD acceptance, and the following Sith encounter remain later
contracts.
