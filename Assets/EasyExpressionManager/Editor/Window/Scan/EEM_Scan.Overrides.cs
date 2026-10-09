// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — MoveMenuItem / OverrideMenuIcon application
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

    void ApplyManagerOverrides(ExpressionNode treeRoot)
    {
        resolvedMoveRecords.Clear();
        unresolvedMoveRecords.Clear();
        resolvedIconRecords.Clear();
        var moves = ReadMoveRecords();
        var icons = new List<(Component comp, string path, Texture2D icon)>();
        foreach (var entry in EnumerateVrcfComponents())
        {
            var so = new SerializedObject(entry.comp);
            if (so.FindProperty("content.fromPath") != null) continue;
            if (TryOverrideIconProps(so, out _, out var icon, out string path))
                icons.Add((entry.comp, EEMPath.FromVrcfControl(path), icon.objectReferenceValue as Texture2D));
        }
        void BetweenSteps()
        {
            foreach (var icon in icons.ToArray())
            {
                if (FindAllNodesAtPath(treeRoot, icon.path).Count > 0) resolvedIconRecords.Add(icon.comp);
                if (ApplyIconOverride(treeRoot, icon.path, icon.icon)) icons.Remove(icon);
            }
        }
        BetweenSteps();
        List<MoveRecord> ordered;
        try { ordered = OrderMovesByDependency(moves, treeRoot); }
        catch (System.InvalidOperationException ex)
        {
            // Keep the editor usable for fixing pre-existing cyclic records.
            // Writes reject the cycle; builds fall back to component order.
            danglingRecordDescriptions.Add(ex.Message + " Preview and builds use component order until the cycle is fixed.");
            ordered = moves;
        }
        foreach (var record in ordered)
        {
            if (ApplyMove(treeRoot, record.from, record.to, record.comp)) resolvedMoveRecords.Add(record.comp);
            else
            {
                unresolvedMoveRecords.Add(record.comp);
                AddUnresolvedRecordWarning(record.comp, "Move '" + EEMPath.ToVrcf(record.from) + "' ? '" + EEMPath.ToVrcf(record.to) + "' (source not found when this record runs)");
            }
            BetweenSteps();
        }
        foreach (var icon in icons)
            if (!resolvedIconRecords.Contains(icon.comp) && FindAllNodesAtPath(treeRoot, icon.path).Count == 0)
                AddUnresolvedRecordWarning(icon.comp, "Icon override for '" + EEMPath.ToVrcf(icon.path) + "' (path does not resolve)");
    }

    /// Record X must run before record Y when either:
    ///   (a) X's destination lands inside Y's source — Y will then carry that
    ///       content along with it, or
    ///   (b) X's source is inside Y's source — X has to lift its item out while
    ///       Y's folder is still sitting at its original path.
    ///
    /// (b) is what op 3 in the v2.1.0 log tripped over: flattening rewrote
    /// 'FX/VIrtual Menu/Emotes/MOTION → FX/MOTION' back to 'Emotes/MOTION →
    /// FX/MOTION', but the record moving Emotes itself was applied first, so by
    /// the time the MOTION record ran its source path no longer existed and the
    /// whole move silently evaporated.
    List<MoveRecord> OrderMovesByDependency(List<MoveRecord> moves, ExpressionNode sourceTree = null)
    {
        sourceTree = sourceTree ?? BuildMoveSourceTree();
        var nativePaths = new HashSet<string>();
        void Collect(ExpressionNode n)
        {
            if (n == null) return;
            if (n != sourceTree) nativePaths.Add(PathOf(n));
            foreach (var child in n.children) Collect(child);
        }
        Collect(sourceTree);
        bool hasSequentialSource = moves.Any(r => !nativePaths.Contains(r.from) ||
            r.from.Contains("__EEM_move_") || r.to.Contains("__EEM_move_"));
        if (moves.Count > 1)
        {
            var last = moves[moves.Count - 1];
            hasSequentialSource |= moves.Take(moves.Count - 1).Any(prior => !string.IsNullOrEmpty(prior.to) &&
                EEMPath.IsAtOrUnder(last.to, prior.from) &&
                (EEMPath.IsAtOrUnder(last.from, prior.to) || EEMPath.IsUnder(prior.to, last.from)));
        }
        bool reciprocalCycle = moves.Any(x => !string.IsNullOrEmpty(x.to) && !EEMPath.PathEq(x.from, x.to) &&
            nativePaths.Contains(x.from) && moves.Any(y => y.comp != x.comp && nativePaths.Contains(y.from) &&
                EEMPath.PathEq(x.to, y.from) && EEMPath.PathEq(y.to, x.from)));
        if (hasSequentialSource && !reciprocalCycle && sourceTree != null)
        {
            // These sources describe an intermediate state. Preserve their
            // established component order when every source actually resolves.
            // A plan check separately compares the final menu with the user's
            // requested result before any edited record is saved.
            var preview = CopyMoveTree(sourceTree);
            bool resolves = true;
            foreach (var r in moves)
                if (!ApplyMove(preview, r.from, r.to, r.comp)) { resolves = false; break; }
            if (resolves) return new List<MoveRecord>(moves);
        }
        var remaining = new List<MoveRecord>(moves);
        var ordered   = new List<MoveRecord>(moves.Count);
        int guard = remaining.Count * remaining.Count + 8;

        bool MustRunFirst(MoveRecord x, MoveRecord y)
        {
            if (ReferenceEquals(x.comp, y.comp)) return false;
            if (string.IsNullOrEmpty(y.from)) return false;
            if (EEMPath.IsAtOrUnder(x.to, y.from)) return true;      // (a)
            if (EEMPath.IsUnder(x.from, y.from)) return true;        // (b)
            return false;
        }

        while (remaining.Count > 0 && guard-- > 0)
        {
            int idx = remaining.FindIndex(y => !remaining.Any(x => MustRunFirst(x, y)));
            if (idx < 0) throw new System.InvalidOperationException("Menu move records form a dependency cycle.");
            ordered.Add(remaining[idx]);
            remaining.RemoveAt(idx);
        }
        ordered.AddRange(remaining);
        return ordered;
    }

    bool ApplyMove(ExpressionNode treeRoot, string fromInternal, string toInternal, Component generator)
    {
        var matches = FindAllNodesAtPath(treeRoot, fromInternal);
        if (matches.Count == 0) return false;
        var toParts = EEMPath.SplitPath(toInternal);
        foreach (var node in matches)
        {
            node.hasManagedMove = true;
            if (generator != null && !node.appliedMoveGenerators.Contains(generator)) node.appliedMoveGenerators.Add(generator);
            node.parent?.children.Remove(node);
            if (toParts.Length == 0) continue;
            node.rawName = toParts[toParts.Length - 1];
            Attach(treeRoot, toParts, node, generator);
        }
        return true;
    }

    bool ApplyIconOverride(ExpressionNode treeRoot, string internalPath, Texture2D icon)
    {
        var matches = FindAllNodesAtPath(treeRoot, internalPath);
        bool changed = false;
        foreach (var node in matches)
        {
            changed |= node.icon != icon;
            node.icon = icon;
            node.hasManagedIcon = true;
        }
        return changed;
    }

    bool TryOverrideIconProps(SerializedObject so, out SerializedProperty pathProp,
                               out SerializedProperty iconProp, out string currentPath)
    {
        pathProp = null; iconProp = null; currentPath = null;
        if (cachedIconMenuPathProp != null) pathProp = so.FindProperty(cachedIconMenuPathProp);
        if (cachedIconTextureProp  != null) iconProp = so.FindProperty(cachedIconTextureProp);

        if (pathProp == null)
            foreach (var c in new[] { "content.path", "content.menuPath", "content.itemPath", "content.menuItemPath" })
            {
                var p = so.FindProperty(c);
                if (p?.propertyType == SerializedPropertyType.String) { pathProp = p; break; }
            }

        if (iconProp == null)
            foreach (var c in new[] { "content.icon.objRef", "content.icon", "content.menuIcon.objRef", "content.menuIcon" })
            {
                var p = so.FindProperty(c);
                if (p?.propertyType == SerializedPropertyType.ObjectReference) { iconProp = p; break; }
            }

        if (pathProp == null || iconProp == null)
        {
            var it = so.GetIterator(); bool enter = true;
            while (it != null && it.NextVisible(enter))
            {
                enter = false;
                string lp = it.propertyPath.ToLowerInvariant();
                if (!lp.StartsWith("content.")) continue;
                if (pathProp == null && it.propertyType == SerializedPropertyType.String && lp.Contains("path")) pathProp = it.Copy();
                if (iconProp == null && it.propertyType == SerializedPropertyType.ObjectReference && lp.Contains("icon")) iconProp = it.Copy();
                if (pathProp != null && iconProp != null) break;
            }
        }
        if (pathProp == null || iconProp == null) return false;
        currentPath = pathProp.stringValue;
        return true;
    }

    void DiscoverOverrideIconProps(SerializedObject so, System.Type fType)
    {
        try
        {
            foreach (var field in fType.GetFields(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                string fn = field.Name.ToLowerInvariant();
                if (cachedIconMenuPathProp == null && field.FieldType == typeof(string) &&
                    (fn.Contains("path") || fn.Contains("menu")))
                    cachedIconMenuPathProp = "content." + field.Name;
                if (cachedIconTextureProp == null &&
                    (fn.Contains("icon") || fn.Contains("texture") || fn.Contains("sprite")))
                {
                    var p = so.FindProperty("content." + field.Name + ".objRef");
                    cachedIconTextureProp = p?.propertyType == SerializedPropertyType.ObjectReference
                        ? "content." + field.Name + ".objRef"
                        : "content." + field.Name;
                }
            }
        }
        catch { }
    }

}
