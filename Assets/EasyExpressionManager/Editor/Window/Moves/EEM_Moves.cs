// ═══════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — VRCFury MoveMenuItem records
//
// All paths inside this file are INTERNAL form. Conversion to/from what VRCFury
// stores happens only in ReadMoveRecords / FlushRecords / WriteRecordFields.
// ═══════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    // ═══════════════════════════════════════════════════════════════════════════
    // COMMIT MOVE
    //
    // THE v2.1.0 BUG: any record whose toPath sat under the folder being moved was
    // rewritten and the whole operation was then marked "absorbed", so no real
    // MoveMenuItem got written. Moving STUFF retargeted `Toggles → STUFF/Clothing`
    // and silently left the two Fury toggles that lived directly under STUFF
    // behind at their old path.
    //
    // Rewriting downstream records is still correct and still happens. What
    // changed is that "do I also need my own record" is now answered by asking
    // the tree what actually materialises the source path, instead of being a
    // side effect of that rewrite.
    // ═══════════════════════════════════════════════════════════════════════════

    void CommitMove(string fromInternal, string toInternal, ExpressionNode movingNode = null)
    {
        if (string.IsNullOrEmpty(fromInternal)) return;
        fromInternal = EEMPath.Norm(fromInternal);
        toInternal   = EEMPath.Norm(toInternal);

        GuardAddressable(fromInternal, "move");
        if (!string.IsNullOrEmpty(toInternal)) GuardAddressable(toInternal, "move");

        // The MoveMenuItem record this writes is a path STRING — it gets
        // re-resolved by NodeByPath on every future scan, not just this one.
        // If the source path is ambiguous right now, the record is fragile
        // from the moment it's written: a later scan can silently re-resolve
        // it to a different duplicate than the one actually moved here.
        GuardUnambiguousPath(fromInternal, "move");
        if (!string.IsNullOrEmpty(toInternal)) GuardDestinationChainUnambiguous(toInternal, "move");

        if (EEMPath.IsUnder(toInternal, fromInternal))
            throw new System.InvalidOperationException(
                $"Move blocked: '{EEMPath.ToVrcf(toInternal)}' is inside '{EEMPath.ToVrcf(fromInternal)}' — that would create a loop.");

        var fType    = FindFeatureType("MoveMenuItem");
        var vrcfType = ConcreteVrcfType();
        if (fType == null || vrcfType == null)
        {
            Debug.LogError("[EASY] VRCFury MoveMenuItem type not found — is VRCFury installed?");
            throw new System.InvalidOperationException("VRCFury MoveMenuItem type not found.");
        }

        var go = GetOrCreateManager();
        if (!go) throw new System.InvalidOperationException("Could not create the ExpressionsManager object.");

        var records = ReadMoveRecords();
        var originalRecords = new List<MoveRecord>(records);
        var sourceTree = BuildMoveSourceTree();
        var expectedTree = CopyMoveTree(sourceTree);
        foreach (var record in OrderMovesByDependency(originalRecords, sourceTree))
            ApplyMove(expectedTree, record.from, record.to, record.comp);
        if (!ApplyMove(expectedTree, fromInternal, toInternal, null))
            throw new System.InvalidOperationException("The moving item no longer resolves in the current menu. Rescan and try again.");
        // A stored destination can be in an ancestor's pre-move coordinates.
        // Resolve it by the controls that this record actually moved; string
        // reversal cannot distinguish contributors to a merged folder.
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            records[i] = new MoveRecord { comp = r.comp, from = r.from, to = ResolvedMoveDestination(r) };
        }

        // The requested destination is in the current displayed tree. Following
        // it through old source paths would redirect a move back to its origin.
        if (EEMPath.PathEq(fromInternal, toInternal)) return;

        // Does anything under the source path exist independently of the records
        // we are about to rewrite? If so we need a record of our own.
        bool needsOwnRecord = NeedsOwnMoveRecord(movingNode ?? NodeByPath(root, fromInternal));
        var sourceNode = movingNode ?? NodeByPath(root, fromInternal);
        string UncoveredSource(ExpressionNode n)
        {
            if (n == null) return null;
            if (!n.hasManagedMove && n.nativePath != null &&
                (n.sourceControl != null || n.sourceObject != null || n.hasNativeFolderSource)) return n.nativePath;
            foreach (var merged in n.mergedSources)
            {
                string candidate = UncoveredSource(merged);
                if (candidate != null) return candidate;
            }
            return null;
        }
        string ownSource = UncoveredSource(sourceNode) ?? NativePathOf(sourceNode);
        if (string.IsNullOrEmpty(ownSource)) ownSource = fromInternal;

        int downstreamRewrites = 0;
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (EEMPath.PathEq(r.to, fromInternal))
                { records[i] = new MoveRecord { comp = r.comp, from = r.from, to = toInternal }; downstreamRewrites++; }
            // (on a delete, things moved INTO the folder stay pointed at it —
            // they arrive first and are removed along with it)
            else if (!string.IsNullOrEmpty(toInternal) && EEMPath.IsUnder(r.to, fromInternal))
                { records[i] = new MoveRecord { comp = r.comp, from = r.from, to = EEMPath.RewriteSeg(r.to, fromInternal, toInternal) }; downstreamRewrites++; }
        }
        // Existing source paths retain their original coordinates. A source
        // under a merged destination can belong to a different native folder.
        if (!needsOwnRecord && !string.IsNullOrEmpty(toInternal))
        {
            // No record of our own means this folder exists only because other
            // things generate it: records pointing into it (handled above) and
            // virtual menus registered inside it. The virtual menus were never
            // carried along, so they kept re-creating the old folder in place.
            int carried = CarryVirtualFoldersWithMove(movingNode ?? NodeByPath(root, fromInternal), fromInternal, toInternal);
            if (carried > 0) NoteConsequence($"carried {carried} virtual menu(s) along with this move");
        }
        if (downstreamRewrites > 0)
            NoteConsequence($"carried {downstreamRewrites} nested record(s) along with this move");

        if (needsOwnRecord)
        {
            var existing = records.FindIndex(r => EEMPath.PathEq(r.from, ownSource));
            if (existing >= 0)
            {
                records[existing] = new MoveRecord { comp = records[existing].comp, from = ownSource, to = toInternal };
                NoteConsequence("superseded existing MoveMenuItem record for this item");
            }
            else
            {
                var comp = AddFeatureComponent(go, fType);
                if (!comp) throw new System.InvalidOperationException("Could not add a MoveMenuItem component.");
                records.Add(new MoveRecord { comp = comp, from = ownSource, to = toInternal });
                NoteConsequence("created new MoveMenuItem record");
            }
        }

        // Carrying a virtual registration changes the pre-move source tree.
        sourceTree = BuildMoveSourceTree();
        bool canonicalPlan;
        try
        {
            FlattenInPlace(records, true);
            canonicalPlan = MovePlanMatches(records, sourceTree, expectedTree);
        }
        catch (System.InvalidOperationException) { canonicalPlan = false; }
        if (canonicalPlan)
        {
            FlushRecords(records);
        }
        else
        {
            // Mixed contributors and a return to a vacated parent sometimes
            // cannot be represented by retargeting native-source records.
            // Preserve the working history and append the actual visible move.
            var created = records.Where(r => !originalRecords.Any(old => old.comp == r.comp)).Select(r => r.comp).Distinct().ToList();
            var comp = created.FirstOrDefault() ?? AddFeatureComponent(go, fType);
            foreach (var extra in created.Skip(1)) if (extra) Undo.DestroyObjectImmediate(extra);
            var sequential = new List<MoveRecord>(originalRecords) {
                new MoveRecord { comp = comp, from = fromInternal, to = toInternal }
            };
            if (!MovePlanMatches(sequential, sourceTree, expectedTree))
            {
                // Returning to a native folder name that an earlier record
                // vacates needs two steps; otherwise that earlier move carries
                // the newly returned folder away again. Stage inside its final
                // destination so dependency ordering waits for it to arrive.
                bool staged = false;
                foreach (var vacating in originalRecords.Where(r => !string.IsNullOrEmpty(r.to) && EEMPath.IsAtOrUnder(toInternal, r.from)))
                {
                    string anchor = ResolvedMoveDestination(vacating);
                    if (EEMPath.IsAtOrUnder(anchor, fromInternal)) continue;
                    string temporary = EEMPath.Combine(anchor, "__EEM_move_" + System.Guid.NewGuid().ToString("N"));
                    var finish = AddFeatureComponent(go, fType);
                    var candidate = new List<MoveRecord>(originalRecords) {
                        new MoveRecord { comp = comp, from = fromInternal, to = temporary },
                        new MoveRecord { comp = finish, from = temporary, to = toInternal },
                    };
                    if (MovePlanMatches(candidate, sourceTree, expectedTree)) { sequential = candidate; staged = true; break; }
                    if (finish) Undo.DestroyObjectImmediate(finish);
                }
                if (!staged)
                    throw new System.InvalidOperationException($"The move from '{EEMPath.ToVrcf(fromInternal)}' to '{EEMPath.ToVrcf(toInternal)}' would not preserve this merged folder. No changes were applied. " + movePlanMismatch);
            }
            FlushRecords(sequential);
            NoteConsequence("preserved move history to relocate merged contributors together");
        }
        if (go) EditorUtility.SetDirty(go);
    }

    /// Re-registers every virtual menu that sits inside the moved folder at its
    /// new location. Subtrees that are positioned by their own MoveMenuItem
    /// record are skipped — that record is what carries them.
    int CarryVirtualFoldersWithMove(ExpressionNode movingNode, string fromInternal, string toInternal)
    {
        if (movingNode == null) return 0;
        var moves = new List<KeyValuePair<ExpressionNode, string>>();
        void Walk(ExpressionNode n)
        {
            if (n.hasManagedMove) return;
            if (n.isVirtualFolder)
                moves.Add(new KeyValuePair<ExpressionNode, string>(n, EEMPath.RewriteSeg(PathOf(n), fromInternal, toInternal)));
            foreach (var c in n.children) Walk(c);
        }
        Walk(movingNode);
        // outermost first: renaming a parent registration also rewrites the
        // registrations nested under it, so the inner ones then find nothing
        // left to change
        foreach (var kv in moves) MoveVirtualFolderRegistration(kv.Key, kv.Value);
        return moves.Count;
    }

    /// True when the source path is materialised by something a record retarget
    /// would NOT carry along — a Fury toggle whose content.name literally starts
    /// with this path, a native VRC control, or a Full Controller prefix.
    bool NeedsOwnMoveRecord(ExpressionNode fromNode)
    {
        if (fromNode == null) return true;   // can't prove otherwise — write the record

        // A folder that exists on the avatar in its own right (built from Fury
        // toggle paths, a menu asset, …) stays put unless a record moves it —
        // even when everything that used to be inside has already been moved
        // out. Only folders that exist purely because records point into them
        // or virtual menus sit in them can travel by rewriting those instead.
        // (Field log: SPS whose items had all been moved out stayed behind.)
        if (!fromNode.hasManagedMove && fromNode.IsFolderLike &&
            (fromNode.hasNativeFolderSource || fromNode.sourceControl != null)) return true;

        bool needs = false;
        void Walk(ExpressionNode n, bool coveredByRecord)
        {
            bool covered = coveredByRecord || n.hasManagedMove;
            bool hasOwnSource = n.sourceObject != null || n.sourceControl != null || n.hasNativeFolderSource;
            if (hasOwnSource && !covered) needs = true;
            if (needs) return;
            foreach (var source in n.mergedSources) Walk(source, coveredByRecord);
            foreach (var c in n.children) Walk(c, covered);
        }
        Walk(fromNode, false);
        return needs;
    }

}
