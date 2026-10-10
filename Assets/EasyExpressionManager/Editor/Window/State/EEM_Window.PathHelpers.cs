// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — tree-bound path helpers & expansion state
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

    /// INTERNAL path of a node, built from RAW names with separators escaped.
    string PathOf(ExpressionNode n, bool includeLeafSelector = false)
    {
        if (n == null || n == root) return "";
        var parts = new List<string>();
        // A cached row may belong to the previous scan's tree. Its parentless
        // root is a UI label, never a real menu-path segment.
        for (var c = n; c != null && c != root && c.parent != null; c = c.parent)
        {
            string name = c.rawName;
            if ((c != n || includeLeafSelector) && c.IsFolderLike && c.parent != null)
            {
                var siblings = MatchVrcControls(c.parent.children, name, true);
                int occurrence = siblings.IndexOf(c);
                // A blank folder path alone is indistinguishable from the
                // root in VRCFury fields. Its explicit zero selector preserves
                // the first blank submenu as a destination/prefix.
                if (occurrence > 0 || string.IsNullOrEmpty(name)) name += ".dup." + System.Math.Max(0, occurrence);
            }
            parts.Insert(0, name);
        }
        return EEMPath.JoinPath(parts);
    }

    ExpressionNode ResolveLiveNode(ExpressionNode node)
    {
        if (node == null) return null;
        if (node == root || allNodesFlatList.Contains(node)) return node;
        if (node.parent == null && string.IsNullOrEmpty(node.stableKey)) return root;
        if (node.sourceControl != null)
        {
            var control = node.sourceControl;
            var owner = node.sourceFullControllerObject?.targetObject;
            if (node.isFromFullController && owner != null &&
                fcControlCloneMap.TryGetValue((owner, node.fcEntryIndex, control), out var copied)) control = copied;
            bool MatchesSource(ExpressionNode candidate)
            {
                if ((candidate.sourceControl == control || candidate.sourceControl == node.sourceControl) && candidate.isFromFullController == node.isFromFullController &&
                    (!node.isFromFullController || candidate.sourceFullControllerObject?.targetObject == owner && candidate.fcEntryIndex == node.fcEntryIndex)) return true;
                return candidate.mergedSources.Any(MatchesSource);
            }
            // Array indexes can shift after an unnamed leaf is removed. A reused
            // stable key must never resolve to the next, unselected control.
            var matches = allNodesFlatList.Where(MatchesSource).ToList();
            if (matches.Count > 0)
                return matches.Count == 1 ? matches[0] : matches.FirstOrDefault(candidate => candidate.stableKey == node.stableKey);
            return ResolveSerializedSource(node, control);
        }
        return allNodesFlatList.FirstOrDefault(n => n.stableKey == node.stableKey);
    }

    ExpressionNode ResolveSerializedSource(ExpressionNode cached, VRCExpressionsMenu.Control wanted)
    {
        var owner = cached.sourceFullControllerObject?.targetObject;
        var original = cached.isFromFullController ? cached.sourceFullControllerMenu : cached.sourceMenu;
        var current = original;
        if (cached.isFromFullController && original && owner != null &&
            fcCloneMap.TryGetValue((owner, cached.fcEntryIndex, original), out var copied)) current = copied;
        if (!current) return null;
        bool countUnchanged = current.controls.Count == cached.sourceControlCount;
        if (!countUnchanged && !bulkMoveSourceKeys.Contains(cached.stableKey)) return null;

        // A deleted row must not bind to an identical surviving peer. Only
        // allow serialized matching when the old source controls were all
        // replaced, as happens when Unity deserializes a menu's control array.
        bool HasSurvivingControl(ExpressionNode previous)
        {
            bool sameOwner = previous.isFromFullController == cached.isFromFullController &&
                (!cached.isFromFullController || previous.sourceFullControllerObject?.targetObject == owner && previous.fcEntryIndex == cached.fcEntryIndex);
            var menu = previous.isFromFullController ? previous.sourceFullControllerMenu : previous.sourceMenu;
            if (sameOwner && (menu == original || menu == current) && previous.sourceControl != null && current.controls.Contains(previous.sourceControl)) return true;
            return previous.mergedSources.Any(HasSurvivingControl);
        }
        if (cached.parent != null && cached.parent.children.Any(HasSurvivingControl)) return null;

        string definition = JsonUtility.ToJson(wanted);
        bool MatchesDefinition(ExpressionNode candidate)
        {
            bool sameOwner = candidate.isFromFullController == cached.isFromFullController &&
                (!cached.isFromFullController || candidate.sourceFullControllerObject?.targetObject == owner && candidate.fcEntryIndex == cached.fcEntryIndex);
            var menu = candidate.isFromFullController ? candidate.sourceFullControllerMenu : candidate.sourceMenu;
            if (sameOwner && (menu == original || menu == current) && candidate.sourceControl != null && JsonUtility.ToJson(candidate.sourceControl) == definition) return true;
            return candidate.mergedSources.Any(MatchesDefinition);
        }
        var matches = allNodesFlatList.Where(MatchesDefinition).ToList();
        if (matches.Count == 1) return matches[0];
        // An unchanged array retains the ordinal used in its stable key.
        // After removals, identical controls require their original references.
        return countUnchanged ? matches.FirstOrDefault(n => n.stableKey == cached.stableKey) : null;
    }

    void RebindSelectionAfterScan(ExpressionNode previousSelected, IEnumerable<ExpressionNode> previousSet, ExpressionNode previousAnchor)
    {
        // Source array indexes can be reused after a deletion. Rebind the
        // control itself so its former selection cannot jump to its neighbour.
        selected = ResolveLiveNode(previousSelected);
        selectedNodes = new HashSet<ExpressionNode>(previousSet.Select(ResolveLiveNode).Where(n => n != null && n != root));
        selectionAnchor = ResolveLiveNode(previousAnchor);
    }

    ExpressionNode ResolveMoveDestination(ExpressionNode cached, string capturedPath)
    {
        if (string.IsNullOrEmpty(capturedPath)) return root;
        var live = ResolveLiveNode(cached);
        if (live != null && live.IsFolderLike) return live;
        if (cached == null) return null;

        // Unity may deserialize controls while an asset is created: the menu
        // asset survives but its managed Control objects have new references.
        // Rebind the destination by its source and serialized definition rather
        // than treating that reference change as a deleted folder.
        if (cached.sourceControl != null)
        {
            string definition = JsonUtility.ToJson(cached.sourceControl);
            var owner = cached.sourceFullControllerObject?.targetObject;
            bool MatchesDestinationSource(ExpressionNode candidate)
            {
                bool sameOwner = candidate.isFromFullController == cached.isFromFullController &&
                    (cached.isFromFullController
                        ? candidate.sourceFullControllerObject?.targetObject == owner && candidate.fcEntryIndex == cached.fcEntryIndex
                        : candidate.sourceMenu == cached.sourceMenu);
                if (sameOwner && candidate.sourceControl != null && JsonUtility.ToJson(candidate.sourceControl) == definition) return true;
                return candidate.mergedSources.Any(MatchesDestinationSource);
            }
            var matches = allNodesFlatList.Where(n => n.IsFolderLike && MatchesDestinationSource(n)).ToList();
            if (matches.Count == 1) return matches[0];
            return matches.FirstOrDefault(n => n.stableKey == cached.stableKey && EEMPath.PathEq(PathOf(n, true), capturedPath));
        }

        // A generated or virtual folder can change which contributor supplies
        // its stable key. Its captured path still identifies the destination.
        return NodeByFolderPath(root, capturedPath);
    }

    string DisplayOf(ExpressionNode n)
    {
        if (n == null) return "";
        if (n == root) return "Expression Menu";
        int idx = n.parent != null ? n.parent.children.IndexOf(n) : -1;
        return EEMPath.Display(n.rawName, n.type, idx);
    }

    /// Human-readable path for logs and dialogs (rich text stripped).
    string DisplayPathOf(ExpressionNode n)
    {
        if (n == null || n == root) return "";
        var parts = new List<string>();
        for (var c = n; c != null && c != root; c = c.parent) parts.Insert(0, DisplayOf(c));
        return string.Join("/", parts);
    }

    /// Uses VRCFury's folder traversal, including .dup.N ancestor selectors.
    /// Returns a node only when exactly one final control matches. Callers that
    /// intentionally affect every same-named final control use FindAllNodesAtPath.
    ExpressionNode NodeByPath(ExpressionNode treeRoot, string internalPath)
    {
        var matches = FindAllNodesAtPath(treeRoot, internalPath);
        return matches.Count == 1 ? matches[0] : null;
    }

    /// Match the installed VRCFury MenuManager: exact names first, then its
    /// normalized and rich-text fallback. Folder filtering is used for ancestors.
    static List<ExpressionNode> MatchVrcControls(IEnumerable<ExpressionNode> children, string name, bool foldersOnly = false)
    {
        var candidates = children.Where(n => !foldersOnly || n.IsFolderLike).ToList();
        var exact = candidates.Where(n => n.rawName == name).ToList();
        if (exact.Count > 0) return exact;
        string key = EEMPath.MatchKey(name);
        if (key != "")
        {
            var normalized = candidates.Where(n => EEMPath.MatchKey(n.rawName) == key).ToList();
            if (normalized.Count > 0) return normalized;
            return candidates.Where(n => Regex.Replace(n.rawName ?? "", "<.*?>", "`")
                .Split('`').Any(slug => EEMPath.MatchKey(slug) == key)).ToList();
        }
        return new List<ExpressionNode>();
    }

    ExpressionNode DestinationFolder(ExpressionNode parent, string part, bool create)
    {
        string name = part;
        int offset = 0, dup = name.IndexOf(".dup.", System.StringComparison.Ordinal);
        if (dup >= 0)
        {
            if (!int.TryParse(name.Substring(dup + 5), out offset) || offset < 0)
                throw new System.InvalidOperationException("Invalid duplicate submenu selector: " + part);
            name = name.Substring(0, dup);
        }
        var folders = MatchVrcControls(parent.children, name, true);
        if (offset < folders.Count) return folders[offset];
        if (!create) return null;
        var folder = new ExpressionNode { rawName = name, type = NodeType.Folder, parent = parent };
        parent.children.Add(folder);
        return folder;
    }

    ExpressionNode NodeByFolderPath(ExpressionNode tree, string path)
    {
        var current = tree;
        foreach (var part in EEMPath.SplitPath(path))
        {
            current = DestinationFolder(current, part, false);
            if (current == null) return null;
        }
        return current;
    }

    bool HasRealFolderSource(ExpressionNode node) => node != null &&
        (node.sourceControl != null || node.isFCPrefixFolder || node.isFromFullController ||
         node.mergedSources.Any(HasRealFolderSource));

    List<ExpressionNode> FindAllNodesAtPath(ExpressionNode treeRoot, string internalPath)
    {
        if (treeRoot == null || string.IsNullOrEmpty(internalPath)) return new List<ExpressionNode>();
        var parts = EEMPath.SplitPath(internalPath);
        var parent = treeRoot;
        for (int i = 0; i < parts.Length; i++)
        {
            if (i == parts.Length - 1) return MatchVrcControls(parent.children, parts[i]);
            string name = parts[i];
            int offset = 0, dup = name.IndexOf(".dup.", System.StringComparison.Ordinal);
            if (dup >= 0)
            {
                if (!int.TryParse(name.Substring(dup + 5), out offset) || offset < 0) return new List<ExpressionNode>();
                name = name.Substring(0, dup);
            }
            var folders = MatchVrcControls(parent.children, name, true);
            if (offset >= folders.Count) return new List<ExpressionNode>();
            parent = folders[offset];
        }
        return new List<ExpressionNode>();
    }

    /// Same lookup, ignoring rich-text markup and case. Used by structure import
    /// against a *different* avatar, never for internal bookkeeping.
    ExpressionNode NodeByDisplayPath(ExpressionNode treeRoot, string displayPath)
    {
        if (string.IsNullOrEmpty(displayPath) || treeRoot == null) return null;
        var cur = treeRoot;
        foreach (var part in displayPath.Split('/'))
        {
            if (string.IsNullOrEmpty(part)) continue;
            var matches = cur.children.Where(x =>
                string.Equals(DisplayOf(x), part, System.StringComparison.OrdinalIgnoreCase)).ToList();
            // A display path is only a legacy, human-readable key. Never pick
            // the first duplicate silently; structured structure exports use
            // raw names and ordinals instead.
            if (matches.Count != 1) return null;
            cur = matches[0];
        }
        return cur == treeRoot ? null : cur;
    }

    /// The ordinal used by a structured structure export for a folder. This
    /// follows VRCFury's exact-first/normalized fallback matching, but keeps
    /// the ordinal as data instead of encoding it into a path segment. That
    /// preserves literal names containing ".dup." for native identity while
    /// retaining the installed VRCFury semantics when a path is eventually
    /// written to VRCFury.
    int StructureFolderOrdinalOf(ExpressionNode node)
    {
        if (node == null || node.parent == null || !node.IsFolderLike) return 0;
        var matches = MatchVrcControls(node.parent.children, node.rawName, true);
        int index = matches.IndexOf(node);
        return index < 0 ? 0 : index;
    }

    /// Resolves a structured raw-name path. Ancestors use explicit folder
    /// ordinals; the final segment intentionally returns every same-name
    /// control/folder. This is deliberately different from VRCFury's ancestor
    /// `.dup.N` parsing: VRCFury treats the final control name literally.
    List<ExpressionNode> FindStructurePathMatches(ExpressionNode treeRoot,
                                                   IList<string> rawSegments,
                                                   IList<int> folderOrdinals)
    {
        if (treeRoot == null || rawSegments == null || rawSegments.Count == 0)
            return new List<ExpressionNode>();

        var parent = treeRoot;
        for (int i = 0; i < rawSegments.Count - 1; i++)
        {
            var folders = MatchVrcControls(parent.children, rawSegments[i], true);
            int ordinal = folderOrdinals != null && i < folderOrdinals.Count
                ? folderOrdinals[i] : 0;
            if (ordinal < 0 || ordinal >= folders.Count) return new List<ExpressionNode>();
            parent = folders[ordinal];
        }
        return MatchVrcControls(parent.children, rawSegments[rawSegments.Count - 1]);
    }

    /// Resolves a structured folder path for a destination. A missing/invalid
    /// ordinal returns null rather than silently selecting a different folder.
    ExpressionNode NodeByStructureFolderPath(ExpressionNode treeRoot,
                                              IList<string> rawSegments,
                                              IList<int> folderOrdinals)
    {
        if (treeRoot == null) return null;
        if (rawSegments == null || rawSegments.Count == 0) return treeRoot;

        var current = treeRoot;
        for (int i = 0; i < rawSegments.Count; i++)
        {
            var folders = MatchVrcControls(current.children, rawSegments[i], true);
            int ordinal = folderOrdinals != null && i < folderOrdinals.Count
                ? folderOrdinals[i] : 0;
            if (ordinal < 0 || ordinal >= folders.Count) return null;
            current = folders[ordinal];
        }
        return current;
    }

    List<string> CollectAllPaths(ExpressionNode node, bool foldersOnly = false)
    {
        var result = new List<string>();
        void Walk(ExpressionNode n)
        {
            foreach (var child in n.children)
            {
                if (!foldersOnly || child.IsFolderLike) result.Add(PathOf(child, foldersOnly && child.IsFolderLike));
                Walk(child);
            }
        }
        if (node != null) Walk(node);
        return result;
    }

    List<ExpressionNode> CollectAllDescendants(ExpressionNode node)
    {
        var result = new List<ExpressionNode>();
        void Walk(ExpressionNode n)
        {
            foreach (var c in n.children) { result.Add(c); Walk(c); }
        }
        if (node != null) Walk(node);
        return result;
    }

    bool IsAncestorOf(ExpressionNode maybeAncestor, ExpressionNode node)
    {
        for (var c = node?.parent; c != null; c = c.parent)
            if (c == maybeAncestor) return true;
        return false;
    }


    void SnapshotExpansionStates(ExpressionNode node)
    {
        if (node == null) return;
        expansionStates[node.stableKey ?? ""] = node.isExpanded;
        foreach (var child in node.children) SnapshotExpansionStates(child);
    }

    void RestoreExpand(ExpressionNode n)
    {
        n.isExpanded = expansionStates.TryGetValue(n.stableKey ?? "", out bool s) && s;
    }

    void SetAllExpanded(ExpressionNode node, bool value)
    {
        if (node == null) return;
        node.isExpanded = value;
        expansionStates[node.stableKey ?? ""] = value;
        foreach (var c in node.children) SetAllExpanded(c, value);
    }

    void ExitSelectionMode()
    {
        selectionMode = false;
        selectedNodes.Clear();
        selectionAnchor = null;
        bulkIconPending = null;
        Repaint();
    }

}
