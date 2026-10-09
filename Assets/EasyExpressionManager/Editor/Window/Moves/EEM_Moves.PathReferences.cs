// Easy Expression Manager: rewrite menu references and remove records for deleted paths.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    // ═══════════════════════════════════════════════════════════════════════════
    // UNIFIED PATH-REFERENCE REWRITING
    //
    // v2.1.0 rewrote MoveMenuItem records on some rename paths and not others,
    // never touched OverrideMenuIcon paths at all, and never touched Full
    // Controller prefixes — so renaming an FC prefix folder was a silent no-op
    // that the next Scan() reverted.
    // ═══════════════════════════════════════════════════════════════════════════

    void RewriteAllPathReferences(string oldPath, string newPath, ExpressionNode subtreeRoot)
    {
        oldPath = EEMPath.Norm(oldPath);
        newPath = EEMPath.Norm(newPath);
        if (EEMPath.PathEq(oldPath, newPath)) return;

        RewriteMoveRecordsForPathChange(oldPath, newPath);
        RewriteIconOverridesForPathChange(oldPath, newPath);
        RewriteFCPrefixesForPathChange(oldPath, newPath);
        RenameFuryPathsUnder(subtreeRoot, oldPath, newPath);
        RenameVirtualFolderPath(oldPath, newPath);
    }

    /// When a node is currently positioned by an ACTIVE MoveMenuItem record
    /// (node.hasManagedMove), that record's `from` field holds the item's
    /// NATIVE identity — whatever it was called before the move — which lives
    /// in a completely different "space" than the node's current RESOLVED
    /// path. Renaming the node changes its native identity directly (the
    /// control's own name field, the toggle's content.name, etc.); this
    /// updates the governing record's `from` to track that, replacing only
    /// the leaf segment so the record's own folder structure (representing
    /// where the item natively sat before any move) is left alone — a rename
    /// only ever changes a leaf name, never which native folder it came from.
    /// Returns false if no record governs this exact path (nothing to do) or
    /// the leaf was already correct.
    bool UpdateGoverningMoveRecordNativeLeaf(string currentResolvedPath, string newLeaf)
    {
        var go = ManagerObjectOrNull();
        var vrcfType = ConcreteVrcfType(); if (vrcfType == null) return false;
        var records = ReadMoveRecords();

        int idx = records.FindIndex(r => EEMPath.PathEq(r.to, currentResolvedPath));
        if (idx < 0) return false;

        string newFrom = EEMPath.ReplaceLastSegment(records[idx].from, newLeaf);
        if (EEMPath.PathEq(newFrom, records[idx].from)) return false;

        records[idx] = new MoveRecord { comp = records[idx].comp, from = newFrom, to = records[idx].to };
        FlattenInPlace(records);
        FlushRecords(records);
        if (go) EditorUtility.SetDirty(go);
        return true;
    }

    bool RewriteMoveRecordsForPathChange(string oldPath, string newPath)
    {
        var go = ManagerObjectOrNull();
        var vrcfType = ConcreteVrcfType(); if (vrcfType == null) return false;
        var records = ReadMoveRecords(); if (records.Count == 0) return false;

        bool anyChanged = false;
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            string newFr = EEMPath.RewriteSeg(r.from, oldPath, newPath);
            string newTo = EEMPath.RewriteSeg(r.to,   oldPath, newPath);
            if (!EEMPath.PathEq(newFr, r.from) || !EEMPath.PathEq(newTo, r.to))
            {
                records[i] = new MoveRecord { comp = r.comp, from = newFr, to = newTo };
                anyChanged = true;
            }
        }
        if (!anyChanged) return false;
        FlattenInPlace(records);
        FlushRecords(records);
        if (go) EditorUtility.SetDirty(go);
        return true;
    }

    bool RewriteIconOverridesForPathChange(string oldPath, string newPath)
    {
        var go = ManagerObjectOrNull();
        var vrcfType = ConcreteVrcfType(); if (vrcfType == null) return false;
        bool any = false;
        foreach (var comp in EnumerateVrcfComponents().Select(e => e.comp))
        {
            if (!comp) continue;
            var so = new SerializedObject(comp); so.Update();
            if (so.FindProperty("content.fromPath") != null) continue;      // that's a move, not an icon
            if (!TryOverrideIconProps(so, out var pp, out _, out string cur)) continue;
            string curInternal = EEMPath.FromVrcfControl(cur);
            string rewritten   = EEMPath.RewriteSeg(curInternal, oldPath, newPath);
            if (EEMPath.PathEq(rewritten, curInternal)) continue;
            // Only carry records targeting a control in the displayed subtree.
            // Missing records have no target to carry; records resolved before
            // an earlier move must retain their intermediate source coordinates.
            var matches = FindAllNodesAtPath(root, curInternal);
            if (matches.Count == 0 || matches.Count > 1 &&
                !matches.All(n => bulkMoveSourceKeys.Contains(n.stableKey))) continue;
            Undo.RecordObject(comp, "Update Override Menu Icon path");
            pp.stringValue = EEMPath.ToVrcf(rewritten);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(comp);
            any = true;
        }
        return any;
    }

    bool RewriteFCPrefixesForPathChange(string oldPath, string newPath)
    {
        if (!avatar) return false;
        bool any = false;
        foreach (var comp in avatar.GetComponentsInChildren<Component>(true))
        {
            if (!comp || comp is Transform) continue;
            string fn = comp.GetType().FullName ?? "";
            if (!fn.Contains("VRCFury") && !fn.Contains("VF.")) continue;

            var so = new SerializedObject(comp); so.Update();
            var menusProp = so.FindProperty("content.menus");
            if (menusProp == null || !menusProp.isArray) continue;

            bool changedHere = false;
            for (int i = 0; i < menusProp.arraySize; i++)
            {
                var prefixProp = menusProp.GetArrayElementAtIndex(i).FindPropertyRelative("prefix");
                if (prefixProp?.propertyType != SerializedPropertyType.String) continue;
                string cur = EEMPath.FromVrcf(prefixProp.stringValue ?? "");
                string rewritten = EEMPath.RewriteSeg(cur, oldPath, newPath);
                if (EEMPath.PathEq(rewritten, cur)) continue;
                if (!changedHere) { Undo.RecordObject(comp, "Update Full Controller prefix"); changedHere = true; }
                prefixProp.stringValue = EEMPath.ToVrcf(rewritten);
            }
            if (changedHere) { so.ApplyModifiedProperties(); EditorUtility.SetDirty(comp); any = true; }
        }
        return any;
    }

    /// Rewrites content.name on every Fury toggle under a node.
    void RenameFuryPathsUnder(ExpressionNode node, string oldPrefix, string newPrefix)
    {
        if (node == null) return;
        oldPrefix = EEMPath.Norm(oldPrefix);
        newPrefix = EEMPath.Norm(newPrefix);

        var all = new List<ExpressionNode> { node };
        all.AddRange(CollectAllDescendants(node));

        foreach (var n in all)
        {
            if (n.sourceObject == null) continue;
            // look first (read-only); only unpack + edit toggles that actually change
            var peek = ReadSO(n.sourceObject)?.FindProperty("content.name");
            if (peek?.propertyType != SerializedPropertyType.String) continue;
            string cur = EEMPath.FromVrcf(peek.stringValue);
            string updated = EEMPath.RewriteSeg(cur, oldPrefix, newPrefix);
            if (EEMPath.PathEq(updated, cur)) continue;
            var so = FreshSO(n.sourceObject);
            var nameProp = so?.FindProperty("content.name");
            if (nameProp?.propertyType != SerializedPropertyType.String) continue;
            if (so.targetObject != null) Undo.RecordObject(so.targetObject, "Rewrite Fury menu path");
            nameProp.stringValue = EEMPath.ToVrcf(updated);
            so.ApplyModifiedProperties();
            n.sourceObject = so;
            if (so.targetObject != null) EditorUtility.SetDirty(so.targetObject);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // RECORD REMOVAL FOR DELETES
    // ═══════════════════════════════════════════════════════════════════════════

    /// Removes records that would re-materialise a path we are deleting, and
    /// records whose source has just been deleted. Without this, deleting a
    /// folder that a record points into brings it straight back on the next scan.
    int RemoveMoveRecordsTouching(string internalPath)
    {
        var go = ManagerObjectOrNull();
        var vrcfType = ConcreteVrcfType(); if (vrcfType == null) return 0;
        int removed = 0;
        foreach (var r in ReadMoveRecords())
        {
            bool targetsInside = EEMPath.IsAtOrUnder(r.to, internalPath);
            bool sourceInside  = EEMPath.IsAtOrUnder(r.from, internalPath);
            if (!targetsInside && !sourceInside) continue;
            if (r.comp) { Undo.DestroyObjectImmediate(r.comp); removed++; }
        }
        if (removed > 0) if (go) EditorUtility.SetDirty(go);
        return removed;
    }

    int RemoveIconOverridesTouching(string internalPath)
    {
        var go = ManagerObjectOrNull();
        var vrcfType = ConcreteVrcfType(); if (vrcfType == null) return 0;
        int removed = 0;
        foreach (var comp in EnumerateVrcfComponents().Select(e => e.comp))
        {
            if (!comp) continue;
            var so = new SerializedObject(comp); so.Update();
            if (so.FindProperty("content.fromPath") != null) continue;
            if (!TryOverrideIconProps(so, out _, out _, out string cur)) continue;
            if (!EEMPath.IsAtOrUnder(EEMPath.FromVrcfControl(cur), internalPath)) continue;
            Undo.DestroyObjectImmediate(comp); removed++;
        }
        return removed;
    }

}
