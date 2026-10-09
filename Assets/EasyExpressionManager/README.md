# Easy Expression Manager

**v2.3.0** — a Unity Editor window for managing VRChat avatar expression menus in one tree: native VRC menus, VRCFury Toggles, VRCFury Full Controllers, and plugin-only virtual menus.

Open with **Tools → Easy Expression Editor**.

This project is distributed as the VPM package `com.neoemberarts.easy-expression-manager` and can be installed through the VRChat Creator Companion. VRCFury is optional: native VRChat expression menus work without it, while VRCFury-specific scanning and operations require VRCFury to be installed.

The safety fixes and their verification results are recorded in [Tests/VALIDATION.md](Tests/VALIDATION.md). Virtual menus now participate in Undo, each avatar instance has separate persistent history, and restore tracks components created by the plugin. Moves use the installed VRCFury's path and merge rules, with dependency ordering applied before its avatar build preprocessor.

---

## What's new in 2.2.0

- **Auto icon**: press **Auto** under any item's icon (closed by default) to photograph the item itself. Live preview (drag to orbit), angle buttons, full texture / silhouette / shaded / outline-only styles, transparent or coloured background, optional outline. What to photograph is picked automatically, trying in order: the objects a VRCFury toggle or slider turns on (or, for VRC menu controls and Full Controller items, the objects their animation switches on), an object on the avatar with the same name as the item, the meshes it changes (material swaps are applied), and finally where its component sits. Only objects that actually have a mesh are picked. A folder uses the first item directly inside it, or an object named like the folder. The panel says which rule found the object; drop any object on the Object field to photograph that instead. Snap saves a PNG to `Assets/EasyExpressionManager_Generated/Icons/<avatar>/` and sets it as the icon. Snapping again replaces the icon the generator made for that item ("Overwrite previous", the default); switch the Snap dropdown to "Save as new file" to keep every version. Your own textures are never overwritten.
- **Dissolve folder** (right-click, or the details pane): moves everything inside up one level exactly as it is and removes the folder. Snapshot first, one Undo step.
- **Same-name items are allowed**, matching VRChat. A quiet grey ×2 marks them. Actions VRCFury applies by name (moving or re-iconing a folder or Full Controller item) explain that they'd affect every item with that name. Moving a folder onto a same-name folder asks first, since VRCFury always combines those.
- **Name formatting**: bold and colours from menu names are shown in the list (sizes and other tags are hidden). Turn it off under **Design → Show Name Formatting**.
- Fixed: a "delete at build" move record (blank To Path) no longer rewrites every other move record on the avatar.
- **Keyboard navigation**: arrow keys move through the list like the Hierarchy (→/← open, close and step in/out of folders), Home/End jump to the ends. F2 renames, Delete deletes.
- **Messages appear in a footer** at the bottom of the window instead of over the menu. Warnings (like an unresolved move record) stay until you dismiss them.
- **Safer snapshot restore**: components are matched by stable or current-session identity, then by hierarchy and component order, then by a unique object name. Deleted components are recreated before their references are resolved. Plugin-created additions are removed; manual additions are kept and listed. The separate **Restore and remove untracked additions** action can remove duplicates from older versions, and also removes manual additions. New snapshots distinguish same-named siblings and menu subassets.
- Fixed: dissolving a folder also removes that folder's own icon record.
- Fixed: selecting or previewing an item no longer unpacks its prefab. VRCFury edits use prefab overrides, preserving the avatar and nested prefab connections. Renaming a folder only touches the toggles whose path really changes.

---

## What's new in 2.1.0

- **Split View** design (now the default): the menu list on the left, the selected item's details on the right. Classic is still available from the **Design** dropdown.
- **+ Virtual Menu** moved from the toolbar to right above the *Expression Menu* row.
- The window keeps a minimum width so no buttons get cut off.
- Virtual menus can have an icon set from the edit panel.
- Moving a folder no longer breaks the move records of items already moved out of it, leaves the folder itself behind, or orphans the icon of a virtual menu inside it.
- Virtual menus inside a moved folder can be renamed, moved and deleted again.
- Moves the tool can't do explain why instead of printing a stack trace.
- Debug log prints only new issues per operation; restore warnings are no longer duplicated.

---

## Installation

Drop the `EasyExpressionManager` folder anywhere inside `Assets/`.

> **Upgrading from any earlier version:** delete the old `Editor/` folder entirely before copying in the new one. The internal file layout has changed more than once (a single file in v2.1.0, ten files in v2.2.0/v2.2.1, forty-odd smaller files as of v2.0.0) and leftover files from an older layout will cause duplicate-type compile errors.

Requires VRChat SDK3 (Avatars) and VRCFury.

---

## File structure

```
EasyExpressionManager/
├── Editor/
│   ├── Core/
│   │   ├── EEM_ExpressionNode.cs         tree node model
│   │   ├── EEMAvatarState.cs             persistent avatar identity, virtual menus, ownership
│   │   ├── EEM_MiniJson.cs               dependency-free JSON reader/writer
│   │   ├── EEM_Paths.cs                  path escaping, comparison, VRCFury conversion
│   │   └── EEM_SerializedPropertyExt.cs  tiny SerializedProperty helper
│   ├── Dialogs/
│   │   ├── EEM_PathFieldGUI.cs           shared autocomplete path field
│   │   ├── EEM_MoveDialog.cs
│   │   ├── EEM_PickFolderDialog.cs       multi-select move destination
│   │   ├── EEM_DuplicateDialog.cs
│   │   ├── EEM_CreateFolderDialog.cs
│   │   ├── EEM_TextPromptDialog.cs       generic prompt (snapshot label, etc.)
│   │   └── EEM_AboutPopup.cs
│   └── Window/
│       ├── State/       window fields, lifecycle, storage, tree-bound path helpers
│       ├── Scan/         read-only tree building — one file per source kind
│       ├── Moves/       MoveMenuItem record engine and build ordering
│       ├── Ops/          rename / move / delete / duplicate / icon / bulk — one file each
│       ├── Snapshot/    storage, capture, resolve, restore, menu UI
│       ├── Structure/   structure export & import
│       ├── Debug/       operation logging & verification
│       └── GUI/          toolbar, rows, edit panel, events, context menu, cosmetics
├── Tests/                standalone and isolated Unity regression runners
└── Data/                 created on first use — snapshots, avatar state
```

The window is split across `partial class EASYExpressionManager` files. `Window/State/EEM_Window.Fields.cs` declares the `EditorWindow` base, and `Moves/EEM_MoveBuildOrder.cs` also declares the SDK build callback.

`Data/` holds your snapshots and avatar state assets, including virtual menu definitions, component ownership, and kept-record warning choices. It lives with the project, so it survives an editor restart and travels with the repo. Keep it and its Unity `.meta` files together. Legacy virtual-menu JSON files are retained during migration.

---

## Designs

Pick a design from the **Design** dropdown at the top of the window. It switches live and is remembered per user. Both designs edit the same data and share the same shortcuts, drag & drop and right-click menu.

- **Split View** (default): the menu list on the left, everything about the selected item on the right. Drag the divider to resize the two sides.
- **Classic**: the original single-column layout.

**+ Virtual Menu** sits right above the *Expression Menu* row in both designs.

The window won't shrink below the width each design needs, and a floating window that opens too narrow is widened automatically, so no buttons get cut off. In Split View, Alt-click a folder arrow to open or close the whole branch.

---

## What the badges mean

| Badge | Meaning |
|---|---|
| `VRC` | Native VRC Expressions Menu control |
| `Fury` | VRCFury Toggle component |
| `FC` | Control inside a VRCFury Full Controller menu |
| `FC⌂` | Folder created by a Full Controller entry *prefix* |
| `VM` | Virtual menu — plugin-only, no VRC asset |
| `→` | Position is managed by a Move Menu Item |
| `×N` | N separate components resolve to this same path |

A folder's name turns **red** with a `⚠ N/8` counter when it holds more than 8 controls, because VRChat only shows 8 per page and spills the rest onto extra pages.

---

## Features

### Snapshot / Restore
`Snapshot ▾` in the toolbar.

A **baseline** snapshot is taken automatically the first time you select an avatar, so there is always something to fall back to. Take more whenever you like. Automatic snapshots before Dissolve keep the newest 10 files within an 8 MB budget per avatar (the latest is always kept). Baselines and named snapshots do not rotate. Opening the window checks only the selected avatar?s history and known legacy identities, without parsing snapshots for other avatars.

Restore reconciles the captured components and menus:

- plugin-created components added since the snapshot are removed
- manual additions are kept and listed, unless **Restore and remove untracked additions** is selected
- deleted captured components are recreated on their surviving objects
- captured component fields, menu controls and virtual menus are restored

Deleted toggles come back when their owning objects still exist. Object references use stable ids, asset GUIDs with subasset ids, and indexed hierarchy routes. A missing object or asset is reported; a snapshot cannot reconstruct data that was already missing when it was captured.

Snapshots are JSON under `Data/Snapshots/<avatar-key>/`. Restore is a single Undo group. PNG pixels and prefab packing are outside snapshot restore: deliberate overwriting of an item's unique generated PNG remains enabled, and an unpacked prefab stays unpacked.

### Structure Export / Import
`Structure ▾` in the toolbar.

Export records where everything ended up. Import applies that layout to a *different* avatar, matching by name: exact path first, then a unique name match anywhere in the tree. Virtual menus the layout depends on are created automatically; anything the target avatar doesn't have is listed and skipped.

Nothing is written until you review the plan and press **Apply Checked** — every row has a checkbox.

### Multi-select
Click a row normally, then Shift-click above or below it to select the entire visible range. Ctrl/Cmd-click toggles individual rows; Ctrl/Cmd+Shift-click adds a visible range to the current selection. Collapsed and filtered-out rows are excluded. You can also right-click → *Select Multiple*. Then:

- **Move Selected…** — pick one destination folder, everything keeps its own name
- **Icon → Apply to N** — bulk icon assignment; **Clear Icons** removes them
- **Delete Selected**

Dragging any selected row drags the whole selection.

A drop into one of the selected folders, or inside one, is rejected. A complete same-name group can move together; partial selection cannot move an unselected same-name item by accident. Unnamed GoGoLoco/native controls retain their blank names when moved, including when moved back to root.

### Gesture Manager

When Gesture Manager is installed, **Enter Play-Mode** selects the first manager in the loaded scene hierarchy, creates one from the installed prefab if none exists, and enters play mode. This button appears in both window designs. Without Gesture Manager installed, the button is hidden and no object is created.

The installed Gesture Manager 3.9 inspector also gets a centered **Easy Expression Manager [by Neo]** button below its existing controls. It opens this window without changing Gesture Manager's package scripts.

### Keyboard shortcuts
| Key | Action |
|---|---|
| `↑` / `↓` | previous / next row (the list scrolls to follow) |
| `→` | open a folder; if it's already open, step into it |
| `←` | close a folder; otherwise jump to its parent |
| `Home` / `End` | first / last row |
| `Delete` (`Cmd`+`Backspace` on Mac) | delete selection, or the highlighted row |
| `Ctrl`/`Cmd` + `D` | duplicate |
| `Ctrl`/`Cmd` + `A` | select all (in selection mode) |
| `F2` | rename |
| `Escape` | leave selection mode / cancel rename |

All of them are ignored while you're typing in a text field.

### Debug log
`Debug JSON` in the toolbar. When `DebugJSON` is enabled, every mutation is executed, then verified against a fresh scan of the real underlying data, and any inconsistency is recorded with an explanation of *what* caused it. Copy to clipboard or save to a file.

Turn off *Include full tree in every entry* if the log gets unwieldy.

---

## Things worth knowing

**Prefab connections are preserved.** VRCFury edits use serialized property overrides and Undo component operations, including on nested prefab instances.

**Menu edits are private to the avatar.** Native menus and Full Controller menus are copied into `Assets/EasyExpressionManager_Generated/<avatar>/` before editing shared assets. Snapshot restore reconstructs private menu assets and repoints the avatar?s descriptor and VRCFury references, preserving shared package assets.

**The list follows external edits.** Inspector and hierarchy changes refresh the list automatically. If a change arrives while an action is pending, the action is refused and the list refreshed so it can be retried with current data.

**Build ordering is best effort.** If move ordering fails or detects a cycle, the upload hook logs a warning and leaves the original records for VRCFury to process in its normal order.

**Cleanup is reviewed before removal.** Unresolved or redundant move and icon records are listed with their paths and owners. Cleanup checks records throughout the avatar, including manual ones. Review the list before removing them: some target menus that only exist at build time. Records that successfully applied before another move changed their paths are retained. Choosing **Keep them** retains the records and remembers that their warnings should be ignored, including after snapshot restore. Editing a kept record to target a different path makes its warning eligible again.

**Paths follow VRCFury's matching rules.** Exact names take priority; case, spaces and rich-text fallback apply when no exact match exists. Literal `/` characters are escaped, and duplicate ancestor folders use `.dup.N` selectors. Final same-named controls can still be affected together by a VRCFury move or icon record.

**Merged moves are checked before saving.** The plugin retains each control's original source and verifies that the proposed move records reproduce the requested layout. When a merged folder needs its existing move sequence preserved, the records keep that sequence. Returning to a vacated folder path may use temporary staging records; the temporary name is removed in the final menu.

**Debug distinguishes state warnings from failed edits.** Pagination and already-unresolved records remain visible without failing an unrelated operation. Restore can bring back the snapshot's original stale or build-only icon records; actual reconciliation problems are reported as `RESTORE INCOMPLETE`. Multiple Full Controllers contributing to one prefix folder are normal.

**Duplication must preserve the displayed content.** A plain native menu graph can be copied, including shared submenus and cycles. A folder combining external sources or moved children is refused when copying its source asset would omit or resurrect content. VRCFury toggles can be duplicated at their current managed path.

---

## Developer settings

`Editor/developer.json` holds build-time toggles:

```json
{
  "DebugJSON": true,
  "VersionCode": "2.0.1"
}
```

- **`DebugJSON`** — set to `false` (or remove the key) before shipping a release build to disable debug capture, verification scans and warnings, and hide the "Debug JSON" toolbar button. Missing the file, missing the key, or a malformed value all default to hidden — a build shipped without this file shows no developer tooling.
- **`VersionCode`** — overrides the version shown in the toolbar, the About popup, and every exported JSON file (snapshots, structure exports, debug logs). Falls back to the version compiled into the plugin if missing or empty.

Edits take effect immediately — no need to restart Unity or reopen the window.

## Support

NeoEmberArts — [Discord](https://discord.com/users/616724478419795977) · [Telegram](https://t.me/NeoEmberArts) · authoranimation@gmail.com

Same-name folder renames and moves offer **Keep Separate**, **Cancel**, or **Combine**. Keep Separate preserves two folders with the same displayed name. You can move individual same-name native or Full Controller controls, including a partial multi-selection, without renaming their displayed labels.
