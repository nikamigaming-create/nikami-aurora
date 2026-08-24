# KOTOR flat loading, HUD, and inventory contract

## Scope

This slice replaces the proof overlay with locally imported KOTOR presentation
data for three bounded flat-screen paths:

- the Endar Spire loading screen;
- the 800x600 gameplay HUD; and
- the opening inventory screen and basic Medpac use.

The public tree contains readers, layout records, runtime code, and synthetic
acceptance tests only. GUI resources, textures, portraits, fonts, music, and
captures remain under ignored `local/` and `artifacts/` paths.

## Owned-source baseline

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- `loadscreen.gui` SHA-256:
  `7E073C2E95319943A9C78D338669A92B1D5C59F2B8764D952B03BA7D6D91D6ED`.
- `inventory.gui` SHA-256:
  `ADBFC1BBDE9B8831FBF5FBB39BCF5EFBBDF27BF069CFB5C9B0C4BC9CCEF6BFF1`.
- `top.gui` SHA-256:
  `7E1535D9BC3A4194E77BC29192463D4C615D8C16543E54E898C81C5E4AE30F57`.
- `mipc8x6.gui` SHA-256:
  `CE6E31B50AE73ED59ABC0B806418901873AE5A320B6A0F2D25749EB0DF73A125`.

The importer resolves every referenced texture through the installed game's
normal resource precedence and records both its source and derived PNG hashes.
The logical `fnt_d16x16` font resolves to the installed Windows
`fnt_d16x16b` alias (source SHA-256
`FA9DADFC2E8318567CC59F33EA2A11E6F0F58C61F42F0C92B280E74AD50427AD`).
Its deterministic Godot bitmap-font descriptor has SHA-256
`B911C3FB3B2895FFCB32A394636ABEE0E71D42B7763079EE728C46FBF4652A70`;
the corrected atlas is rendered at the logical source height of 10 pixels.

## Loading presentation

The source layout is 640x480. It binds:

- `load_end_m01aa` (source SHA-256
  `4DB1100D85124AF861E355EDCE0AE316CB46A40D28AD9184B67E774B24E1B23D`);
- `logo_sw_02` (source SHA-256
  `030A2B8F281B8129591621F15904ABE07EC6D078C29AEB20BE027CE2A1C90822`);
- the source progress-bar extent and `bluefill` texture;
- loading label string reference 42493 and hint string reference 38103; and
- `mus_loadscreen` (encoded source SHA-256
  `D75C63DE9683EF0F3A47FBAAF9EC03225A46869C6E334200018B7D83E20D2E98`,
  validated decoded payload SHA-256
  `07D32AAA841ABF7A14307D09CDBE71C8EDC8F47420D1D600799A179CA6BE7ADA`).

The exact Endar source image is the monochrome ship corridor. No unrelated
character artwork is substituted. Progress advances during local room import,
and loading music stops when the module becomes ready.

## HUD presentation

The runtime materializes `mipc8x6` at its native 800x600 reference viewport.
Static frames and icons come from source GUI fills, while the active portrait,
vitality bar, force bar, minimap texture, minimap transform, and player arrow
are live controls. The Endar minimap texture source SHA-256 is
`924CF4AB4D9B0D08FC6C2E02572208FC87FCBB843A084BBDB480AF3727E5F14C`.
The vitality bar reads the same profile-owned vitality snapshot shown by the
inventory screen.

Deterministic flat capture modes force both the window and Godot content-scale
viewport to 800x600. A 16:9 internal canvas is therefore not accepted as an
800x600 HUD result.

## Inventory behavior

The runtime uses the 640x480 `inventory` layout and 640x86 `top` toolbar,
including the source selected-inventory highlight, background, stats frames,
player and Trask portraits, item prototype geometry, owned icons, descriptions,
and source button labels. The opening locker supplies exactly two Medpacs, one
Clothing item, and one Short Sword through the profile-owned inventory state.

`Use Item` does not equip Clothing or the Short Sword. Equipment remains a
separate screen/transaction. For the currently supported basic Medpac:

1. full vitality disables use and cannot consume a stack;
2. healing is `10 + Wisdom modifier + Treat Injury skill`, with a minimum of
   one point and a maximum of the character's vitality cap;
3. one item is removed atomically; and
4. the transition emits a typed item-use event containing before/after quantity
   and vitality.

Credits, current/maximum vitality, and defense are read from the gameplay
snapshot rather than duplicated UI constants.

## Gates and claim boundary

The owned-data runtime produced clean 800x600 captures for loading, HUD, and
inventory with empty Godot error logs. `dotnet build` and the source-free
acceptance executable pass; the latter covers full-health no-op behavior,
Medpac quantity/vitality changes, the healing formula, and rejection of
non-medical items.

These gates confirm source binding, deterministic layout, and the bounded
inventory transition. They do not yet establish complete retail visual parity.
Matched live-retail capture rows, resolution-specific HUD selection beyond
800x600, the full campaign party/item corpus, party join/leave and
controlled-character flows, arbitrary item effects, combat HUD state,
save/load, and character-generation-derived statistics remain separate
contracts. Opening Inventory party selection is specified by the
inventory-party contract. The opening Quest Items toggle and list scroll
controller are specified by the inventory-controls contract. The opening-player
equipment screen is specified separately by
`docs/contracts/kotor-equipment-ui/behavior-contract.md`.
