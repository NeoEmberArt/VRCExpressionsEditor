// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — rename (unified across source kinds)
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{

    void DoRename(ExpressionNode node, string newRawName)
    {
        node = ResolveLiveNode(node);
        if (node == null || node == root || string.IsNullOrEmpty(newRawName)) return;

        string oldPath  = PathOf(node);
        string oldLeaf  = node.rawName;
        string newLeaf  = EEMPath.Clean(newRawName);
        if (string.Equals(oldLeaf, newLeaf, System.StringComparison.Ordinal)) return;

        var peers = MatchVrcControls(node.parent.children.Where(n => n != node), newLeaf);
        bool combineFolders = false;
        bool separateFolder = false;
        if (peers.Count > 0)
        {
            if (node.IsFolderLike && peers.Any(n => n.IsFolderLike))
            {
                int choice = EditorUtility.DisplayDialogComplex("Name already used",
                    $"A folder named \"{EEMPath.Strip(newLeaf)}\" already exists. Combine their contents or keep two separate folders?",
                    "Keep Separate", "Cancel", "Combine");
                if (choice == 1) return;
                combineFolders = choice == 2;
                separateFolder = choice == 0;
            }
            else if (!EditorUtility.DisplayDialog("Name already used",
                $"\"{EEMPath.Strip(newLeaf)}\" already exists. Keep both items with this name?", "Keep Separate", "Cancel")) return;
        }

        string newPath  = EEMPath.ReplaceLastSegment(oldPath, newLeaf);
        string logType  = node.type.ToString();

        // v2.1.0 logged the *leaf names* here while the verifier resolved them as
        // full paths, so every successful rename reported a false mismatch.
        var ctx = new Dictionary<string, object>
        {
            ["oldLeaf"] = EEMPath.Strip(oldLeaf),
            ["newLeaf"] = EEMPath.Strip(newLeaf),
            ["previousMatchCount"] = FindAllNodesAtPath(root, oldPath).Count,
        };

        SnapshotExpansionStates(root);
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName($"Rename \"{EEMPath.Strip(oldLeaf)}\"");
        int grp = Undo.GetCurrentGroup();

        LogOp("Rename", oldPath, logType, oldPath, newPath, () =>
        {
            node = ResolveLiveNode(node);
            if (node == null || node == root) throw new System.InvalidOperationException("The item no longer exists. Rescan and try again.");
            GuardRenameCollision(node, newLeaf);

            if (!node.IsFolderLike && node.sourceControl != null && node.hasManagedMove)
            {
                MoveIndividualControl(node, PathOf(node.parent, true), newLeaf);
                QueueRescan();
                return;
            }

            if (node.IsFolderLike && (string.IsNullOrEmpty(node.rawName) || separateFolder || combineFolders ||
                FindAllNodesAtPath(root, PathOf(node)).Count > 1))
            {
                RenameFolderByMove(node, newLeaf, separateFolder);
                QueueRescan();
                return;
            }

            bool movedFolder = node.IsFolderLike && (node.hasManagedMove || node.mergedSources.Count > 0);
            for (var ancestor = node.parent; !movedFolder && node.IsFolderLike && ancestor != null; ancestor = ancestor.parent)
                movedFolder = ancestor.hasManagedMove;
            if (movedFolder)
            {
                CommitMove(oldPath, newPath, node);
                RewriteIconOverridesForPathChange(oldPath, newPath);
                QueueRescan();
                return;
            }


            if (EEMPath.SegmentHasSeparator(newLeaf))
                Debug.LogWarning($"[EASY] \"{newLeaf}\" contains a '/'. VRCFury will read that as a folder " +
                                 "separator unless escaped; this tool writes the escaped path.");

            // ── the node's own name, wherever it is stored ────────────────────
            if (node.isVirtualFolder)
            {
                // registered path, not tree path — they differ when a parent
                // folder was moved by a record (renaming used to silently miss)
                string reg = VirtualPathOf(node);
                RenameVirtualFolderPath(reg, EEMPath.ReplaceLastSegment(reg, newLeaf));
            }

            if (node.sourceControl != null && node.sourceMenu != null && !node.isFromFullController)
            {
                EnsureWritableNativeMenu(node);
                Undo.RecordObject(node.sourceMenu, "Rename control");
                node.sourceControl.name = newLeaf;
                EditorUtility.SetDirty(node.sourceMenu);
            }
            else if (node.isFromFullController && node.sourceControl != null)
            {
                var writable = EnsureWritableFCMenu(node, out int idx);
                if (writable != null && idx >= 0 && idx < writable.controls.Count)
                {
                    Undo.RecordObject(writable, "Rename control");
                    writable.controls[idx].name = newLeaf;
                    EditorUtility.SetDirty(writable);
                }
            }
            else if (node.isFCPrefixFolder && node.fcPrefixGenerators.Count > 0)
            {
                // This is the third kind of "same-named duplicate" node besides
                // native controls and Fury toggles: a folder synthesised purely
                // from one or more Full Controller entries' "prefix" field, with
                // no sourceControl/sourceObject of its own. None of the branches
                // above touch it, and previously nothing did — the in-memory
                // node's rawName changed, the row visibly updated, and then the
                // next scan silently reverted it because the actual prefix
                // string on the component was never written. Renamed here
                // instead, by exact (owner, prefix) pairs, not a broad sweep —
                // see RenameFCPrefixFolder for why that distinction matters.
                RenameFCPrefixFolder(node, oldPath, newPath);
            }

            if (node.sourceObject != null)
            {
                var so = FreshSO(node.sourceObject);
                var nameProp = so?.FindProperty("content.name");
                if (nameProp?.propertyType == SerializedPropertyType.String && so.targetObject != null)
                {
                    Undo.RecordObject(so.targetObject, "Rename toggle");
                    string cur = EEMPath.FromVrcf(nameProp.stringValue);
                    nameProp.stringValue = EEMPath.ToVrcf(EEMPath.ReplaceLastSegment(cur, newLeaf));
                    so.ApplyModifiedProperties();
                    node.sourceObject = so;
                    EditorUtility.SetDirty(so.targetObject);
                }
            }

            // ── everything that referenced the old path ───────────────────────
            // move records, icon overrides, Full Controller prefixes, Fury toggle
            // paths beneath this node, and virtual folder entries — matched by
            // the OLD path STRING, which can't tell this node apart from a
            // same-named sibling. If the path was ambiguous, sweeping by string
            // risks rewriting a record that actually belongs to the OTHER
            // duplicate. Renaming is the only way out of that ambiguity, so the
            // rename itself always proceeds; only the sweep is skipped, with a
            // clear pointer to check things afterward.
            if (FindAllNodesAtPath(root, oldPath).Count > 1)
            {
                Debug.LogWarning($"[EASY] '{EEMPath.ToVrcf(oldPath)}' was ambiguous before this rename, so move/icon/" +
                                 "prefix records referencing it were left untouched rather than risk updating the " +
                                 "wrong duplicate's records. Check Records ⚠ in the toolbar after the rescan.");
                NoteConsequence("skipped path-reference sweep — old path was ambiguous");
            }
            else
            {
                // If this node is CURRENTLY placed here by an active
                // MoveMenuItem record (node.hasManagedMove), the record's
                // `from` field is expressed in NATIVE path space — whatever
                // this item was called BEFORE the move — not resolved path
                // space. The sweep below only rewrites `from`/`to` values that
                // fall within the RESOLVED path being renamed, so it correctly
                // updates the record's `to` (resolved space) but has no way to
                // relate that to the record's `from` (native space) — they're
                // different strings describing different things. Left alone,
                // the native rename below (node.sourceControl.name = newLeaf,
                // or the equivalent for other source kinds) changes the item's
                // real name while the record keeps looking for its OLD name —
                // VRCFury finds nothing by that name at build time, the move
                // silently stops applying, and the item ends up back at its
                // original native position. Fixed by updating the governing
                // record's `from` leaf to match, BEFORE the general sweep runs.
                if (node.hasManagedMove)
                {
                    bool updated = UpdateGoverningMoveRecordNativeLeaf(oldPath, newLeaf);
                    if (updated) NoteConsequence("updated MoveMenuItem record's native fromPath to match the rename");
                }
                RewriteAllPathReferences(oldPath, newPath, node);
            }

            node.rawName = newLeaf;
            QueueRescan();
        }, ctx);

        Undo.CollapseUndoOperations(grp);
        FlushRescan();
        Repaint();
    }

    /// Rewrites the "prefix" field on every Full Controller entry this specific
    /// folder node owns (its fcPrefixGenerators), replacing only the segment
    /// matching this node's own path — not a blind scan of every component's
    /// every entry. A folder can be one segment of a MULTI-level prefix
    /// ("Category/ShapeModify"), so this needs to rewrite the trailing segment
    /// within each owned prefix string, same as any other path-reference
    /// rewrite, and needs to touch only the entries actually recorded against
    /// THIS node — a global sweep here would also rename an unrelated
    /// duplicate's entries just because they happen to share the same prefix
    /// string, defeating the entire point of renaming one to disambiguate it.
    void RenameFCPrefixFolder(ExpressionNode node, string oldPath, string newPath)
    {
        if (node.fcPrefixGenerators.Count == 0)
        {
            Debug.LogWarning($"[EASY] \"{DisplayOf(node)}\" is a Full Controller prefix folder with no tracked " +
                             "generators — nothing to rename on the component side. Rescan and try again.");
            return;
        }

        var byOwner = node.fcPrefixGenerators
            .Where(g => g.owner?.targetObject != null)
            .GroupBy(g => g.owner.targetObject);

        int totalRewritten = 0;
        var newGenerators = new List<FCPrefixGenerator>();

        foreach (var group in byOwner)
        {
            var comp = group.Key as Component;
            if (!comp) continue;


            var so = new SerializedObject(comp); so.Update();
            var menusProp = so.FindProperty("content.menus");
            if (menusProp == null || !menusProp.isArray) continue;

            var wanted = new HashSet<string>(group.Select(g => g.prefix), System.StringComparer.Ordinal);
            bool changedHere = false;

            for (int i = 0; i < menusProp.arraySize; i++)
            {
                var prefixProp = menusProp.GetArrayElementAtIndex(i).FindPropertyRelative("prefix");
                if (prefixProp?.propertyType != SerializedPropertyType.String) continue;
                string cur = prefixProp.stringValue ?? "";
                if (!wanted.Contains(cur)) continue;   // only entries THIS node owns

                string curInternal = EEMPath.FromVrcf(cur);
                string rewritten = EEMPath.RewriteSeg(curInternal, oldPath, newPath);
                if (EEMPath.PathEq(rewritten, curInternal)) continue;

                if (!changedHere) { Undo.RecordObject(comp, $"Rename Full Controller prefix \"{DisplayOf(node)}\""); changedHere = true; }
                prefixProp.stringValue = EEMPath.ToVrcf(rewritten);
                newGenerators.Add(new FCPrefixGenerator(so, EEMPath.ToVrcf(rewritten)));
                totalRewritten++;
            }
            if (changedHere) { so.ApplyModifiedProperties(); EditorUtility.SetDirty(comp); }
        }

        if (totalRewritten == 0)
        {
            Debug.LogWarning($"[EASY] Renaming \"{DisplayOf(node)}\" didn't find a matching prefix entry to " +
                             "rewrite on any of its tracked components. The row may show the new name until the " +
                             "next scan, then revert — rescan to confirm.");
            NoteConsequence("WARNING: no matching FC prefix entry found — rename may revert on next scan");
            return;
        }

        // Keep this node's own bookkeeping in sync with what we just wrote, so
        // a second edit in the same session (before the queued rescan runs)
        // still matches the entries it actually owns.
        node.fcPrefixGenerators = newGenerators;
        Debug.Log($"[EASY] Renamed {totalRewritten} Full Controller prefix entr{(totalRewritten == 1 ? "y" : "ies")} " +
                 $"for \"{EEMPath.ToVrcf(newPath)}\".");
        NoteConsequence($"rewrote {totalRewritten} FC prefix entr{(totalRewritten == 1 ? "y" : "ies")}");
    }

    void GuardRenameCollision(ExpressionNode node, string newLeaf)
    {
        // Same-name siblings are legal. Source-based renames remain specific;
        // path sweeps are skipped when their original path is ambiguous.
    }

    void RenameFolderByMove(ExpressionNode node, string newLeaf, bool keepSeparate)
    {
        string parentPath = PathOf(node.parent, true);
        if (string.IsNullOrEmpty(node.rawName))
        {
            MoveUnnamedFolder(node, parentPath, newLeaf, keepSeparate);
            return;
        }
        if (FindAllNodesAtPath(root, PathOf(node)).Count > 1)
        {
            MoveUnnamedFolder(node, parentPath, newLeaf, keepSeparate);
            return;
        }
        string from = PathOf(node);
        string leaf = newLeaf;
        int ordinal = MatchVrcControls(node.parent.children.Where(n => n != node), newLeaf, true).Count;
        if (keepSeparate && ordinal > 0) leaf += ".dup." + ordinal;
        string to = EEMPath.Combine(parentPath, leaf);
        CommitMove(from, to, node);
        RewriteIconOverridesForPathChange(from, to);
    }

    // ── inline rename plumbing ────────────────────────────────────────────────

    void StartInlineRename(ExpressionNode n)
    {
        if (n == null || n == root) return;
        inlineEditingNode  = n;
        inlineRenameBuffer = n.rawName ?? "";
        GUI.FocusControl(null);
        Repaint();
    }

    void HandleGlobalInlineRenameKeys()
    {
        if (inlineEditingNode == null) return;
        if (Event.current == null || Event.current.type != EventType.KeyDown) return;
        if (GUI.GetNameOfFocusedControl() != "InlineRename") return;
        if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
            { FinishInlineRename(); Event.current.Use(); }
        else if (Event.current.keyCode == KeyCode.Escape)
            { CancelInlineRename(); Event.current.Use(); }
    }

    void FinishInlineRename()
    {
        if (inlineEditingNode == null) return;
        var node = inlineEditingNode;
        string newName = (inlineRenameBuffer ?? "").Trim();
        inlineEditingNode  = null;
        inlineRenameBuffer = "";
        if (!string.IsNullOrEmpty(newName) && !string.Equals(newName, node.rawName, System.StringComparison.Ordinal))
            Defer(() => DoRename(node, newName));
        GUI.FocusControl(null);
        Repaint();
    }

    void CancelInlineRename()
        { inlineEditingNode = null; inlineRenameBuffer = ""; GUI.FocusControl(null); Repaint(); }

}
