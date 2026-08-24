# KOTOR Endar footlocker inventory contract

## Evidence baseline

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- `footlker001.utp` SHA-256:
  `887CF2443EFB93A069B0AFE1E7A7BC8C4B525797F061BAD98B749C33F33906AC`.
- `baseitems.2da` SHA-256:
  `E9D031FAF0A5D3D4E9CCF33AEE5233FDA8F781A58B30FA722E7CF12B78C85C95`.
- The UTP tag is `end_locker01`, `HasInventory` is true, and its `ItemList`
  contains four entries.

The owned-install importer resolves each `InventoryRes` through normal KOTOR
resource precedence, validates its UTI, and writes metadata only into the
ignored local module manifest. It does not publish the UTP, UTI, TLK, 2DA,
icons, models, or textures.

## Authored contents

| Quantity | Resref | Local English name | Base item | Equip slots | UTI SHA-256 |
|---:|---|---|---:|---:|---|
| 2 | `g_i_medeqpmnt01` | Medpac | 55 | 0 | `A6449C3EA78042B3E0B09440EAFEAA209C5AA207DE0AFA0CFBCC9296583D9972` |
| 1 | `g_a_clothes01` | Clothing | 85 | 2 | `FC8AB4485644BEC2FAE71C99BBD8853170C1A5D739953B62EB95266173443CF1` |
| 1 | `g_w_shortswrd01` | Short Sword | 4 | 48 | `9EC88EBA45CB0ED430483362121672F48CDD9C541ADFE4CF7442F76C14BFD652` |

Duplicate UTP entries are aggregated into a quantity without discarding their
droppable/infinite flags. Display names are resolved from the user's local TLK,
so another installed language remains local rather than being hard-coded. The
profile validates both each UTI hash and the `baseitems.2da` hash behind the
derived slot/model metadata.

## Profile transition

For the current opening slice, using the locker performs one deterministic
take-all transaction:

1. mark only `placeable:0000` used;
2. add the three resrefs and exact quantities to the profile-owned player
   inventory snapshot;
3. emit a typed item-transfer presentation event; and
4. execute the validated `k_pend_chest02` inventory-disturbed contract.

A repeated interaction emits `already-open` and cannot duplicate items or XP.
Infinite source stacks are rejected until their engine semantics have an
isolated contract.

## Shared presentation

Godot renders the item-transfer event as a compact billboarded world-space
readout 1.8 meters in front of the active desktop camera or HMD view. It is not
a desktop-only `CanvasLayer`. When OpenXR is active, the same event requests a
short pulse through the versioned `haptic` action on the controller that
performed the interaction (falling back to the right controller for automated
runtime tests).

Owned-install runtime evidence:

```text
NIKAMI_AURORA_INVENTORY status=transferred source=placeable:0000 items=2x Medpac, 1x Clothing, 1x Short Sword
NIKAMI_AURORA_NCS status=executed script=k_pend_chest02 ... xp=0->50
```

The transfer and desktop world-space presentation are `confirmed`. Physical-
headset readability and haptic delivery remain hardware-gated. Basic Medpac
use and its flat inventory presentation are specified by the flat-UI contract.
Selective container transfer, arbitrary item effects, general equipment
menus/variants, party inventories, and persistence are not claimed by this
slice. The isolated opening Clothing/Short Sword equipment path is specified
separately.
