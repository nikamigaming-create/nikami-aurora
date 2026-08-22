# KOTOR first-corridor trigger and Carth transmission contract

## Target and source evidence

- Game: Steam KOTOR 1.0.3.0.
- Executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- Module: `end_m01aa`.
- Trigger template: `end_trig02`.
- UTT SHA-256:
  `E410351B8A8EFF1C139ABC477D066E77463B56C5985987BB9072A780F36FD8B5`.
- OnEnter script: `k_pend_trig02`, 991 instructions, SHA-256
  `99C7AF5868DAEADD96C6027BF7912B6904855990308BAAFFFD6DFBF732AB67BA`.
- Trask OnUserDefined script: `k_pend_trask_d`, 1,301 instructions,
  SHA-256
  `28CC82593A0133962B6D2AEC0BA1C1C6B0182918756ECDE533B9112D23F3029A`.
- Starter condition: `k_pend_traskdl14`, 763 instructions, SHA-256
  `FCAA7779E5DA5D86C570ECDF6EB0AE488B571CA308816A955D288E5A659F38EB`.
- Dialogue `end_trask01` SHA-256:
  `F65EF68C0FB56382F07433151A0F75939AE33E69FBE102D1C1BB13B6476CFF41`.

Decompiler text is not implementation input. The importer validates raw NCS
instruction/action sequences, constants, source hashes, trigger metadata, and
the installed DLG starter record.

## Trigger geometry

The UTT supplies `OnEnter=k_pend_trig02`, tag `end_trig02`, and height 3.0.
GIT position plus its local polygon yields these native world points:

```text
(24.519506454, 23.692136765, -0.958454883)
(24.583787918, 17.811786652, -1.027556635)
(28.517141342, 16.838779449, -0.251498997)
(28.235795975, 24.547742844, -0.263391554)
```

`Profiles.Kotor` owns segment-versus-polygon crossing so a long accepted step
cannot skip a narrow trigger. The trigger is one-shot and receives a stable
`trigger:0000` placement ID. Desktop and OpenXR movement therefore enter the
same native-coordinate trigger path.

## Script and dialogue outcome

For a party member entering this trigger, the validated bytecode outcome is:

1. set global number `END_TRASK_DLG` to 10;
2. resolve actor tag `end_trask`;
3. suppress click input for 0.5 seconds;
4. after 0.1 seconds signal user event 50 to Trask; and
5. Trask clears actions and starts his default conversation `end_trask01`.

Condition `k_pend_traskdl14` compares `END_TRASK_DLG` with the initialized
constant 10. It selects DLG starter 8. That starter is camera 1, speaker
`Carth`, sound `nm01aatras02057_`, and the authored “All hands to the bridge”
transmission. The installed playable audio is 45,144 bytes, SHA-256
`F9FD9BC2306476F33575EEE4571179565EBE7C6664045C69D39FD706B81BBE35`;
its LIP has 58 frames and SHA-256
`A5414EA825DE77A5E8D3C358B8204D375B0993B4044B3F96F4A234F66880B777`.

The profile snapshot owns the one-shot trigger bit and global number. It emits
typed trigger, global, and dialogue-request events. Godot schedules the source
delay/input lock and presents the requested starter; it does not independently
choose global 10 or starter 8.

## Carth actor and camera

Carth resolves from `p_carth001.utc` SHA-256
`84ECC167333AEA1DB9189D6D9DDEE80EFF853211449BC39AF994D76CFFD45B36`:

| Source | Model/texture | MDL SHA-256 | MDX SHA-256 |
|---|---|---|---|
| Body | `P_CarthBB` / `P_CarthBB01` | `CA3F328CF5CA3317003134F1BF281B250EC4392E49B306634452D8D0B666B4DB` | `81A7812D07C57C05DF9DFDC7E5E4115CFD5AEE884C1D54AE009AE7E546AD5585` |
| Head | `p_CarthH` | `D5623396660859067017614EC0AA255FF3270D122E81188D8BC47AAB3EA7148D` | `E9F60A76942A70433DBDC3E51F5F48E1A45C4A10F25D443FAC13C106E6FC97EA` |

The ignored actor GLB reports 13 meshes, 2,242 vertices, 2,681 triangles,
six skins, two independent head skins, and `pause1`, `tlknorm`, `walk`, and
`talk`. Tag alias `Carth` points dialogue animation/LIP state at this actor.

Camera 1 uses its authored position, height, GFF WXYZ orientation, X-axis pitch,
and FOV 55. Desktop cinematics use a separate root `Camera3D`; the gameplay
SpringArm cannot overwrite the pose on subsequent frames. OpenXR continues to
set the HMD-relative cinematic base instead.

## Retail oracle

Stock retail footage at approximately 02:32–02:40 confirms that Carth is
**standing**, not seated, for this radio transmission. By the end of the line,
retail uses the same waist/chest-up, right-of-center composition with a bridge
chair and viewport behind him. See the
[recorded retail walkthrough](https://www.youtube.com/watch?v=UN7xzhqFzp0&t=152s)
and an independent
[retail transmission screenshot](https://minireview.io/role-playing/star-wars-kotor).

Retail frames are local research oracles only and are not tracked. The current
port-side capture matches standing `tlknorm`, actor scale, camera side, and
background relationship. Retail blue transmission grading, exact UI styling,
starfield/TXI behavior, and sub-frame cut timing remain presentation deltas.

## Verification and remaining work

Confirmed in synthetic and owned-runtime tests:

- segment crossing fires the actual trigger once and cannot refire;
- global transition `END_TRASK_DLG 0→10` persists in the profile snapshot;
- event 50 requests `end_trask01` starter 8 after 0.1 seconds;
- camera 1 holds independently of SpringArm updates;
- installed Carth voice and 58-frame LIP start on the assembled actor;
- Carth is standing with `tlknorm`, matching retail posture;
- source-opaque room materials remain audited;
- no-HMD Oculus OpenXR falls back cleanly through the same progression path.

The rest of starter 8, Trask's response, exact transmission color/UI, true
physical-HMD acceptance, Trask follower movement, and the following combat
encounter remain subsequent isolated contracts.
