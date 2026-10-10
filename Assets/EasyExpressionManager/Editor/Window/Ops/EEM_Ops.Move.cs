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
            if (choice == 1) throw new System.OperationCanceledException("Move canceled.");
            keepSeparate = choice == 0;
            if (keepSeparate)
            {
                int ordinal = MatchVrcControls(destNode.children, node.rawName, true).Count;
                to = EEMPath.Combine(destFolderInternalPath, node.rawName + ".dup." + ordinal);
            }
        }

        bool nativeDirect = IsOrdinaryNativeNode(node) && !HasVrcfMoveSupport();
        // A Fury toggle is moved through its own component, so a same-name
        // neighbour can't be hit by mistake. Everything else is moved by a
        // path record, which VRCFury applies to every item with that name.
        var sourceMatches = FindAllNodesAtPath(root, from);
        bool individualMove = !nativeDirect && !IsDirectToggle(node) && !string.IsNullOrEmpty(node.rawName) && sourceMatches.Count > 1 &&
            (node.IsFolderLike || !sourceMatches.All(n => bulkMoveSourceKeys.Contains(n.stableKey)));
        if (!nativeDirect && !individualMove && !IsDirectToggle(node) && !string.IsNullOrEmpty(node.rawName))
            GuardUnambiguousPath(from, "move");
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

    string NativeMoveLeaf(ExpressionNode node, string toInternal)
    {
        string leaf = EEMPath.LeafOf(toInternal);
        // Final control names are literal. Only submenu Keep Separate
        // destinations carry a generated selector; a literal .dup. name on
        // the selected native control/folder must survive an ordinary move.
        if (!node.IsFolderLike || string.Equals(leaf, node.rawName, System.StringComparison.Ordinal))
            return leaf;
        if (string.Equals(leaf, node.rawName, System.StringComparison.Ordinal))
            return leaf; // A literal native name may itself contain ".dup.".

        // .dup.N is a folder selector used by the tree/path model, not a
        // literal native control name. Keep the selected base name when a
        // keep-separate move generated that selector. Blank folders use the
        // leading .dup.N form because an empty name otherwise means root.
        int selector = leaf.IndexOf(".dup.", System.StringComparison.Ordinal);
        if (selector >= 0)
        {
            string baseName = selector == 0 ? "" : leaf.Substring(0, selector);
            if ((selector == 0 && node.IsFolderLike && string.IsNullOrEmpty(node.rawName) ||
                 selector > 0 && baseName == node.rawName) &&
                int.TryParse(leaf.Substring(selector + 5), out int ordinal) && ordinal >= 0)
                return baseName;
            throw new System.InvalidOperationException(
                "The native destination uses an invalid or mismatched .dup.N selector; refusing to target another sibling.");
        }
        return leaf;
    }

    void MoveNativeNode(ExpressionNode node, string toInternal)
    {
        if (!(IsOrdinaryNativeNode(node) || IsNativeFolderNode(node)) || HasVrcfMoveSupport())
            throw new System.InvalidOperationException("This item is not eligible for a native-only move.");

        string destinationPath = EEMPath.ParentPath(toInternal);
        var destinationNode = string.IsNullOrEmpty(destinationPath)
            ? root : NodeByFolderPath(root, destinationPath);
        if (destinationNode == null)
            throw new System.InvalidOperationException("The native destination folder no longer exists. Rescan and try again.");
        string rawLeaf = EEMPath.LeafOf(toInternal);
        int selector = rawLeaf.IndexOf(".dup.", System.StringComparison.Ordinal);
        if (node.IsFolderLike && !string.Equals(rawLeaf, node.rawName, System.StringComparison.Ordinal) && selector >= 0)
        {
            if (selector == 0 && !node.IsFolderLike)
                throw new System.InvalidOperationException("A .dup.N selector is valid only for native folders; refusing to target another sibling.");
            string baseName = selector == 0 ? "" : rawLeaf.Substring(0, selector);
            if (!int.TryParse(rawLeaf.Substring(selector + 5), out int requestedOrdinal) || requestedOrdinal < 0)
                throw new System.InvalidOperationException("The native destination has an invalid .dup.N selector.");
            int availableOrdinal = MatchVrcControls(destinationNode.children.Where(n => n != node), baseName, node.IsFolderLike).Count;
            if (requestedOrdinal != availableOrdinal)
                throw new System.InvalidOperationException(
                    $"The native destination selector '{rawLeaf}' does not name the next '{baseName}' sibling; refusing to move to a different row.");
        }
        string leaf = NativeMoveLeaf(node, toInternal);
        if (string.IsNullOrEmpty(leaf) &&
            !(node.IsFolderLike && string.IsNullOrEmpty(node.rawName) && selector == 0))
            throw new System.InvalidOperationException("A native move destination must include a control or folder name.");

        // Native menus cannot merge two submenu controls the way VRCFury does.
        // Refuse the Combine choice before clone-on-write or source mutation,
        // so the dialog's wording can never result in two unmerged folders.
        if (node.IsFolderLike && FolderMergeTarget(node, toInternal) != null)
            throw new System.InvalidOperationException(
                "Native folders cannot be combined without VRCFury. Choose Keep Separate or install VRCFury.");

        var destination = NativeMenuForFolder(destinationNode);
        if (!destination)
        {
            if (destinationNode.isVirtualFolder || destinationNode.isVirtualIntermediate ||
                destinationNode.isMoveGeneratedFolder || destinationNode.isFCPrefixFolder)
                throw new System.InvalidOperationException(
                    "A native-only move cannot target a VRCFury virtual/generated folder. Create a native submenu there or install VRCFury.");
            throw new System.InvalidOperationException("The destination is not a writable native expression menu.");
        }

        // UI ancestry is not sufficient when two submenu controls share an
        // asset. Moving a submenu control into any menu reachable from its own
        // submenu would create a serialized graph cycle.
        if (node.sourceControl.subMenu && destination != node.sourceMenu &&
            MenuGraphContains(node.sourceControl.subMenu, destination))
            throw new System.InvalidOperationException(
                "Cannot move a native folder into its own submenu graph.");
        if (node.sourceMenu.controls.IndexOf(node.sourceControl) < 0)
            throw new System.InvalidOperationException("The native control changed before it could be moved.");
        if (destination != node.sourceMenu) EnsureNativeMenuCanAppend(destination, "move the native item");

        var source = EnsureWritableNativeMenu(node);
        node = ResolveLiveNode(node);
        destinationNode = ResolveLiveNode(destinationNode);
        destination = NativeMenuForFolder(destinationNode);
        if (!source || !destination || node == null || node.sourceControl == null)
            throw new System.InvalidOperationException("The native menu changed; rescan and try again.");
        int sourceIndex = source.controls.IndexOf(node.sourceControl);
        if (sourceIndex < 0)
            throw new System.InvalidOperationException("The native control changed before it could be moved.");
        if (source != destination) EnsureNativeMenuCanAppend(destination, "move the native item");
        ConfirmSharedNativeDestination(destination, source);

        Undo.RecordObject(source, "Move native expression item");
        if (source != destination) Undo.RecordObject(destination, "Move native expression item");
        var moved = source.controls[sourceIndex];
        if (source != destination) source.controls.RemoveAt(sourceIndex);
        moved.name = leaf;
        if (source != destination) destination.controls.Add(moved);
        EditorUtility.SetDirty(source);
        if (source != destination) EditorUtility.SetDirty(destination);
    }

    void RemoveNativeFolderLink(ExpressionNode node)
    {
        if (!IsNativeFolderNode(node) || HasVrcfMoveSupport())
            throw new System.InvalidOperationException("This folder is not eligible for native-only removal.");
        if (node.sourceMenu.controls.IndexOf(node.sourceControl) < 0)
            throw new System.InvalidOperationException("The native folder changed before it could be removed.");

        var source = EnsureWritableNativeMenu(node);
        node = ResolveLiveNode(node);
        if (!source || node == null || node.sourceControl == null)
            throw new System.InvalidOperationException("The native folder changed; rescan and try again.");
        int index = source.controls.IndexOf(node.sourceControl);
        if (index < 0)
            throw new System.InvalidOperationException("The native folder changed before it could be removed.");
        Undo.RecordObject(source, "Remove dissolved native folder");
        source.controls.RemoveAt(index);
        EditorUtility.SetDirty(source);
    }

    bool CanNativeReorder(ExpressionNode node, int direction = 0)
    {
        // This edits the serialized order of an ordinary native menu asset. It
        // remains safe when Fury is installed because IsOrdinaryNativeNode
        // excludes managed/merged/generated sources and requires a native
        // parent; those rows continue through their existing Fury paths.
        if (!IsOrdinaryNativeNode(node)) return false;
        node = ResolveLiveNode(node);
        var menu = node == null ? null : NativeMenuForFolder(node.parent);
        int index = menu == null || node?.sourceControl == null ? -1 : menu.controls.IndexOf(node.sourceControl);
        return index >= 0 && (direction == 0 || index + direction >= 0 && index + direction < menu.controls.Count);
    }

    void ReorderNativeSibling(ExpressionNode node, int direction)
    {
        if (!CanNativeReorder(node, direction)) return;
        node = ResolveLiveNode(node);
        var menu = NativeMenuForFolder(node.parent);
        int index = menu.controls.IndexOf(node.sourceControl);
        int target = index + direction;
        var writable = EnsureWritableNativeMenu(node);
        node = ResolveLiveNode(node);
        menu = NativeMenuForFolder(node.parent);
        index = menu.controls.IndexOf(node.sourceControl);
        target = index + direction;
        if (!writable || target < 0 || target >= menu.controls.Count) return;
        Undo.RecordObject(menu, direction < 0 ? "Move native expression item up" : "Move native expression item down");
        var control = menu.controls[index];
        menu.controls.RemoveAt(index);
        menu.controls.Insert(target, control);
        EditorUtility.SetDirty(menu);
        QueueRescan();
        Repaint();
    }

    /// The actual write for a move, shared by PerformMove and the Move dialog
    /// (which used to carry its own copy of this branch).
    void ApplyMoveByKind(ExpressionNode node, string from, string to)
    {
        // Blank native controls deliberately stay on MoveUnnamedControl above;
        // native-only moves here are named controls/folders only.
        if (IsOrdinaryNativeNode(node) && !string.IsNullOrEmpty(node.rawName) && !HasVrcfMoveSupport())
        {
            MoveNativeNode(node, to);
            return;
        }
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
        if (node?.sourceObject == null)
            throw new System.InvalidOperationException("The VRCFury toggle source is missing; rescan and try again.");
        GuardAddressable(toInternalPath, "move");
        var so = FreshSO(node.sourceObject);
        if (so?.targetObject == null)
            throw new System.InvalidOperationException("The VRCFury toggle source could not be read; rescan and try again.");
        var nameProp = so?.FindProperty("content.name");
        if (nameProp?.propertyType != SerializedPropertyType.String)
            throw new System.InvalidOperationException("The VRCFury toggle has no editable content.name field.");
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
                // The edited From path is authoritative. Falling back to the
                // initially selected row could move a different item when the
                // user typed an invalid or stale path.
                var moving = NodeByPath(root, from);
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
                        if (choice == 1) throw new System.OperationCanceledException("Move canceled.");
                        if (choice == 0)
                        {
                            var parent = NodeByFolderPath(root, EEMPath.ParentPath(to));
                            int ordinal = MatchVrcControls(parent.children, EEMPath.LeafOf(to), true).Count;
                            to = EEMPath.ReplaceLastSegment(to, EEMPath.LeafOf(to) + ".dup." + ordinal);
                        }
                    }
                }

                GuardDestinationChainUnambiguous(to, "move");
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
