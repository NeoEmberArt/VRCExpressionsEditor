using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    // VRCFury treats a blank MoveMenuItem destination as deletion. Relocate a
    // blank control through its backing menu instead, preserving its blank name.
    void MoveUnnamedControl(ExpressionNode node, string destination)
    {
        if (node.IsFolderLike)
        {
            MoveUnnamedFolder(node, destination);
            return;
        }
        var source = node.isFromFullController ? EnsureWritableFCMenu(node, out _) : EnsureWritableNativeMenu(node);
        if (!source || node.sourceControl == null)
            throw new InvalidOperationException("The unnamed control's source menu could not be resolved.");
        int index = source.controls.IndexOf(node.sourceControl);
        if (index < 0) throw new InvalidOperationException("The unnamed control changed before it could be moved.");

        VRCExpressionsMenu destinationMenu;
        if (node.isFromFullController)
        {
            var template = CreateInstance<VRCExpressionsMenu>();
            template.name = "Unnamed control";
            try { destinationMenu = CloneMenuAsset(template); }
            finally { DestroyImmediate(template); }
            var so = FreshSO(node.sourceFullControllerObject);
            var menus = so.FindProperty("content.menus");
            Undo.RecordObject(so.targetObject, "Move unnamed Full Controller control");
            int entryIndex = menus.arraySize;
            menus.arraySize++;
            var entry = menus.GetArrayElementAtIndex(entryIndex);
            entry.FindPropertyRelative("menu.objRef").objectReferenceValue = destinationMenu;
            var prefix = entry.FindPropertyRelative("prefix");
            if (prefix != null) prefix.stringValue = EEMPath.ToVrcf(destination);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(so.targetObject);
        }
        else
        {
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            destinationMenu = descriptor ? descriptor.expressionsMenu : null;
            if (!destinationMenu) throw new InvalidOperationException("The native expression menu could not be resolved.");
            var target = string.IsNullOrEmpty(destination) ? root : NodeByFolderPath(root, destination);
            string nativePath = target == root ? "" : NativePathOf(target);
            foreach (var segment in EEMPath.SplitPath(nativePath))
            {
                string name = segment;
                int occurrence = 0, selector = name.IndexOf(".dup.", StringComparison.Ordinal);
                if (selector >= 0)
                {
                    if (!int.TryParse(name.Substring(selector + 5), out occurrence) || occurrence < 0)
                        throw new InvalidOperationException("Invalid destination submenu selector.");
                    name = name.Substring(0, selector);
                }
                var candidates = destinationMenu.controls.FindAll(c => c != null &&
                    c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && c.name == name);
                while (candidates.Count <= occurrence)
                {
                    var template = CreateInstance<VRCExpressionsMenu>();
                    template.name = string.IsNullOrEmpty(name) ? "Unnamed menu" : name;
                    VRCExpressionsMenu submenu;
                    try { submenu = CloneMenuAsset(template); }
                    finally { DestroyImmediate(template); }
                    Undo.RecordObject(destinationMenu, "Create native destination submenu");
                    var control = new VRCExpressionsMenu.Control
                    { name = name, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = submenu };
                    destinationMenu.controls.Add(control);
                    EditorUtility.SetDirty(destinationMenu);
                    candidates.Add(control);
                }
                var chosen = candidates[occurrence];
                if (!chosen.subMenu)
                {
                    var template = CreateInstance<VRCExpressionsMenu>();
                    template.name = string.IsNullOrEmpty(name) ? "Unnamed menu" : name;
                    try
                    {
                        Undo.RecordObject(destinationMenu, "Create native destination submenu");
                        chosen.subMenu = CloneMenuAsset(template);
                        EditorUtility.SetDirty(destinationMenu);
                    }
                    finally { DestroyImmediate(template); }
                }
                destinationMenu = chosen.subMenu;
            }
        }
        Undo.RecordObject(source, "Move unnamed control");
        Undo.RecordObject(destinationMenu, "Move unnamed control");
        var moved = source.controls[index];
        source.controls.RemoveAt(index);
        moved.icon = node.icon;
        destinationMenu.controls.Add(moved);
        EditorUtility.SetDirty(source);
        EditorUtility.SetDirty(destinationMenu);
        // Clear an older record governing this item, rather than allowing it to
        // send a newly relocated blank root control back to the old destination.
        if (node.hasManagedMove)
        {
            string path = PathOf(node);
            foreach (var record in ReadMoveRecords())
                if (EEMPath.PathEq(record.to, path)) Undo.DestroyObjectImmediate(record.comp);
        }
        RewriteIconOverridesForPathChange(PathOf(node), EEMPath.Combine(destination, ""));
    }

    // Giving each blank folder a separate FC menu entry makes all of them
    // ordinal zero: Fury merges their children into one folder. Instead give
    // its private backing controls a unique source name, then move that source
    // to an explicit blank-folder ordinal AFTER Fury merges the FC menus.
    // The built menu keeps the original blank name, icon and control settings.
    void MoveUnnamedFolder(ExpressionNode node, string destination, string renamedLeaf = null, bool keepSeparate = true)
    {
        var destinationNode = string.IsNullOrEmpty(destination) ? root : NodeByFolderPath(root, destination);
        if (destinationNode == null) throw new InvalidOperationException("The destination folder no longer exists.");
        bool destinationIsRoot = destinationNode == root;
        string oldLeaf = node.rawName ?? "";
        string oldPath = PathOf(node, true);
        int oldOrdinal = MatchVrcControls(node.parent.children, oldLeaf, true).IndexOf(node);
        string oldParent = PathOf(node.parent, true);
        int oldCount = MatchVrcControls(node.parent.children, oldLeaf, true).Count;
        string temporaryName = "__EEM_unnamed_" + Guid.NewGuid().ToString("N");
        string temporaryDestination = EEMPath.Combine(PathOf(node.parent, true), temporaryName);
        var sources = new List<ExpressionNode>();
        void CollectSources(ExpressionNode source)
        {
            if (sources.Contains(source)) return;
            sources.Add(source);
            foreach (var merged in source.mergedSources) CollectSources(merged);
        }
        CollectSources(node);

        var records = ReadMoveRecords();
        var physicalTree = BuildMoveSourceTree();
        var physicalNodes = CollectAllDescendants(physicalTree);
        var renamedPaths = new List<(string from, string to)>();
        bool MatchesBacking(ExpressionNode candidate, ExpressionNode sourceNode)
        {
            var control = sourceNode.sourceControl;
            if (control == null)
                return (sourceNode.isVirtualFolder && candidate.isVirtualFolder &&
                    EEMPath.PathEq(candidate.virtualPath, sourceNode.virtualPath)) ||
                    candidate.mergedSources.Any(merged => MatchesBacking(merged, sourceNode));
            var owner = sourceNode.sourceFullControllerObject?.targetObject;
            if (sourceNode.isFromFullController && owner != null &&
                fcControlCloneMap.TryGetValue((owner, sourceNode.fcEntryIndex, control), out var copied)) control = copied;
            if ((candidate.sourceControl == control || candidate.sourceControl == sourceNode.sourceControl) &&
                (!sourceNode.isFromFullController || candidate.sourceKey == sourceNode.sourceKey || candidate.sourceFullControllerObject?.targetObject == sourceNode.sourceFullControllerObject?.targetObject &&
                    candidate.fcEntryIndex == sourceNode.fcEntryIndex)) return true;
            return candidate.mergedSources.Any(merged => MatchesBacking(merged, sourceNode));
        }
        // Trace the terminal move for each backing folder. Retargeting the
        // first record breaks a later staging record after a second move.
        var terminalMoves = new Dictionary<ExpressionNode, Component>();
        var traceTree = CopyMoveTree(physicalTree);
        foreach (var record in OrderMovesByDependency(records, physicalTree))
        {
            var moving = FindAllNodesAtPath(traceTree, record.from);
            foreach (var sourceNode in sources.Where(s => s.isVirtualFolder || s.sourceControl != null && !string.IsNullOrEmpty(s.sourceControl.name)))
                if (moving.Any(candidate => MatchesBacking(candidate, sourceNode))) terminalMoves[sourceNode] = record.comp;
            ApplyMove(traceTree, record.from, record.to, record.comp);
        }
        var stagedRecords = new HashSet<Component>();
        bool hasSource = false;
        foreach (var sourceNode in sources.Where(s => s.sourceControl != null))
        {
            if (!string.IsNullOrEmpty(sourceNode.sourceControl.name) && terminalMoves.TryGetValue(sourceNode, out var terminal))
            {
                // A previous unnamed-folder move already gave this source a
                // unique name. Stage its own record without touching a peer.
                if (stagedRecords.Add(terminal))
                {
                    var record = records.First(r => r.comp == terminal);
                    Undo.RecordObject(record.comp, "Stage unnamed folder move");
                    WriteRecordFields(new MoveRecord { comp = record.comp, from = record.from, to = temporaryDestination });
                }
                hasSource = true;
                continue;
            }
            var physical = physicalNodes.FirstOrDefault(candidate => MatchesBacking(candidate, sourceNode));
            if (physical == null) throw new InvalidOperationException("The unnamed folder's original path could not be resolved.");
            string physicalPath = PathOf(physical, true);
            renamedPaths.Add((physicalPath, EEMPath.Combine(EEMPath.ParentPath(physicalPath), temporaryName)));
            var source = sourceNode.isFromFullController ? EnsureWritableFCMenu(sourceNode, out _) : EnsureWritableNativeMenu(sourceNode);
            if (!source || !source.controls.Contains(sourceNode.sourceControl))
                throw new InvalidOperationException("The unnamed folder's source control could not be resolved.");
            Undo.RecordObject(source, "Address unnamed folder move");
            sourceNode.sourceControl.name = temporaryName;
            sourceNode.sourceControl.icon = node.icon;
            EditorUtility.SetDirty(source);
            hasSource = true;
        }

        // Virtual folders are backed by saved registrations, not controls.
        // Retarget their terminal record when already moved; otherwise give
        // the selected registration a private staging path like a control.
        foreach (var sourceNode in sources.Where(s => s.isVirtualFolder))
        {
            if (terminalMoves.TryGetValue(sourceNode, out var terminal))
            {
                if (stagedRecords.Add(terminal))
                {
                    var record = records.First(r => r.comp == terminal);
                    Undo.RecordObject(record.comp, "Stage virtual folder move");
                    WriteRecordFields(new MoveRecord { comp = record.comp, from = record.from, to = temporaryDestination });
                }
            }
            else
            {
                var physical = physicalNodes.FirstOrDefault(candidate => MatchesBacking(candidate, sourceNode));
                if (physical == null) throw new InvalidOperationException("The virtual folder's registration could not be resolved.");
                string physicalPath = PathOf(physical, true);
                string registration = VirtualPathOf(sourceNode);
                string stagedRegistration = EEMPath.Combine(EEMPath.ParentPath(registration), temporaryName);
                renamedPaths.Add((physicalPath, EEMPath.Combine(EEMPath.ParentPath(physicalPath), temporaryName)));
                RenameVirtualFolderPath(registration, stagedRegistration);
            }
            hasSource = true;
        }

        // Prefix contributors belong to their original FC component, including
        // its parameter rewriting. Rename only the entries captured for this row.
        foreach (var generator in node.fcPrefixGenerators)
        {
            var so = FreshSO(generator.owner);
            var entries = so?.FindProperty("content.menus");
            if (entries == null) throw new InvalidOperationException("An unnamed folder prefix could not be resolved.");
            int segmentIndex = EEMPath.Depth(NativePathOf(node)) - 1;
            var parts = EEMPath.SplitPath(EEMPath.FromVrcf(generator.prefix));
            if (segmentIndex < 0 || segmentIndex >= parts.Length)
                throw new InvalidOperationException("The unnamed folder prefix changed during the operation.");
            parts[segmentIndex] = temporaryName;
            Undo.RecordObject(so.targetObject, "Address unnamed folder prefix");
            for (int i = 0; i < entries.arraySize; i++)
            {
                var prefix = entries.GetArrayElementAtIndex(i).FindPropertyRelative("prefix");
                if (prefix != null && prefix.stringValue == generator.prefix)
                    prefix.stringValue = EEMPath.ToVrcf(EEMPath.JoinPath(parts));
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(so.targetObject);
            hasSource = true;
        }
        if (!hasSource) throw new InvalidOperationException("The unnamed folder has no writable source.");

        // Renaming an earlier backing folder lowers its peers' ordinals.
        // Their registrations (including child registrations) must follow the
        // same shift as move sources, or a rescan creates an extra empty peer.
        bool shiftedVirtualPaths = false;
        foreach (var rename in renamedPaths.Distinct())
        {
            string part = EEMPath.LeafOf(rename.from);
            int selector = part.IndexOf(".dup.", StringComparison.Ordinal);
            string name = selector < 0 ? part : part.Substring(0, selector);
            int removedOrdinal = selector < 0 ? 0 : int.Parse(part.Substring(selector + 5));
            for (int i = 0; i < virtualFolderPaths.Count; i++)
            {
                string shifted = ShiftDuplicateFolderPath(virtualFolderPaths[i], EEMPath.ParentPath(rename.from), name, removedOrdinal);
                if (shifted == virtualFolderPaths[i]) continue;
                virtualFolderPaths[i] = shifted;
                shiftedVirtualPaths = true;
            }
        }
        if (shiftedVirtualPaths) SaveVirtualFolders();

        foreach (var record in ReadMoveRecords())
        {
            string from = record.from, to = record.to;
            foreach (var rename in renamedPaths.Distinct())
            {
                string part = EEMPath.LeafOf(rename.from);
                int selector = part.IndexOf(".dup.", StringComparison.Ordinal);
                string name = selector < 0 ? part : part.Substring(0, selector);
                int removedOrdinal = selector < 0 ? 0 : int.Parse(part.Substring(selector + 5));
                from = EEMPath.IsAtOrUnder(from, rename.from) ? EEMPath.RewriteSeg(from, rename.from, rename.to) :
                    ShiftDuplicateFolderPath(from, EEMPath.ParentPath(rename.from), name, removedOrdinal);
            }
            to = EEMPath.IsAtOrUnder(to, oldPath) ? EEMPath.RewriteSeg(to, oldPath, temporaryDestination) :
                ShiftDuplicateFolderPath(to, oldParent, oldLeaf, oldOrdinal);
            if (from != record.from || to != record.to)
            {
                Undo.RecordObject(record.comp, "Follow unnamed folder move");
                WriteRecordFields(new MoveRecord { comp = record.comp, from = from, to = to });
            }
        }

        RewriteIconOverridesForPathChange(oldPath, temporaryDestination);
        for (int i = oldOrdinal + 1; i < oldCount; i++)
            RewriteIconOverridesForPathChange(EEMPath.Combine(oldParent, oldLeaf + ".dup." + i), EEMPath.Combine(oldParent, oldLeaf + ".dup." + (i - 1)));
        RefreshUnnamedMovePreview();
        destinationNode = destinationIsRoot ? root : ResolveMoveDestination(destinationNode, destination);
        if (destinationNode == null) throw new InvalidOperationException("The destination folder changed during the move.");
        string finalLeaf = renamedLeaf ?? "";
        int ordinal = MatchVrcControls(destinationNode.children, finalLeaf, true).Count;
        if (keepSeparate && (ordinal > 0 || finalLeaf == "")) finalLeaf += ".dup." + ordinal;
        string finalPath = EEMPath.Combine(PathOf(destinationNode, true), finalLeaf);
        RewriteIconOverridesForPathChange(temporaryDestination, finalPath);
        var stagedPaths = allNodesFlatList.Where(n => n.IsFolderLike && n.rawName == temporaryName).Select(n => PathOf(n)).ToList();
        if (stagedPaths.Count == 0) throw new InvalidOperationException("The unnamed folder could not be staged for its move.");
        foreach (string stagedPath in stagedPaths)
        {
            var staged = NodeByFolderPath(root, stagedPath);
            if (staged == null) throw new InvalidOperationException("An unnamed folder contributor disappeared during its move.");
            CommitMove(stagedPath, finalPath, staged);
            RefreshUnnamedMovePreview();
        }
        if (allNodesFlatList.Any(n => n.rawName == temporaryName))
            throw new InvalidOperationException("An unnamed folder contributor remained behind after its move.");
    }

    static string ShiftBlankFolderPath(string path, string parent, int removedOrdinal)
    {
        var parts = EEMPath.SplitPath(path);
        var prefix = EEMPath.SplitPath(parent);
        if (parts.Length <= prefix.Length || !parts.Take(prefix.Length).SequenceEqual(prefix)) return path;
        string part = parts[prefix.Length];
        if (!part.StartsWith(".dup.", StringComparison.Ordinal) || !int.TryParse(part.Substring(5), out int ordinal) || ordinal <= removedOrdinal) return path;
        parts[prefix.Length] = ".dup." + (ordinal - 1);
        return EEMPath.JoinPath(parts);
    }

    static string ShiftDuplicateFolderPath(string path, string parent, string name, int removedOrdinal)
    {
        var parts = EEMPath.SplitPath(path);
        var prefix = EEMPath.SplitPath(parent);
        if (parts.Length <= prefix.Length || !parts.Take(prefix.Length).SequenceEqual(prefix)) return path;
        string selector = name + ".dup.";
        string part = parts[prefix.Length];
        if (!part.StartsWith(selector, StringComparison.Ordinal) ||
            !int.TryParse(part.Substring(selector.Length), out int ordinal) || ordinal <= removedOrdinal) return path;
        parts[prefix.Length] = name + ".dup." + (ordinal - 1);
        return EEMPath.JoinPath(parts);
    }

    void RefreshUnnamedMovePreview()
    {
        // Do not clear the batch's clone maps as a full Scan would do: later
        // selections still refer to controls in the original menu asset.
        root = BuildMoveSourceTree();
        foreach (var record in OrderMovesByDependency(ReadMoveRecords(), root))
            ApplyMove(root, record.from, record.to, record.comp);
        AssignStableKeys(root, "");
        allNodesFlatList = CollectAllDescendants(root);
    }
}
