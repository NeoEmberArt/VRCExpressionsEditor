// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — dissolve folder
// ═════════════════════════════════════════════════════════════════════════════
//
// Moves everything inside a folder up one level, exactly as it is (no renames,
// no merges of its own), then makes the emptied folder go away. How the folder
// goes away depends on what builds it:
//
//   virtual menu ............ unregistered
//   made by move records /
//   only a virtual menu's
//   parent .................. nothing to do — retargeting the records and
//                             re-registering the virtual menus already removed
//                             everything that built it
//   made by Fury toggle
//   names ................... nothing to do — the toggles were renamed out
//   VRC menu sub-menu, Full
//   Controller sub-menu or
//   prefix .................. a VRCFury "Move Menu Item" record with a blank
//                             To Path, which deletes it at build time. That
//                             record runs after the ones that moved the items
//                             out, so nothing inside is lost.
//
// One snapshot first, one Undo step for the whole thing.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    bool CanDissolve(ExpressionNode n) => n != null && n != root && n.IsFolderLike;

    bool IsNativeDissolveChild(ExpressionNode node, VRCExpressionsMenu owner)
    {
        if (node == null || owner == null || node.sourceMenu != owner || node.sourceControl == null ||
            node.isFromFullController || node.sourceObject != null || node.isVirtualFolder ||
            node.isVirtualIntermediate || node.isFCPrefixFolder || node.isMoveGeneratedFolder ||
            node.hasManagedMove || node.mergedSources.Count > 0) return false;
        return owner.controls.Contains(node.sourceControl);
    }

    bool IsNativeOnlyDissolveCandidate(ExpressionNode folder, ExpressionNode parent)
    {
        if (HasVrcfMoveSupport() || !IsNativeFolderNode(folder)) return false;
        var parentMenu = NativeMenuForFolder(parent);
        var childMenu = folder.sourceControl?.subMenu;
        if (!parentMenu || !childMenu || folder.sourceMenu != parentMenu ||
            !parentMenu.controls.Contains(folder.sourceControl) || parentMenu == childMenu) return false;

        // Every represented child must be a direct control in this submenu.
        // Null serialized slots are preserved by the atomic list move, but an
        // unrepresented non-null control is not safe to move blindly.
        if (folder.children.Any(child => !IsNativeDissolveChild(child, childMenu))) return false;
        var represented = new HashSet<VRCExpressionsMenu.Control>(
            folder.children.Select(child => child.sourceControl));
        return childMenu.controls.Where(control => control != null).All(represented.Contains);
    }

    void DissolveInteractive(ExpressionNode f)
    {
        f = ResolveLiveNode(f);
        if (!CanDissolve(f)) return;

        var    parent     = f.parent ?? root;
        string parentName = parent == root ? "the top level" : $"\"{DisplayOf(parent)}\"";
        string name       = DisplayOf(f);
        var    kids       = new List<ExpressionNode>(f.children);

        // Items VRCFury reaches by name can't be moved one at a time when two of
        // them share a name — a move record would take both. Say so up front
        // instead of failing halfway through.
        bool nativeOnly = IsNativeOnlyDissolveCandidate(f, parent);
        var childMenuForPreview = nativeOnly ? f.sourceControl?.subMenu : null;
        var clash = kids.Where(k => k.sameNameCount > 1 && !IsDirectToggle(k) &&
                                    !(nativeOnly && IsNativeDissolveChild(k, childMenuForPreview)))
                        .Select(DisplayOf).Distinct().ToList();
        if (clash.Count > 0)
        {
            EditorUtility.DisplayDialog("Can't dissolve",
                $"Inside \"{name}\", more than one item is called {string.Join(", ", clash.Select(c => $"\"{c}\""))}. " +
                "VRCFury moves this kind of item by name, so they can't be moved up one at a time. Rename one first.",
                "OK");
            return;
        }

        // short preview
        var others = parent.children.Where(c => c != f).ToList();
        int repeats = kids.Count(k => !k.IsFolderLike &&
            others.Any(o => EEMPath.MatchKey(o.rawName) == EEMPath.MatchKey(k.rawName)));
        int combines = kids.Count(k => k.IsFolderLike &&
            others.Any(o => o.IsFolderLike && EEMPath.MatchKey(o.rawName) == EEMPath.MatchKey(k.rawName)));

        string msg = kids.Count == 0
            ? $"\"{name}\" is empty and will be removed."
            : $"{ItemsText(kids.Count)} move up to {parentName}, then \"{name}\" is removed.";
        if (repeats > 0)
            msg += $"\n\n{repeats} name(s) will appear twice in {parentName}.";
        if (combines > 0)
            msg += $"\n\n{combines} folder(s) share a name with a folder already there. " +
                   "VRCFury always combines same-name folders, so their contents will end up together.";
        int after = others.Count + kids.Count - combines;
        if (nativeOnly && childMenuForPreview != null)
        {
            var parentMenuForPreview = NativeMenuForFolder(parent);
            if (parentMenuForPreview != null)
                after = parentMenuForPreview.controls.Count - 1 + childMenuForPreview.controls.Count;
        }
        bool overCapacity = nativeOnly
            ? after > MENU_CAPACITY
            : after > MENU_CAPACITY && others.Count <= MENU_CAPACITY;
        if (overCapacity)
            msg += $"\n\n{parentName} will hold {after} items, more than the {MENU_CAPACITY} VRChat shows per page.";
        msg += "\n\nA snapshot is taken first, and Ctrl+Z undoes it.";

        if (!EditorUtility.DisplayDialog($"Dissolve \"{name}\"?", msg, "Dissolve", "Cancel")) return;

        SnapshotExpansionStates(root);

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName($"Dissolve {name}");
        int grp = Undo.GetCurrentGroup();

        bool dissolved = DissolveFolder(f);

        Undo.CollapseUndoOperations(grp);
        if (!dissolved) { FlushRescan(); return; }
        if (selected == f) selected = null;
        selectedNodes.Remove(f);
        FlushRescan();
    }

    bool DissolveFolder(ExpressionNode folder)
    {
        folder = ResolveLiveNode(folder);
        if (!CanDissolve(folder)) return false;
        var parent = folder.parent ?? root;
        string parentPath = parent == root ? "" : PathOf(parent);
        string folderPath = PathOf(folder);
        string name = DisplayOf(folder);
        var children = folder.children.ToList();
        return LogOp("Dissolve", folderPath, folder.type.ToString(), "", parentPath, () =>
        {
            // Importing the safety snapshot can publish projectChanged. Capture
            // it after LogOp accepts the current tree, before changing any items,
            // so our own snapshot cannot make this operation reject itself.
            try
            {
                string snapshot = WriteSnapshot($"Before dissolving {name}", false, true);
                if (string.IsNullOrEmpty(snapshot))
                    throw new System.InvalidOperationException("The safety snapshot did not produce a file.");
            }
            catch (System.Exception ex)
            {
                throw new System.InvalidOperationException(
                    "Dissolve was cancelled because the safety snapshot could not be created: " + ex.Message, ex);
            }
            if (IsNativeOnlyDissolveCandidate(folder, parent))
            {
                DissolveNativeFolderAtomic(folder, folderPath);
                QueueRescan();
                return;
            }
            foreach (var child in children)
                PerformMove(child, parentPath, parent == root ? null : parent, confirmFolderMerge: false);
            RemoveDissolvedFolder(folder, folderPath);
            QueueRescan();
        });
    }

    void DissolveNativeFolderAtomic(ExpressionNode folder, string folderPath)
    {
        if (folder == null) throw new System.InvalidOperationException("The native folder no longer exists. Rescan and try again.");

        // Clone/rebind the entire native graph before taking either list out of
        // the source menu. This retains the existing shared-menu confirmation
        // and makes the source and destination edits part of one Undo group.
        var writableFolderMenu = EnsureWritableNativeMenu(folder);
        if (!writableFolderMenu)
            throw new System.InvalidOperationException("The native folder menu could not be made writable; dissolve was cancelled.");

        folder = ResolveLiveNode(folder);
        var parent = folder?.parent ?? root;
        if (!IsNativeOnlyDissolveCandidate(folder, parent))
            throw new System.InvalidOperationException("The native dissolve target changed during clone-on-write; rescan and try again.");

        var parentMenu = NativeMenuForFolder(parent);
        var childMenu = folder.sourceControl?.subMenu;
        if (!parentMenu || !childMenu || parentMenu == childMenu || MenuGraphContains(childMenu, parentMenu))
            throw new System.InvalidOperationException(
                "Native dissolve was refused because the source and destination menus would create a shared-menu cycle.");

        var descriptor = avatar ? avatar.GetComponent<VRCAvatarDescriptor>() : null;
        if (NativeMenuSharedWithinOwner(childMenu, descriptor) &&
            !EditorUtility.DisplayDialog(
                "Shared child submenu",
                "This folder's child submenu is shared by multiple native menu branches. " +
                "Dissolving it will move its controls out for every branch that shares it. Continue only if that global change is intended.",
                "Dissolve all branches", "Cancel"))
            throw new System.OperationCanceledException(
                "Native dissolve canceled because the child submenu is shared by multiple branches.");

        int folderIndex = parentMenu.controls.IndexOf(folder.sourceControl);
        if (folderIndex < 0 || parentMenu.controls.LastIndexOf(folder.sourceControl) != folderIndex)
            throw new System.InvalidOperationException("The native folder link changed before it could be dissolved.");

        var childControls = childMenu.controls.ToList();
        if (childControls.Any(control => control != null && control.subMenu == childMenu))
            throw new System.InvalidOperationException("Native dissolve was refused because the child submenu contains a self-reference.");

        int finalCount = parentMenu.controls.Count - 1 + childControls.Count;
        if (finalCount > MENU_CAPACITY)
            throw new System.InvalidOperationException(
                $"Cannot dissolve the native folder: the destination would contain {finalCount} controls, above the {MENU_CAPACITY}-control VRChat limit.");

        Undo.RecordObject(parentMenu, "Dissolve native folder");
        Undo.RecordObject(childMenu, "Dissolve native folder");
        parentMenu.controls.RemoveAt(folderIndex);
        childMenu.controls.Clear();
        parentMenu.controls.AddRange(childControls);
        EditorUtility.SetDirty(parentMenu);
        EditorUtility.SetDirty(childMenu);

        if (folder.sameNameCount <= 1 && RemoveIconOverridesTouching(folderPath) > 0)
            NoteConsequence("removed the dissolved folder's icon record");
        NoteConsequence($"moved {childControls.Count} native control(s) and removed the native submenu link atomically");
    }

    void RemoveDissolvedFolder(ExpressionNode f, string folderPath)
    {
        // The folder's own icon record would otherwise be left pointing at
        // nothing. (Its items' icon records already moved with them. Skipped if
        // another folder here has the same name — the record is shared.)
        if (f.sameNameCount <= 1 && RemoveIconOverridesTouching(folderPath) > 0)
            NoteConsequence("removed the dissolved folder's icon record");

        if (f.isVirtualFolder)
        {
            RemoveVirtualFolder(VirtualPathOf(f));
            NoteConsequence("unregistered the virtual menu");
            if (!HasRealFolderSource(f)) return;
        }

        bool builtOnlyByOthers = f.isMoveGeneratedFolder || f.isVirtualIntermediate;
        bool builtByToggleNames = f.sourceControl == null && !f.isFCPrefixFolder && !f.isFromFullController;
        if (!HasRealFolderSource(f) && (builtOnlyByOthers || builtByToggleNames))
        {
            NoteConsequence("folder disappears on its own once emptied");
            return;
        }

        // A real menu entry (VRC sub-menu, Full Controller sub-menu or prefix):
        // delete it at build time, after the records that moved its items out.
        CommitMove(folderPath, "", f);
        NoteConsequence("added a delete-at-build record for the emptied folder");
    }
}
