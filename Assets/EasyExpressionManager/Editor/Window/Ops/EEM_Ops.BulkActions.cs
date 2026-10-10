// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — bulk delete, bulk move, bulk icon
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

    /// Drops any node whose ancestor is also selected, then orders deepest-first
    /// so a parent is never removed out from under a child mid-loop.
    List<ExpressionNode> NormaliseSelection(IEnumerable<ExpressionNode> nodes)
    {
        var list = nodes.Where(n => n != null && n != root).Distinct().ToList();
        var top  = list.Where(n => !list.Any(other => other != n && IsAncestorOf(other, n))).ToList();
        return top.OrderByDescending(n => EEMPath.Depth(PathOf(n))).ToList();
    }

    void DeleteSelectedNodes()
    {
        if (selectedNodes.Count == 0) return;
        var liveTargets = selectedNodes.Select(ResolveLiveNode).ToList();
        if (liveTargets.Any(n => n == null))
        {
            EditorUtility.DisplayDialog("Can't delete", "A selected item changed or was removed. Rescan and select the complete block again.", "OK");
            return;
        }
        var targets = NormaliseSelection(liveTargets);
        if (targets.Count == 0) return;

        var ambiguous = new List<string>();
        foreach (var t in targets)
        {
            try
            {
                if (!DeletesByOwnObject(t)) GuardUnambiguousPath(PathOf(t, t.IsFolderLike), "delete");
                GuardNoOrphanShell(t);
            }
            catch (System.Exception ex) { ambiguous.Add(ex.Message); }
        }
        if (ambiguous.Count > 0)
        {
            EditorUtility.DisplayDialog("Can't delete",
                "One or more selected items can't be safely deleted:\n\n" +
                string.Join("\n\n", ambiguous), "OK");
            return;
        }

        foreach (var t in targets)
            if (!ConfirmDeleteRisk(t)) return;

        if (!EditorUtility.DisplayDialog(
            $"Delete {targets.Count} item(s)?",
            $"This will remove {targets.Count} selected item(s) and everything inside them.\n\nUndo with Ctrl+Z.",
            "Delete All", "Cancel")) return;

        SnapshotExpansionStates(root);
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName($"Delete {targets.Count} items");
        int undoGroup = Undo.GetCurrentGroup();
        string logPaths = string.Join(", ", targets.Select(n => PathOf(n)));
        var priorDangling = new List<string>(danglingRecordDescriptions);
        LogOp("BulkDelete", logPaths, "Multiple", logPaths, "", () =>
        {
            foreach (var n in targets) DeleteNode(n);
            QueueRescan();
        }, new Dictionary<string, object>
        {
            ["expectedDeleteCounts"] = targets.GroupBy(n => PathOf(n)).Select(group => (object)new Dictionary<string, object>
            {
                ["path"] = group.Key,
                ["maximumCount"] = System.Math.Max(0, FindAllNodesAtPath(root, group.Key).Count - group.Count()),
            }).ToList(),
        });
        Undo.CollapseUndoOperations(undoGroup);
        ExitSelectionMode();
        FlushRescan();
        OfferCleanupIfNewDanglingRecords(priorDangling);
    }


    void OpenBulkMoveDialog()
    {
        if (selectedNodes.Count == 0) return;
        var targets = NormaliseSelection(selectedNodes);
        if (targets.Count == 0) return;

        var folderPaths = new List<string> { "" };
        folderPaths.AddRange(CollectAllPaths(root, foldersOnly: true).Select(EEMPath.ToVrcf));

        PickFolderDialog.Open(
            "Every selected item is moved into this folder, keeping its own name.",
            targets.Select(DisplayPathOf).ToList(),
            folderPaths,
            "",
            destRaw => MoveSelectedTo(targets, EEMPath.FromVrcf(destRaw)));
    }

    void MoveSelectedTo(List<ExpressionNode> targets, string destFolderInternalPath)
    {
        var liveTargets = targets.Select(ResolveLiveNode).ToList();
        if (liveTargets.Any(n => n == null))
        {
            EditorUtility.DisplayDialog("Move Failed", "A selected item changed or was removed before this move. Rescan and select the complete block again.", "OK");
            return;
        }
        targets = NormaliseSelection(liveTargets);
        if (targets.Count == 0) return;
        var destNode = string.IsNullOrEmpty(destFolderInternalPath) ? root : NodeByFolderPath(root, destFolderInternalPath);
        if (!string.IsNullOrEmpty(destFolderInternalPath) && destNode == null)
        {
            EditorUtility.DisplayDialog("Move Failed",
                $"No folder resolves at '{EEMPath.ToVrcf(destFolderInternalPath)}'.", "OK");
            return;
        }
        if (targets.Any(n => n == destNode || IsAncestorOf(n, destNode)))
        {
            EditorUtility.DisplayDialog("Move Failed", "The destination is one of the selected folders or is inside one. Choose another folder.", "OK");
            return;
        }
        destFolderInternalPath = PathOf(destNode, true);

        SnapshotExpansionStates(root);
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName($"Move {targets.Count} items");
        int undoGroup = Undo.GetCurrentGroup();

        // shallowest first: moving a parent first keeps child paths meaningful
        var ordered = targets.OrderBy(n => EEMPath.Depth(PathOf(n))).ToList();
        string logFrom = string.Join(", ", ordered.Select(n => PathOf(n)));
        var expectedPaths = ordered.Select(n => EEMPath.Combine(destFolderInternalPath, n.rawName)).Cast<object>().ToList();
        var expectedBlankCounts = ordered.Where(n => string.IsNullOrEmpty(n.rawName))
            .GroupBy(n => n.IsFolderLike).Select(group => (object)new Dictionary<string, object>
            {
                ["path"] = EEMPath.Combine(destFolderInternalPath, ""),
                ["foldersOnly"] = group.Key,
                ["minimumCount"] = destNode.children.Count(n => string.IsNullOrEmpty(n.rawName) && n.IsFolderLike == group.Key) +
                    group.Count(n => n.parent != destNode),
            }).ToList();

        LogOp("BulkMove", logFrom, "Multiple", logFrom, destFolderInternalPath, () =>
        {
            var failures = new List<string>();
            var completedGroups = new HashSet<string>();
            bulkMoveSourceKeys = new HashSet<string>(ordered.Select(n => n.stableKey));
            try
            {
                foreach (var n in ordered)
                {
                    var live = ResolveLiveNode(n);
                    try
                    {
                        if (live == null) throw new System.InvalidOperationException("A selected item no longer exists after the previous move.");
                        destNode = ResolveMoveDestination(destNode, destFolderInternalPath);
                        if (destNode == null) throw new System.InvalidOperationException("The destination folder no longer exists after the previous move.");
                        destFolderInternalPath = PathOf(destNode, true);
                        string sourcePath = PathOf(live);
                        bool groupMove = !live.IsFolderLike && !IsDirectToggle(live) && !string.IsNullOrEmpty(live.rawName) &&
                            FindAllNodesAtPath(root, sourcePath).Count > 1 &&
                            FindAllNodesAtPath(root, sourcePath).All(candidate => bulkMoveSourceKeys.Contains(candidate.stableKey));
                        if (groupMove && completedGroups.Contains(sourcePath)) continue;
                        PerformMove(live, destFolderInternalPath, null);
                        if (groupMove) completedGroups.Add(sourcePath);
                        RefreshUnnamedMovePreview();
                        // Keep this operation's control-copy map and selected row
                        // identities intact until the complete block has moved.
                    }
                    catch (System.Exception ex)
                    {
                        failures.Add(DisplayPathOf(live ?? n) + " — " + ex.Message);
                    }
                }
            }
            finally { bulkMoveSourceKeys.Clear(); }
            if (failures.Count > 0)
                throw new System.InvalidOperationException(
                    $"{failures.Count} of {ordered.Count} item(s) could not be moved:\n• " + string.Join("\n• ", failures));
            if (expectedBlankCounts.Count > 0)
            {
                RefreshUnnamedMovePreview();
                destNode = ResolveMoveDestination(destNode, destFolderInternalPath);
                if (destNode == null) throw new System.InvalidOperationException("The destination folder disappeared during the move.");
                destFolderInternalPath = PathOf(destNode, true);
                expectedPaths.Clear();
                expectedPaths.AddRange(ordered.Select(n => (object)EEMPath.Combine(destFolderInternalPath, n.rawName)));
                foreach (var count in expectedBlankCounts.OfType<Dictionary<string, object>>()) count["path"] = EEMPath.Combine(destFolderInternalPath, "");
                var outcomeIssues = new List<object>();
                VerifyExpectedOutcome("BulkMove", logFrom, logFrom, destFolderInternalPath,
                    new Dictionary<string, object> { ["expectedBlankCounts"] = expectedBlankCounts }, outcomeIssues);
                if (outcomeIssues.Count > 0)
                    throw new System.InvalidOperationException(string.Join("\n", outcomeIssues));
            }
        }, new Dictionary<string, object>
        {
            ["itemCount"] = ordered.Count,
            ["destination"] = EEMPath.ToVrcf(destFolderInternalPath),
            ["expectedPaths"] = expectedPaths,
            ["expectedBlankCounts"] = expectedBlankCounts,
        });

        Undo.CollapseUndoOperations(undoGroup);
        ExitSelectionMode();
        FlushRescan();
    }


    void ApplyBulkIcon(Texture2D icon)
    {
        if (selectedNodes.Count == 0) return;
        var liveTargets = selectedNodes.Select(ResolveLiveNode).ToList();
        if (liveTargets.Any(n => n == null))
        {
            EditorUtility.DisplayDialog("Can't change icon", "A selected item changed or was removed. Rescan and select the complete block again.", "OK");
            return;
        }
        var targets = liveTargets.Where(n => n != root).Distinct().ToList();
        if (targets.Count == 0) return;

        // Direct source edits can distinguish unnamed controls. Only path
        // overrides need the shared-name guard.
        var ambiguous = new List<string>();
        foreach (var t in targets)
        {
            // only icons written as a path record can hit a same-name neighbour
            if (!IconIsPathBased(t)) continue;
            try { GuardUnambiguousPath(PathOf(t), "change the icon on"); }
            catch (System.Exception ex) { ambiguous.Add(ex.Message); }
        }
        if (ambiguous.Count > 0)
        {
            EditorUtility.DisplayDialog("Can't change icon",
                "One or more selected items are at an ambiguous path and can't be safely changed together:\n\n" +
                string.Join("\n\n", ambiguous), "OK");
            return;
        }

        SnapshotExpansionStates(root);
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName($"Set icon on {targets.Count} items");
        int undoGroup = Undo.GetCurrentGroup();
        string logPaths = string.Join(", ", targets.Select(n => PathOf(n)));

        LogOp("BulkIcon", logPaths, "Multiple", "(various)", icon ? icon.name : "(none)", () =>
        {
            foreach (var n in targets)
            {
                var live = ResolveLiveNode(n);
                if (live == null) throw new System.InvalidOperationException("A selected control changed during the icon operation; rescan before editing it.");
                if (!ApplyIconChange(live, icon))
                    throw new System.InvalidOperationException("Bulk icon change cancelled; all icon changes were rolled back.");
                RefreshUnnamedMovePreview();
            }
            // Bind selections to the copied controls before the final Scan
            // clears the batch's source-to-copy maps.
            RebindSelectionAfterScan(selected, targets, selectionAnchor);
            QueueRescan();
        }, new Dictionary<string, object>
        {
            ["itemCount"] = targets.Count,
            ["expectedIconId"] = icon ? icon.GetInstanceID() : 0,
            ["expectedIconTargets"] = targets.Select(n => (object)new Dictionary<string, object>
            {
                ["stableKey"] = n.stableKey,
                ["path"] = PathOf(n, n.IsFolderLike),
            }).ToList(),
        });

        Undo.CollapseUndoOperations(undoGroup);
        FlushRescan();
    }

}
