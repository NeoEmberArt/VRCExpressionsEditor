// Easy Expression Manager: read, flatten and write VRCFury move records.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    struct MoveRecord { public Component comp; public string from; public string to; }

    List<MoveRecord> ReadMoveRecords(GameObject managerGO, System.Type vrcfType)
    {
        var result = new List<MoveRecord>();
        if (!managerGO || vrcfType == null) return result;
        foreach (var comp in managerGO.GetComponents(vrcfType))
        {
            if (!comp) continue;
            var so = new SerializedObject(comp); so.Update();
            var fp = so.FindProperty("content.fromPath");
            var tp = so.FindProperty("content.toPath");
            if (fp?.propertyType != SerializedPropertyType.String ||
                tp?.propertyType != SerializedPropertyType.String) continue;
            result.Add(new MoveRecord
            {
                comp = comp,
                from = EEMPath.FromVrcfControl(fp.stringValue),
                to   = EEMPath.FromVrcf(tp.stringValue)
            });
        }
        return result;
    }

    List<MoveRecord> ReadMoveRecords()
    {
        var records = new List<MoveRecord>();
        foreach (var entry in EnumerateVrcfComponents())
        {
            var so = new SerializedObject(entry.comp);
            var from = so.FindProperty("content.fromPath");
            var to = so.FindProperty("content.toPath");
            if (from?.propertyType == SerializedPropertyType.String && to?.propertyType == SerializedPropertyType.String)
                records.Add(new MoveRecord { comp = entry.comp, from = EEMPath.FromVrcfControl(from.stringValue), to = EEMPath.FromVrcf(to.stringValue) });
        }
        return records;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FLATTEN / FLUSH
    // ═══════════════════════════════════════════════════════════════════════════

    void FlattenInPlace(List<MoveRecord> records, bool quiet = false)
    {
        // Staging records describe real intermediate states. Collapsing those
        // states can let an earlier parent move carry the returned folder away.
        if (records.Any(r => r.from.Contains("__EEM_move_") || r.to.Contains("__EEM_move_"))) return;
        var nativeSources = new HashSet<string>();
        void CollectSources(ExpressionNode n)
        {
            if (n == null) return;
            if (n.nativePath != null) nativeSources.Add(n.nativePath);
            foreach (var merged in n.mergedSources) CollectSources(merged);
            foreach (var child in n.children) CollectSources(child);
        }
        CollectSources(root);
        bool HasStableNativeParent(string path)
        {
            for (string parent = EEMPath.ParentPath(path); !string.IsNullOrEmpty(parent); parent = EEMPath.ParentPath(parent))
                if (nativeSources.Contains(parent) && !records.Any(r => !EEMPath.PathEq(r.from, r.to) && EEMPath.IsAtOrUnder(parent, r.from))) return true;
            return false;
        }
        bool changed = true; int guard = 128;
        while (changed && guard-- > 0)
        {
            changed = false;
            for (int a = 0; a < records.Count && !changed; a++)
            for (int b = 0; b < records.Count && !changed; b++)
            {
                if (a == b) continue;
                var ra = records[a]; var rb = records[b];
                // A record with a blank toPath deletes its item at build time; it
                // doesn't place anything anywhere, so nothing can sit "under" it.
                // (IsUnder(x, "") is true for every path — without this skip a
                // single delete record rewrote every other record on the avatar.)
                if (string.IsNullOrEmpty(ra.to) || EEMPath.PathEq(ra.from, ra.to)) continue;
                if (!nativeSources.Contains(rb.from) && EEMPath.PathEq(ra.to, rb.from))
                {
                    records[b] = new MoveRecord { comp = rb.comp, from = ra.from, to = rb.to };
                    records[a] = new MoveRecord { comp = ra.comp, from = ra.from, to = ra.from };
                    changed = true;
                }
                else if (!nativeSources.Contains(rb.from) && EEMPath.IsUnder(rb.from, ra.to))
                {
                    records[b] = new MoveRecord { comp = rb.comp, from = EEMPath.RewriteSeg(rb.from, ra.to, ra.from), to = rb.to };
                    changed = true;
                }
                else if (!HasStableNativeParent(rb.to) && EEMPath.IsUnder(rb.to, ra.to))
                {
                    records[b] = new MoveRecord { comp = rb.comp, from = rb.from, to = EEMPath.RewriteSeg(rb.to, ra.to, ra.from) };
                    changed = true;
                }
            }
        }
        // v2.1.0 gave up here in silence, leaving half-rewritten records behind.
        if (guard <= 0)
        {
            string dump = string.Join(" | ", records.Select(r => $"'{EEMPath.ToVrcf(r.from)}' -> '{EEMPath.ToVrcf(r.to)}'"));
            if (!quiet)
            {
                Debug.LogError("[EASY] MoveMenuItem flattening did not converge — the records may reference each " +
                               $"other in a loop. Open Records ⚠ in the toolbar to inspect them.\nRecords at the time " +
                               $"of failure ({records.Count}): {dump}");
                NoteConsequence($"WARNING: flattening did not converge ({records.Count} records) — {dump}");
            }
            throw new System.InvalidOperationException("Menu moves form a loop; no changes were applied.");
        }
    }

    void FlushRecords(List<MoveRecord> records)
    {
        for (int i = records.Count - 1; i >= 0; i--)
        {
            var r = records[i];
            // Repeated sources can be valid when an intermediate move refills
            // that path. Cleanup reviews records that actually failed to apply.
            bool redundant = string.IsNullOrEmpty(r.from) || EEMPath.PathEq(r.from, r.to);
            if (redundant)
            {
                if (r.comp) Undo.DestroyObjectImmediate(r.comp);
                continue;
            }
            WriteRecordFields(r);
        }
        ReorderManagerMoveComponents();
    }

    void ReorderManagerMoveComponents()
    {
        var go = ManagerObjectOrNull();
        var type = ConcreteVrcfType();
        if (!go || type == null) return;
        var sorted = OrderMovesByDependency(ReadMoveRecords(go, type), BuildMoveSourceTree()).Select(r => r.comp).ToList();
        foreach (var component in sorted.AsEnumerable().Reverse())
        {
            Undo.RegisterCompleteObjectUndo(go, "Order menu moves");
            while (UnityEditorInternal.ComponentUtility.MoveComponentUp(component)) { }
        }
    }

    void WriteRecordFields(MoveRecord r)
    {
        if (!r.comp) return;
        var so = new SerializedObject(r.comp); so.Update();
        var fp = so.FindProperty("content.fromPath");
        var tp = so.FindProperty("content.toPath");
        if (fp == null || tp == null) return;
        string fromV = EEMPath.ToVrcf(r.from);
        string toV   = EEMPath.ToVrcf(r.to);
        if (fp.stringValue == fromV && tp.stringValue == toV) return;
        Undo.RecordObject(r.comp, "Update Move Menu Item");
        fp.stringValue = fromV;
        tp.stringValue = toV;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(r.comp);
    }

    /// Only ever called from an explicit user action now, never from Scan().
    void FlattenAllMoveChains()
    {
        var go = ManagerObjectOrNull(); if (go == null) return;
        var vrcfType = ConcreteVrcfType(); if (vrcfType == null) return;
        var records = ReadMoveRecords(); if (records.Count < 2) return;
        var before = records.Select(r => (r.from, r.to)).ToList();
        FlattenInPlace(records);
        var after = records.Select(r => (r.from, r.to)).ToList();
        if (!before.SequenceEqual(after)) { FlushRecords(records); if (go) EditorUtility.SetDirty(go); }
    }

}
