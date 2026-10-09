// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — explicit Move Menu Item cleanup
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
    string MenuRecordWarningKey(Component comp)
    {
        var so = new SerializedObject(comp);
        string feature = so.FindProperty("content")?.managedReferenceFullTypename ?? "";
        string owner = RelativeHierarchyPath(comp.transform);
        var from = so.FindProperty("content.fromPath");
        if (from != null)
            return owner + "|" + feature + "|move|" + from.stringValue + "|" + so.FindProperty("content.toPath")?.stringValue;
        if (!TryOverrideIconProps(so, out _, out var icon, out var path)) return "";
        return owner + "|" + feature + "|icon|" + path + "|" + (icon?.objectReferenceValue ? StableId(icon.objectReferenceValue) : "");
    }

    bool IsKeptMenuRecord(Component comp)
    {
        if (!comp) return false;
        var state = AvatarState();
        string key = MenuRecordWarningKey(comp);
        if (state.keptMenuRecordKeys.Contains(key)) return true;
        var configured = MiniJson.Dict(LoadDeveloperSettings(), "KeptMenuRecords");
        return MiniJson.List(configured, state.avatarKey).OfType<string>().Contains(key);
    }

    void KeepMenuRecords(IEnumerable<Component> records)
    {
        var state = AvatarState();
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Keep menu records");
        Undo.RecordObject(state, "Keep menu records");
        foreach (var comp in records.Where(c => c))
        {
            string key = MenuRecordWarningKey(comp);
            if (key != "" && !state.keptMenuRecordKeys.Contains(key)) state.keptMenuRecordKeys.Add(key);
        }
        EditorUtility.SetDirty(state);
        AssetDatabase.SaveAssetIfDirty(state);
        QueueRescan();
        FlushRescan();
    }

    void AddUnresolvedRecordWarning(Component comp, string description)
    {
        if (!IsKeptMenuRecord(comp)) danglingRecordDescriptions.Add(description);
    }

    List<Component> FindUnresolvedMenuRecords()
    {
        var result = ReadMoveRecords().Where(r => string.IsNullOrEmpty(r.from) || EEMPath.PathEq(r.from, r.to) ||
            (unresolvedMoveRecords.Contains(r.comp) && !resolvedMoveRecords.Contains(r.comp))).Select(r => r.comp).ToList();
        foreach (var entry in EnumerateVrcfComponents())
        {
            var so = new SerializedObject(entry.comp);
            string feature = so.FindProperty("content")?.managedReferenceFullTypename ?? "";
            if (!feature.Contains("SetIcon") && !feature.Contains("OverrideMenuIcon") &&
                !feature.Contains("OverrideMenuItemIcon") && !feature.Contains("SetMenuIcon") && !feature.Contains("MenuIconOverride")) continue;
            if (TryOverrideIconProps(so, out _, out _, out var path) &&
                !resolvedIconRecords.Contains(entry.comp) && FindAllNodesAtPath(root, EEMPath.FromVrcfControl(path)).Count == 0)
                result.Add(entry.comp);
        }
        return result.Distinct().Where(c => !IsKeptMenuRecord(c)).ToList();
    }

    void CleanUpRecordsInteractive()
    {
        var vrcfType = ConcreteVrcfType();
        if (!avatar || vrcfType == null)
        {
            EditorUtility.DisplayDialog("Nothing to clean", "No VRCFury menu records on this avatar.", "OK");
            return;
        }

        var doomed = FindUnresolvedMenuRecords();

        if (doomed.Count == 0)
        {
            EditorUtility.DisplayDialog("Nothing to clean",
                "No unresolved or redundant move/icon records were found.", "OK");
            return;
        }

        string Describe(Component comp)
        {
            var so = new SerializedObject(comp);
            var from = so.FindProperty("content.fromPath");
            if (from != null) return $"• Move '{from.stringValue}' → '{so.FindProperty("content.toPath")?.stringValue}' on '{comp.gameObject.name}'";
            TryOverrideIconProps(so, out _, out _, out var path);
            return $"• Icon '{path}' on '{comp.gameObject.name}'";
        }
        string list = string.Join("\n", doomed.Take(15).Select(Describe));
        if (doomed.Count > 15) list += $"\n… and {doomed.Count - 15} more";

        if (!EditorUtility.DisplayDialog("Remove unresolved menu records?",
            $"{doomed.Count} move/icon record(s) are unresolved or redundant:\n\n{list}\n\n" +
            "These may be leftovers — or they may target menus that only exist at build time. " +
            "The list includes manually authored records. Ctrl+Z undoes removal.",
            "Remove them", "Keep them"))
        {
            KeepMenuRecords(doomed);
            return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Clean up menu records");
        int grp = Undo.GetCurrentGroup();
        LogOp("CleanRecords", "", "Multiple", string.Join(", ", doomed.Select(Describe)), "", () =>
        {
            foreach (var comp in doomed)
                if (comp) { Undo.DestroyObjectImmediate(comp); }
            QueueRescan();
        });
        Undo.CollapseUndoOperations(grp);
        FlushRescan();
    }
}
