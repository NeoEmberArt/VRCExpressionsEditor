// Easy Expression Manager: build and validate prospective menu layouts.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    ExpressionNode BuildMoveSourceTree()
    {
        var tree = new ExpressionNode { rawName = "Expression Menu", type = NodeType.Folder };
        var descriptor = avatar ? avatar.GetComponent<VRCAvatarDescriptor>() : null;
        if (descriptor?.expressionsMenu) BuildVRCMenu(tree, descriptor.expressionsMenu, new HashSet<VRCExpressionsMenu>());
        if (avatar) ScanVRCFury(avatar.transform, tree);
        InjectVirtualFolders(tree);
        CaptureNativePaths(tree);
        return tree;
    }

    ExpressionNode CopyMoveTree(ExpressionNode node, ExpressionNode parent = null)
    {
        if (node == null) return null;
        var copy = new ExpressionNode {
            rawName = node.rawName, type = node.type, parent = parent,
            sourceKey = node.sourceKey, nativePath = node.nativePath,
            sourceControl = node.sourceControl, sourceObject = node.sourceObject,
            sourceControlCount = node.sourceControlCount,
            hasNativeFolderSource = node.hasNativeFolderSource,
            isVirtualFolder = node.isVirtualFolder, virtualPath = node.virtualPath,
            isMoveGeneratedFolder = node.isMoveGeneratedFolder, isVirtualIntermediate = node.isVirtualIntermediate,
            isFromFullController = node.isFromFullController, isFCPrefixFolder = node.isFCPrefixFolder,
            hasManagedMove = node.hasManagedMove,
            appliedMoveGenerators = new List<Component>(node.appliedMoveGenerators),
            moveGenerators = new List<Component>(node.moveGenerators),
        };
        foreach (var child in node.children) copy.children.Add(CopyMoveTree(child, copy));
        foreach (var merged in node.mergedSources) copy.mergedSources.Add(CopyMoveTree(merged, parent));
        return copy;
    }

    string MoveTreeLayout(ExpressionNode tree)
    {
        var rows = new List<string>();
        void Walk(ExpressionNode node)
        {
            bool transientEmpty = node.IsFolderLike && node.children.Count == 0 && (node.isMoveGeneratedFolder || node.isVirtualIntermediate) &&
                !node.isVirtualFolder && !node.hasNativeFolderSource && node.sourceControl == null && node.mergedSources.Count == 0;
            if (node != tree && !transientEmpty) rows.Add(PathOf(node, node.IsFolderLike) + ":" + (node.IsFolderLike ? "folder" : node.type.ToString()) +
                (node.IsFolderLike ? "" : ":" + node.sourceKey));
            foreach (var child in node.children) Walk(child);
        }
        Walk(tree);
        rows.Sort(System.StringComparer.Ordinal);
        return string.Join("\n", rows);
    }

    string movePlanMismatch;
    bool MovePlanMatches(List<MoveRecord> records, ExpressionNode sourceTree, ExpressionNode expectedTree)
    {
        var preview = CopyMoveTree(sourceTree);
        try
        {
            foreach (var record in OrderMovesByDependency(records, sourceTree))
                if (!ApplyMove(preview, record.from, record.to, record.comp) && !unresolvedMoveRecords.Contains(record.comp))
                { movePlanMismatch = "Source not found: " + EEMPath.ToVrcf(record.from); return false; }
        }
        catch (System.InvalidOperationException ex) { movePlanMismatch = ex.Message; return false; }
        string actual = MoveTreeLayout(preview), expected = MoveTreeLayout(expectedTree);
        if (actual == expected) return true;
        movePlanMismatch = "Missing: " + string.Join(", ", expected.Split('\n').Except(actual.Split('\n')).Take(2)) +
            ". Unexpected: " + string.Join(", ", actual.Split('\n').Except(expected.Split('\n')).Take(2));
        return false;
    }

    string ResolvedMoveDestination(MoveRecord record)
    {
        if (string.IsNullOrEmpty(record.to)) return record.to;
        var destinations = new HashSet<string>();
        void Walk(ExpressionNode node)
        {
            // Final folder selectors are part of the destination's identity.
            // Dropping one turns a blank root submenu into Fury's delete path,
            // or combines separate unnamed folders during a later named move.
            if (node.appliedMoveGenerators.Contains(record.comp)) destinations.Add(PathOf(node, node.IsFolderLike));
            foreach (var child in node.children) Walk(child);
        }
        if (root != null) Walk(root);
        return destinations.Count == 1 ? destinations.First() : record.to;
    }

}
