# Easy Expression Manager - Tutorial

There are multiple ways to perform most operations in Easy Expression Manager.

All operations are registered with Unity's Undo system and can be undone with **Ctrl+Z**.

---

# Rename

There are multiple ways to rename an item or folder:

1. Double-click a folder or item to change the name inline.
2. Click an item and change the name in the editing header.
3. Right-click the item and select **Rename**.

![Rename](https://public-files.gumroad.com/ly1fq0awiyvhc15pjtyondfk9hv8)

---

# Delete

There are multiple ways to delete items:

1. Right-click an item and select **Delete**.
2. Right-click an item, select **Select Multiple**, select what you want to delete, then select **Delete Selected**.

![Delete](https://public-files.gumroad.com/ugooixppvqjzv5vd2iusd27gwbm1)

---

# Move

There are multiple ways to move items and folders:

1. Drag and drop an item or entire folder into the folder you want.
2. Click an item or folder, then select **Move/Rename via Expressions Manager** in the editing header.
3. Right-click an item, select **Select Multiple**, select what you want to move, then select **Move Selected**.

![Move](https://public-files.gumroad.com/4xikl5tohui3gbey74wk41kw9qiy)

---

# Change Icon

There are multiple ways to change an item's icon:

1. Click an item or folder, then click its icon in the editing header.
2. Right-click an item, select **Select Multiple**, select what you want to edit, then select **Icon → Apply to (selection)**.

![Change Icon](https://public-files.gumroad.com/wd5mqbb7gde235mkn1y9w8vhjvf7)

---

# Virtual Menu

Virtual Menus are **non-destructive**.

They only create a temporary placeholder within Easy Expression Manager. Nothing is created on your avatar until you move something into the Virtual Menu.

### Creating a Virtual Menu

1. In the header, select **+ Virtual Menu**.
2. Enter a name.
3. Select **Create**.

![Virtual Menu](https://public-files.gumroad.com/1tup842wi8x2izp7ksxtixkub8hl)

---

# Duplicate

There are two ways to duplicate an item:

1. Right-click the item and select **Duplicate**.
2. Select an item, then select **Duplicate** in the editing header.

---

# Snapshot

**WIP**

Snapshots are a way of branching development into recoverable instances.

The goal is to allow you to back up your current state and restore it if something goes wrong.

> ⚠️ **BIG WIP**
>
> Snapshots are not recommended for main projects yet, but the feature does currently work.

---

# Structure

**WIP**

Structure is a way of copying your edited hierarchy into other similar avatars.

> ⚠️ **HUGE WIP**
>
> Structure is not recommended for main projects yet. It is not fully tested.

---

# Item Counter

The Item Counter warns you when VRChat will automatically create an additional subfolder or menu page.

VRChat allows a maximum of **8 items in a single menu page**.

If a folder contains more than 8 items, VRChat will automatically create additional pages when the avatar is built.

Easy Expression Manager will:

- Show in the header which menu has too many items.
- Show a warning on the right side of the folder that has the issue.
- Display the current item count compared to the 8-item limit.

For example:

`⚠ 10/8`

means the folder contains **10 items**, exceeding VRChat's **8-item** limit.

![Item Counter](https://public-files.gumroad.com/vcubcd4816wdb8tlstcykc2d3pg8)

