# KOTOR flat equipment-menu contract

## Scope and evidence

This slice materializes the player equipment menu from the locally owned game
and connects it to profile-owned inventory/equipment state. The public tree
contains only importer/runtime logic, hashes, tests, and this contract. GUI,
TLK, texture, model, GLB, and capture payloads remain ignored local data.

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- `equip.gui` SHA-256:
  `0045D8EA3DDF3B767F68E1D4BC66E98DE490193BF028F6E29ED87846FCEF8625`.
- `top.gui` SHA-256:
  `7E1535D9BC3A4194E77BC29192463D4C615D8C16543E54E898C81C5E4AE30F57`.
- Equipment background `lbl_equip` SHA-256:
  `A72FF47A4CE480219C7818F12014E3662D2E77E478F24C7122162846B522871A`.
- Empty-choice icon `inone` SHA-256:
  `B5FE3F88E98543E1E495D52A42BF9C2A06DB46510BBFF7F04DF2A51B348EEADB`.

`equip.gui` contains absent design-time party placeholders `po_mhk47` and
`po_mzaalbar`. The importer records those unresolved references and replaces
the portrait controls from live party state, as retail does; it does not invent
or distribute substitute textures.

## Native menu presentation

The 640x480 equipment and inventory resources remain at native size and are
centered inside an 800x600 viewport. They are not enlarged to fill the window.
The 800x600 gameplay HUD remains native to `mipc8x6`, while the loading screen
continues to use its full-viewport policy.

The equipment screen binds the source background, toolbar states, nine slot
frames, paper-doll icons, portrait/stat frames, list prototype, player and Trask
portraits, and OK/Close controls. Slot labels use TLK references 31375-31388;
`None` uses reference 363 and `Equipped` uses reference 32346. The current item
is marked as equipped, and OK is hidden until the user selects a different
choice.

The human paper-doll icons are owned textures selected by slot: `ihead`,
`iimplant`, `iarmor`, `ihand_l`, `ihand_r`, `iweap_l`, `iweap_r`, `ibelt`, and
`ihands`. An equipped item replaces only its matching empty-slot icon.

## Profile transaction and presentation variants

All nine slot buttons resolve to the source equip-slot bit masks. For the
opening locker, Armor offers Clothing and both LeftHand and RightHand offer the
Short Sword; other slots correctly offer only `None`. Equipping validates the
installed item definition and atomically moves one item from inventory into the slot.
Unequipping atomically removes the slot, returns one item to inventory, and
emits a typed removal event. Repeating either settled state is a no-op.

The local importer produces the exact visual combinations required by those
independent transactions:

- base body, no weapon;
- Clothing body, no weapon;
- base body with the Short Sword on `lhand`;
- Clothing body with the Short Sword on `lhand`;
- base body with the Short Sword on `rhand`; and
- Clothing body with the Short Sword on `rhand`.

Godot selects a variant from the resulting profile snapshot, validates its
UTI/base-items provenance and animation/skin contract, validates the generated
weapon hierarchy against the recorded `lhand` or `rhand` hook, preserves the
current locomotion clip, and updates the source camera hook. It does not infer
slot legality or attach the weapon with a guessed transform.

## Deterministic gates and claim boundary

The source-free acceptance suite covers invalid-slot rejection, atomic equip,
idempotent repeated equip, atomic unequip/return, and idempotent repeated
unequip. The owned-data runtime gate traverses base, Clothing, left-hand Sword,
Clothing plus left-hand Sword, right-hand Sword, and Clothing plus right-hand
Sword variants before capturing the final 800x600 equipment screen. A
hand-aware closeup confirms each single-weapon hook from its visible side. A
separate menu-navigation gate asserts mutual exclusion and
HUD hiding across HUD -> Equipment -> Inventory -> Equipment. Godot stderr must
remain empty.

This is a bounded opening-player menu contract, not a whole-game visual-parity
claim. Party-member equipment state, list scrolling under a large inventory,
item-detail mode, prerequisite failures, arbitrary armor/weapon models, derived
damage/to-hit calculations, dual-wield rules/variants, and matched live-retail telemetry
remain separate gates.
