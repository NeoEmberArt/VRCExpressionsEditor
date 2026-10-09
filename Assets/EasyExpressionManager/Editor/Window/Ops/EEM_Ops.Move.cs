// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — single-item move
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

    /// Single entry point used by drag-drop, the move dialog, bulk move and
    /// structure import.
    void PerformMove(ExpressionNode node, string destFolderInternalPath, ExpressionNode destNode,
                     bool confirmFolderMerge = true)
    {
        node = ResolveLiveNode(node);
        if (node == null || node == root) throw new System.InvalidOperationException("The moving item no longer exists. Rescan and try again.");
        // The path was captured at drop/dialog time. Never derive it again
        // from a destination row left over from an earlier scan.
        destNode = string.IsNullOrEmpty(destFolderInternalPath) ? root : NodeByFolderPath(root, destFolderInternalPath);
        if (destNode == null) throw new System.InvalidOperationException("The destination folder no longer exists. Rescan and try again.");
        destFolderInternalPath = PathOf(destNode, true);
        string from = PathOf(node);
        string to   = EEMPath.Combine(destFolderInternalPath, node.rawName);

        if (destNode != null) GuardNoCycle(node, destNode);
        if (EEMPath.IsUnder(to, from))
            throw new System.InvalidOperationException(
                $"Cannot move '{DisplayOf(node)}' inside itself.");
        if (EEMPath.PathEq(from, to)) return;

        var mergeInto = string.IsNullOrEmpty(node.rawName) ? null : FolderMergeTarget(node, to);
        bool keepSeparate = mergeInto == null;
        if (mergeInto != null && confirmFolderMerge)
        {
            int choice = EditorUtility.DisplayDialogComplex("Name already used",
                $"\"{DisplayOf(mergeInto)}\" already exists there. Combine their contents or keep two separate folders?",
                "Keep Separate", "Cancel", "Combine");
            if (choice == 1) return;
            keepSeparate = choice == 0;
            if (keepSeparate)
            {
                int ordinal = MatchVrcControls(destNode.children, node.rawName, true).Count;
                to = EEMPath.Combine(destFolderInternalPath, node.rawName + ".dup." + ordinal);
            }
        }

        // A Fury toggle is moved through its own component, so a same-name
        // neighbour can't be hit by mistake. Everything else is moved by a
        // path record, which VRCFury applies to every item with that name.
        var sourceMatches = FindAllNodesAtPath(root, from);
        bool individualMove = !IsDirectToggle(node) && !string.IsNullOrEmpty(node.rawName) && sourceMatches.Count > 1 &&
            (node.IsFolderLike || !sourceMatches.All(n => bulkMoveSourceKeys.Contains(n.stableKey)));
        if (!individualMove && !IsDirectToggle(node) && !string.IsNullOrEmpty(node.rawName)) GuardUnambiguousPath(from, "move");
        GuardDestinationChainUnambiguous(to, "move");

        if (individualMove) MoveIndividualControl(node, destFolderInternalPath, null, keepSeparate);
        else ApplyMoveByKind(node, from, to);
        QueueRescan();
    }

    void MoveIndividualControl(ExpressionNode node, string destination, string destinationLeaf = null, bool keepSeparateFolder = true)
    {
        if (node.IsFolderLike)
        {
            MoveUnnamedFolder(node, destination, destinationLeaf ?? node.rawName, keepSeparateFolder);
            return;
        }
        if (node.sourceControl == null)
            throw new System.InvalidOperationException("This duplicate has no individual source. Rename its source first.");
        string leaf = destinationLeaf ?? node.rawName;
        var source = node.isFromFullController ? EnsureWritableFCMenu(node, out _) : EnsureWritableNativeMenu(node);
        if (!source || !source.controls.Contains(node.sourceControl))
            throw new System.InvalidOperationException("The selected control's source changed. Rescan and try again.");
        var trace = BuildMoveSourceTree();
        var ownRecords = new List<MoveRecord>();
        foreach (var record in OrderMovesByDependency(ReadMoveRecords(), trace))
        {
            var matches = FindAllNodesAtPath(trace, record.from);
            bool SameSource(ExpressionNode candidate) => candidate.sourceControl == node.sourceControl &&
                candidate.isFromFullController == node.isFromFullController &&
                (!node.isFromFullController || candidate.sourceFullControllerObject?.targetObject == node.sourceFullControllerObject?.targetObject &&
                    candidate.fcEntryIndex == node.fcEntryIndex);
            if (matches.Count == 1 && SameSource(matches[0])) ownRecords.Add(record);
            ApplyMove(trace, record.from, record.to, record.comp);
        }
        Undo.RecordObject(source, "Move selected duplicate");
        string temporary = "__EEM_item_" + System.Guid.NewGuid().ToString("N");
        node.sourceControl.name = temporary;
        node.sourceControl.icon = node.icon;
        EditorUtility.SetDirty(source);
        foreach (var record in ownRecords)
        {
            Undo.RecordObject(record.comp, "Address selected duplicate");
            WriteRecordFields(new MoveRecord { comp = record.comp,
                from = EEMPath.ReplaceLastSegment(record.from, temporary),
                to = EEMPath.ReplaceLastSegment(record.to, temporary) });
        }
        RefreshUnnamedMovePreview();
        var staged = ResolveLiveNode(node);
        if (staged == null) throw new System.InvalidOperationException("The selected duplicate could not be located after staging.");
        CommitMove(PathOf(staged), EEMPath.Combine(destination, leaf), staged);
    }

    /// The actual write for a move, shared by PerformMove and the Move dialog
    /// (which used to carry its own copy of this branch).
    void ApplyMoveByKind(ExpressionNode node, string from, string to)
    {
        var matches = FindAllNodesAtPath(root, from);
        if (!string.IsNullOrEmpty(node.rawName) && node.sourceControl != null && matches.Count > 1 &&
            (node.IsFolderLike || !matches.All(n => bulkMoveSourceKeys.Contains(n.stableKey))))
        {
            MoveIndividualControl(node, EEMPath.ParentPath(to), EEMPath.LeafOf(to));
            return;
        }
        // A child of a moved generated folder is still positioned by that
        // ancestor's record. Renaming its Fury source directly would vacate
        // the original prefix and leave the ancestor record unresolved.
        bool positionedByMove = node.hasManagedMove;
        for (var parent = node.parent; parent != null && !positionedByMove; parent = parent.parent)
            positionedByMove = parent.hasManagedMove;
        if (!string.IsNullOrEmpty(to) && string.IsNullOrEmpty(node.rawName) && node.sourceControl != null)
        {
            if (node.IsFolderLike && !string.IsNullOrEmpty(EEMPath.LeafOf(to)))
                MoveUnnamedFolder(node, EEMPath.ParentPath(to), EEMPath.LeafOf(to));
            else MoveUnnamedControl(node, EEMPath.ParentPath(to));
        }
        else if (positionedByMove)
        {
            // Keep an existing move's native source valid when editing its
            // resolved destination, including moves authored outside this tool.
            CommitMove(from, to, node);
        }
        else if (IsDirectToggle(node) && !string.IsNullOrEmpty(to))
        {
            MoveFuryToggleTo(node, to);
        }
        else if (node.isVirtualFolder && !HasRealFolderSource(node))
        {
            RewriteAllPathReferences(from, to, node);
            // RewriteAllPathReferences renames the registration by TREE path;
            // when a parent folder was moved by a record the registration lives
            // somewhere else and that rename missed — use the real one.
            if (!string.IsNullOrEmpty(to)) MoveVirtualFolderRegistration(node, to);
            return;   // icon records already handled by RewriteAllPathReferences
        }
        else
        {
            CommitMove(from, to, node);
        }

        // Override Menu Icon records address the FINAL (post-move) tree, so any
        // that sat at or under the moved item have to follow it. Without this a
        // virtual menu's icon was orphaned the moment its parent folder moved.
        if (!string.IsNullOrEmpty(to)) RewriteIconOverridesForPathChange(from, to);
    }

    void MoveFuryToggleTo(ExpressionNode node, string toInternalPath)
    {
        if (node?.sourceObject == null) return;
        GuardAddressable(toInternalPath, "move");
        var so = FreshSO(node.sourceObject);
        var nameProp = so?.FindProperty("content.name");
        if (nameProp?.propertyType != SerializedPropertyType.String) return;
        if (so.targetObject != null) Undo.RecordObject(so.targetObject, "Move Toggle");
        nameProp.stringValue = EEMPath.ToVrcf(toInternalPath);
        so.ApplyModifiedProperties();
        node.sourceObject = so;
        if (so.targetObject != null) EditorUtility.SetDirty(so.targetObject);
    }

    void OpenMoveDialog(ExpressionNode node, ExpressionNode suggestedTarget = null)
    {
        if (node == null || node == root) return;
        string cur = PathOf(node);
        string sugg = suggestedTarget != null
            ? EEMPath.Combine(PathOf(suggestedTarget, true), node.rawName)
            : cur;

        MoveDialog.Open(EEMPath.ToVrcf(cur), EEMPath.ToVrcf(sugg), CollectAllPaths(root).Select(EEMPath.ToVrcf).ToList(),
            (fromRaw, toRaw) =>
        {
            string from = EEMPath.FromVrcfControl(fromRaw);
            string to   = EEMPath.FromVrcf(toRaw);

            SnapshotExpansionStates(root);
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Move menu item");
            int grp = Undo.GetCurrentGroup();

            LogOp("Move", from, node.type.ToString(), from, to, () =>
            {
                if (EEMPath.IsUnder(to, from))
                    throw new System.InvalidOperationException("Destination is inside the item being moved.");
                var moving = NodeByPath(root, from) ?? ResolveLiveNode(node);
                if (moving == null || moving == root)
                    throw new System.InvalidOperationException("The moving item no longer exists. Rescan and try again.");
                if (!EEMPath.PathEq(from, to))
                {
                    var mergeInto = FolderMergeTarget(moving, to);
                    if (mergeInto != null)
                    {
                        int choice = EditorUtility.DisplayDialogComplex("Name already used",
                            $"\"{DisplayOf(mergeInto)}\" already exists there. Combine their contents or keep two separate folders?",
                            "Keep Separate", "Cancel", "Combine");
                        if (choice == 1) return;
                        if (choice == 0)
                        {
                            var parent = NodeByFolderPath(root, EEMPath.ParentPath(to));
                            int ordinal = MatchVrcControls(parent.children, EEMPath.LeafOf(to), true).Count;
                            to = EEMPath.ReplaceLastSegment(to, EEMPath.LeafOf(to) + ".dup." + ordinal);
                        }
                    }
                }

                ApplyMoveByKind(moving, from, to);
                QueueRescan();
            }, new Dictionary<string, object>
            {
                ["sourceCategory"] = SourceCategoryOf(node),
                ["via"] = "dialog",
                ["previousMatchCount"] = FindAllNodesAtPath(root, from).Count,
            });

            Undo.CollapseUndoOperations(grp);
            FlushRescan();
            Repaint();
        });
    }

    string SourceCategoryOf(ExpressionNode n)
    {
        if (n == null) return "Unknown";
        if (n.sourceObject != null) return "VRCFuryToggle";
        if (n.isFromFullController) return "FullController";
        if (n.isVirtualFolder) return "VirtualFolder";
        if (n.sourceControl != null) return "VRCNative";
        if (n.isFCPrefixFolder) return "FCPrefixFolder";
        if (n.isMoveGeneratedFolder) return "MoveGeneratedFolder";
        return "Folder";
    }

    /// A short human label for an ambiguous-path error — e.g. "FullController
    /// SubMenu" or plain "FCPrefixFolder" when the category already implies
    /// the shape (folders don't need "Folder" appended twice).
    string DescribeSource(ExpressionNode n)
    {
        string cat = SourceCategoryOf(n);
        bool catImpliesType = cat is "FCPrefixFolder" or "VirtualFolder" or "MoveGeneratedFolder" or "Folder";
        return catImpliesType ? cat : $"{cat} {n.type}";
    }

}
