# KOTOR flat inventory-party contract

## Scope and evidence

This slice turns the opening Inventory party portraits into source-positioned
controls backed by profile-owned selection and vitality state. It covers the
player and Trask on the Endar Spire. It does not invent a full campaign roster
or claim party-member equipment support.

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- `inventory.gui` SHA-256:
  `ADBFC1BBDE9B8831FBF5FBB39BCF5EFBBDF27BF069CFB5C9B0C4BC9CCEF6BFF1`.
- `portraits.2da` SHA-256:
  `4081846F2D3D5E1F43BFF728960A424E12D5D91450273DD012973C94DF088461`.
- `end_trask.utc` SHA-256:
  `24B574D46698F75314BA3F6ABADCBAE54F74FF71A2986EDC68F683C5D42DE840`.
- Trask portrait `po_ptrask` source SHA-256:
  `35E4103E81F05BC6C4B4AEB02A9D9D4C3CEA53214AF8DC9C146E1B75D3297C64`.
- Equipped `g_a_clothes01.uti` SHA-256:
  `FC8AB4485644BEC2FAE71C99BBD8853170C1A5D739953B62EB95266173443CF1`.
- `baseitems.2da` SHA-256:
  `E9D031FAF0A5D3D4E9CCF33AEE5233FDA8F781A58B30FA722E7CF12B78C85C95`.

The `BTN_CHANGE1` and `BTN_CHANGE2` extents come from `inventory.gui`.
Their design-time Zaalbar and HK-47 fills are placeholders; the importer
replaces them with the active opening roster's player and Trask portraits.

## Imported state

The player retains the existing profile baseline of 20/20 vitality and Defense
10. Trask is imported from `end_trask.utc` with 30/36 vitality, Dexterity 14,
natural AC 0, and equipped basic Clothing. The source tables yield:

`10 base + 0 natural AC + 0 Clothing base AC + 2 Dexterity modifier = 12 Defense`.

The Clothing row's Dexterity limit is -1, so the full modifier applies. The
importer records the UTC, armor UTI, base-items table, portrait table, and
portrait texture hashes with the party member instead of baking unexplained
numbers into the runtime.

## Runtime behavior

Selecting either source portrait emits a typed profile event and persists the
selected party-member ID. Inventory then refreshes the large source portrait,
vitality, Defense, selected portrait border, and Use Item availability from the
same snapshot. Inventory remains shared, as in this opening state. A Medpac
targets the selected member: full-health player use remains a no-op, while
selecting injured Trask allows the item and heals Trask without changing player
vitality. Switching back preserves companion vitality.

The source portrait controls support mouse and keyboard focus. Selecting a
party member in Inventory does not change the controlled world actor.

## Gates and claim boundary

The source-free acceptance suite proves player -> Trask -> player selection,
typed selection events, selected-member Medpac targeting, shared inventory
consumption, and independent vitality persistence. The owned-data runtime gate
uses:

```powershell
./scripts/Start-KotorGodot.ps1 `
  -InventoryScreen `
  -TestInventoryPartySelection `
  -CaptureAndExit
```

It asserts Trask's imported `30/36`, Defense `12`, enabled Medpac target, and a
native 800x600 final frame with empty Godot stderr.

The full campaign party roster, join/leave flows, active-character switching,
companion AI, companion equipment, companion model variants, per-member skills,
and matched live-retail telemetry remain separate gates.
