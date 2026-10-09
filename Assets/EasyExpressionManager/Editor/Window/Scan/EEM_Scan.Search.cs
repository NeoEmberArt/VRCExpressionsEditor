// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — tree search
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

    void UpdateSearchCache()
    {
        string q = (searchFilter ?? "").Trim().ToLowerInvariant();
        if (cachedSearchFilter == q) return;
        cachedSearchFilter = q;
        searchVisibleNodes.Clear();
        if (root == null || string.IsNullOrEmpty(q)) return;
        MarkSearch(root, q, "");
    }

    bool MarkSearch(ExpressionNode node, string q, string parentPath)
    {
        if (node == null) return false;
        string name2 = DisplayOf(node);
        string path  = string.IsNullOrEmpty(parentPath) ? name2 : parentPath + "/" + name2;
        bool self    = node != root &&
                       (name2.ToLowerInvariant().Contains(q) || path.ToLowerInvariant().Contains(q));
        if (self) { AddSearchSubtree(node); return true; }
        bool childMatch = false;
        foreach (var child in node.children) childMatch |= MarkSearch(child, q, node == root ? "" : path);
        if (childMatch && node != root) searchVisibleNodes.Add(node);
        return childMatch;
    }

    void AddSearchSubtree(ExpressionNode node)
    {
        if (node == null) return;
        searchVisibleNodes.Add(node);
        foreach (var c in node.children) AddSearchSubtree(c);
    }
}
