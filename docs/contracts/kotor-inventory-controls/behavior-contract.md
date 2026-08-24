# KOTOR flat inventory-control contract

## Scope and evidence

This slice completes the source controls needed to switch the opening player
inventory between normal and quest-item views and to navigate an overflowing
item list. It extends the existing native inventory presentation; it does not
invent a category bar that is absent from KOTOR 1's `inventory.gui`.

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- `inventory.gui` SHA-256:
  `ADBFC1BBDE9B8831FBF5FBB39BCF5EFBBDF27BF069CFB5C9B0C4BC9CCEF6BFF1`.
- Scroll direction texture `uparrow` SHA-256:
  `9ECA38CEFD4F5CDD9958B5ADFEE5F956F8DCFF9211298186E396F0D6E96D8DDF`.
- Scroll thumb texture `bluefill` SHA-256:
  `C9209DBC4CF5E9F8C3DDC72F3BD86764FAB9DC5704AA0B65B736CB895B4FCC14`.
- `Quest Items` is source TLK reference 32182; `All Items` is reference
  41822.

The importer records each opening UTI's `Plot` bit alongside its icon, name,
description, and source hash. Medpac, Clothing, and Short Sword all have the
bit clear in the owned opening locker.

## Filter behavior

The source `BTN_QUESTITEMS` control toggles between two views over the same
profile-owned inventory snapshot:

1. normal mode includes every materialized item stack and labels the button
   `Quest Items`;
2. quest mode includes only records whose installed UTI has `Plot` set and
   labels the return action `All Items`; and
3. changing mode clears the stale selection, description, use action, and
   scroll offset before rebuilding the list.

The deterministic opening replay proves `3 all -> 0 quest -> 3 all`, including
both source button labels, without adding a synthetic quest item to gameplay.

## Overflow and input behavior

The item prototype remains 245x50 at its source extent. A clipped scroll
viewport now owns the row container, so rows cannot bleed into the party or
bottom-button regions. The source scrollbar stays hidden when the list fits.
When content exceeds the 290-pixel viewport, it materializes:

- `uparrow` at each end, with the lower image vertically flipped;
- a `bluefill` thumb whose size reflects visible/content height;
- one-row arrow-button movement with clamped first/final positions;
- an invisible input slider over the authored track for thumb dragging; and
- normal `ScrollContainer` wheel/focus scrolling.

The owned opening state has only three item types and therefore correctly
shows no scrollbar. A clearly labeled acceptance simulator repeats those three
already imported UI records three times without changing gameplay state. It
proves nine clipped rows, a 160-pixel range, arrow movement, thumb movement,
drag-to-first, drag-to-last, and bottom clamping. Simulator output and captures
remain ignored local artifacts and are not retail-state evidence.

## Claim boundary

The normal and quest views, source scrollbar geometry, clipping, and input
controller are confirmed for this bounded opening inventory. Importing every
item the full campaign can later place in party inventory, party-member
switching, quest-item acquisition flows, arbitrary item-use effects, item
sorting, persistence, and matched live-retail telemetry remain separate gates.
