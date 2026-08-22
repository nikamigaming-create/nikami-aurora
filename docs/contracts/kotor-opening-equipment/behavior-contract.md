# KOTOR opening equipment-presentation contract

## Evidence baseline

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- `appearance.2da` SHA-256:
  `955EA39F7D111CBB1099EAA52CC3ABB3FF91DBEDA231672774D902F1D3D60F2D`.
- `baseitems.2da` SHA-256:
  `E9D031FAF0A5D3D4E9CCF33AEE5233FDA8F781A58B30FA722E7CF12B78C85C95`.
- Clothing UTI SHA-256:
  `FC8AB4485644BEC2FAE71C99BBD8853170C1A5D739953B62EB95266173443CF1`.
- Short Sword UTI SHA-256:
  `9EC88EBA45CB0ED430483362121672F48CDD9C541ADFE4CF7442F76C14BFD652`.

The implementation consumes table/UTI behavior and source hashes. No retail
model, texture, item, or generated GLB is tracked.

## Source resolution

For player appearance row 137 (`P_MAL_A_MED_01`):

- Clothing base item 85 selects `bodyvar=B`;
- appearance columns `modelb` and `texb`, plus UTI texture variation 1,
  resolve body `PMBBM` and texture `PMBBM01`;
- the normal head remains row 41, model `PMHA01`; and
- Short Sword base item 4 and model variation 1 resolve
  `w_Shortswrd_001`.

Source model hashes:

| Model | MDL SHA-256 | MDX SHA-256 |
|---|---|---|
| `PMBBM` | `873DD2B3275D0C846FFAECF4E51BC685AFE3E865D5B266DD5F184AA28A9ECC12` | `6CCAB8D56537506142FA1E69F54F899A0B1D37CC1602C2BD9227E02AEC0C1DC0` |
| `PMHA01` | `BAFA3CECA6F3440FAF5687271CE78C1D90E7C5580D1A8E533A70F0E50F040A94` | `D18A5521E795F3721B3FE37878E6A70CBCA2335BE22AA229166ACA14F085123E` |
| `w_Shortswrd_001` | `0E6DA2E5CD4EF7569D1909B8867CD4930CEE2D767A9C1FE90B0359753C0E2E4C` | `9EFC89709DE14ABA0B9C7C62B99BC399026EDD4FBA520B8F76E3051569195E05` |

## Assembly and animation

The ignored equipped GLB keeps the Clothing body and head as separate skinned
models. The head remains beneath the body's `headhook` with its independent
bind space. The sword model remains a separate hierarchy beneath the animated
body `rhand`; no guessed hand transform is applied.

Export report:

```text
meshes=12 vertices=2432 triangles=2837 skins=5 headSkins=2
animations=pause1,walk,run
```

Godot rejects a variant without all three required clips, a body/head skin, or
UTI/base-items hashes matching the inventory definitions that produced the
equipment state.

## Profile transition and input

The profile owns Armor and RightHand slots. Equipping is an atomic transaction:
it validates the item's installed equip-slot mask and availability, removes one
unequipped instance from inventory, records the slot, and emits typed equipment
events. Invalid-slot requests cannot partially mutate inventory. Repeating the
same loadout is idempotent.

- Desktop: `Q` equips the opening Clothing and Short Sword after looting.
- OpenXR: `by_button` (`B`/`Y`) enters the same profile transaction.
- A successful XR action requests its pulse on the controller that initiated
  the equipment change.

Godot selects the source-bound variant from the resulting profile snapshot,
preserves the active `pause1`/`walk`/`run` locomotion state, updates the installed
camera hook, and never decides slot legality itself.

## Runtime and visual gates

Confirmed in the owned Endar runtime:

- equipped idle with `pause1` active and no T-pose;
- equipped `run` active across an accepted exact 1.5-meter move;
- Clothing texture/body and head skin remain intact;
- the Short Sword hilt is enclosed by the animated right glove and the blade
  begins at that hilt, with no floating or duplicated weapon; and
- a compact world-space `EQUIPPED` notice replaces the loot notice rather than
  overlapping it.

Physical-HMD input/haptic delivery remains hardware-gated. General equipment
menus, individual unequip/re-equip presentation variants, dual wielding,
combat stances/attacks, item stats, saves, and arbitrary armor/weapon model
coverage remain future contracts.
