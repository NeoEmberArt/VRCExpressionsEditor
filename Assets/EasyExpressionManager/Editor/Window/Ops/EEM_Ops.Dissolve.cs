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

public partial class EASYExpressionManager
{
    bool CanDissolve(ExpressionNode n) => n != null && n != root && n.IsFolderLike;

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
        var clash = kids.Where(k => k.sameNameCount > 1 && !IsDirectToggle(k))
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
        if (after > MENU_CAPACITY && others.Count <= MENU_CAPACITY)
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
            try { WriteSnapshot($"Before dissolving {name}", false, true); }
            catch (System.Exception ex) { Debug.LogWarning("[EASY] Couldn't take a snapshot before dissolving: " + ex.Message); }
            foreach (var child in children)
                PerformMove(child, parentPath, parent == root ? null : parent, confirmFolderMerge: false);
            RemoveDissolvedFolder(folder, folderPath);
            QueueRescan();
        });
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
