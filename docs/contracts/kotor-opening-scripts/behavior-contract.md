# KOTOR opening-script contract

## Evidence baseline

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- Module script archive: `end_m01aa_s.rim`.
- `plot.2da` row 47: label `end_tutorial`, base XP `1000`.
- `nwscript.nss` action 714 defines `GivePlotXP` as awarding a percentage of
  the XP associated with a plot label to the party.

Decompiler output is not implementation input. Contracts below are validated
against NCS instruction/action sequences, constants, source hashes, and runtime
experiments.

## `k_pend_traskdl40`

- Source SHA-256:
  `24DB7616BC7898A2617D4D4B63695EAAA2AFC7525AD9F8E9FED37891F03AAD96`.
- 25 instructions.
- Action sequence: `GetObjectByTag`, `ActionOpenDoor`, `AssignCommand`,
  `ActionPauseConversation`, `GetObjectByTag`, `ActionMoveToObject`,
  `ActionResumeConversation`.
- Door tag: `end_door01`.
- Move parameters: run enabled, range 1.0; the empty target-tag semantics remain
  unresolved.

Current runtime support resolves and opens the exact door. Conversation
scheduling and movement are logged but not yet executed.

## Plot-XP scripts

`k_pend_chest02` contains a verified branch:

```text
if first_player_xp == 0:
    award 5% of plot end_tutorial (1000) => 50 XP
```

`k_pend_door1xp` contains a verified branch:

```text
if first_player_xp == 50:
    award 10% of plot end_tutorial (1000) => 100 XP
```

The door script source SHA-256 is
`E5FB8F2960633D6EF7AD64FA470561CD3ADC61B30025574C8A4102B493B1B5A0`
and contains 753 instructions. The locker script source SHA-256 is
`D4BE97E82F2D551E1E41A0880373003CF1F69BD988B16183702AAEB5699AB3AA`
and contains 985 instructions.

## Runtime verification

- With player XP 0, the dialogue script opens `end_door01` and its OnOpen XP
  contract correctly skips.
- With player XP 50, the same authored opening executes the door contract and
  reports XP `50→150`.
- Contract import fails if any validated instruction/action sequence drifts.
- `end_locker01` is materialized from `footlker001.utp` and `PLC_FootLker`;
  opening it executes `OnInventory=k_pend_chest02` and reports XP `0→50`.
- The deterministic runtime chain executes locker then door contracts and
  asserts the complete XP transition `0→50→150`.
- Experience, door, and placeable-used values now live in profile-owned
  before/after snapshots. Godot consumes typed transition events and no longer
  owns parallel XP or interaction booleans.

Source values, action IDs, plot math, target door, and both XP branches are
`confirmed`. Party-wide XP propagation, empty-tag movement semantics, action
queue timing, and physical door animation remain `unknown` or `probable` until
their next isolated experiments.
