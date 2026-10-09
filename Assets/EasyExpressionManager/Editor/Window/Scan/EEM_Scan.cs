// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — tree scanning (read-only)
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
    void Scan()
    {
        externalChangesPending     = false;
        rescanQueued               = false;
        visitedMenus.Clear();
        fcCloneMap.Clear();
        fcControlCloneMap.Clear();
        cachedSearchFilter         = null;
        searchVisibleNodes.Clear();
        cachedVrcfCompType         = null;
        featureTypeCache.Clear();
        cachedIconMenuPathProp     = null;
        cachedIconTextureProp      = null;
        danglingRecordDescriptions.Clear();
        overCapacityMenus.Clear();
        iconAvatarTransforms = null;
        iconTargetsNode = null;

        var previouslySelected = selected;
        var previouslySelectedSet = selectedNodes.ToList();
        var previousAnchor = selectionAnchor;
        var previousInline = inlineEditingNode;
        var previousDrag = draggedNode;
        var previousDragCandidate = dragCandidate;

        root = new ExpressionNode { rawName = "Expression Menu", type = NodeType.Folder, isExpanded = true };
        root.stableKey = "";
        if (!avatar) { allNodesFlatList.Clear(); return; }

        var desc = avatar.GetComponent<VRCAvatarDescriptor>();
        if (desc?.expressionsMenu != null)
            BuildVRCMenu(root, desc.expressionsMenu, new HashSet<VRCExpressionsMenu>());

        ScanVRCFury(avatar.transform, root);
        InjectVirtualFolders(root);
        CaptureNativePaths(root);
        ApplyManagerOverrides(root);

        AssignStableKeys(root, "");
        RestoreExpandRecursive(root);

        allNodesFlatList.Clear();
        BuildFullFlatList(root, allNodesFlatList);

        AnalyseTree(root);

        // re-bind selection across the rescan
        RebindSelectionAfterScan(previouslySelected, previouslySelectedSet, previousAnchor);
        inlineEditingNode = ResolveLiveNode(previousInline);
        draggedNode = ResolveLiveNode(previousDrag);
        dragCandidate = ResolveLiveNode(previousDragCandidate);

        Repaint();
    }

    void BuildFullFlatList(ExpressionNode node, List<ExpressionNode> result)
    {
        if (node == null) return;
        if (node != root) result.Add(node);
        foreach (var c in node.children) BuildFullFlatList(c, result);
    }

    void CaptureNativePaths(ExpressionNode node)
    {
        if (node == null) return;
        node.nativePath = PathOf(node);
        foreach (var child in node.children) CaptureNativePaths(child);
    }


    void AssignStableKeys(ExpressionNode node, string parentKey)
    {
        node.stableKey = string.IsNullOrEmpty(node.sourceKey) ? parentKey : node.sourceKey;
        var seen = new Dictionary<string, int>(System.StringComparer.Ordinal);
        foreach (var child in node.children)
        {
            string raw = child.rawName ?? "";
            seen.TryGetValue(raw, out int n);
            seen[raw] = n + 1;
            string key = (string.IsNullOrEmpty(parentKey) ? "" : parentKey + "/") +
                         EEMPath.EscapeSeg(raw) + (n > 0 ? "#" + n : "");
            AssignStableKeys(child, key);
        }
    }

    void RestoreExpandRecursive(ExpressionNode node)
    {
        if (node == null) return;
        if (node != root) RestoreExpand(node);
        foreach (var c in node.children) RestoreExpandRecursive(c);
    }


    void AnalyseTree(ExpressionNode node)
    {
        if (node == null) return;

        if (node.IsFolderLike || node == root)
        {
            int count = node.children.Count;
            if (count > MENU_CAPACITY)
                overCapacityMenus.Add((node == root ? "Expression Menu" : DisplayPathOf(node)) + $"  ({count}/{MENU_CAPACITY})");
        }

        node.nameContainsSeparator = EEMPath.SegmentHasSeparator(node.rawName);

        // Same-name siblings, matched the way VRCFury matches menu names
        // (case-insensitive, rich text ignored).
        if (node.children.Count > 1)
        {
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);
            foreach (var c in node.children)
            {
                string k = EEMPath.MatchKey(c.rawName);
                counts.TryGetValue(k, out int n); counts[k] = n + 1;
            }
            foreach (var c in node.children) c.sameNameCount = counts[EEMPath.MatchKey(c.rawName)];
        }
        else foreach (var c in node.children) c.sameNameCount = 1;

        foreach (var c in node.children) AnalyseTree(c);
    }

    int ChildCountOf(ExpressionNode n) => n?.children.Count ?? 0;

    bool IsOverCapacity(ExpressionNode n) =>
        n != null && (n.IsFolderLike || n == root) && n.children.Count > MENU_CAPACITY;

}
