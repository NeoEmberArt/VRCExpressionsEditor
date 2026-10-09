// Easy Expression Manager: shared toolbar, menu and item action controls.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public partial class EASYExpressionManager
{
    // ── shared actions (Classic uses these too) ───────────────────────────────
    void CreateVirtualMenuInteractive()
    {
        CreateFolderDialog.Open(name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            string internalPath = EEMPath.JoinPath(new[] { EEMPath.Clean(name) });
            LogOp("Create", "", "VirtualFolder", "", internalPath, () =>
            {
                AddVirtualFolder(internalPath);
                QueueRescan();
            });
            FlushRescan();
        });
    }

    void ShowStructureMenu()
    {
        var m = new GenericMenu();
        m.AddItem(new GUIContent("Export Structure…"), false, ExportStructure);
        m.AddItem(new GUIContent("Import Structure…"), false, ImportStructure);
        m.ShowAsContext();
    }

    bool DebugEnabled => DeveloperFlag("DebugJSON", defaultValue: false);

    void ShowDebugMenu()
    {
        var genMenu = new GenericMenu();
        genMenu.AddItem(new GUIContent("Copy JSON to Clipboard"), false, () => GenerateDebugJson(false));
        genMenu.AddItem(new GUIContent("Save JSON File..."), false, () => GenerateDebugJson(true));
        genMenu.AddSeparator("");
        genMenu.AddItem(new GUIContent("Include initial/final tree snapshots"), includeTreesInDebugJson,
            () => { includeTreesInDebugJson = !includeTreesInDebugJson; });
        if (debugLog.Count > 0)
        {
            genMenu.AddSeparator("");
            genMenu.AddItem(new GUIContent("Clear Log"), false, () => { debugLog.Clear(); debugOpCounter = 0; Repaint(); });
        }
        else genMenu.AddDisabledItem(new GUIContent("Clear Log"));
        genMenu.ShowAsContext();
    }

    void DrawRecordsButton(GUIStyle style, params GUILayoutOption[] opts)
    {
        if (danglingRecordDescriptions.Count == 0) return;
        string tip = "Move / icon records that don't resolve to anything in the tree. Click to review and clean up.\n" +
                     string.Join("\n", danglingRecordDescriptions.Take(8));
        var c = IC("console.warnicon.sml", " " + danglingRecordDescriptions.Count, tip,
                    "Records " + danglingRecordDescriptions.Count);
        if (GUILayout.Button(c, style, opts)) Defer(CleanUpRecordsInteractive);
    }

    void DrawDebugButton(GUIStyle style, params GUILayoutOption[] opts)
    {
        if (!DebugEnabled) return;
        if (GUILayout.Button(new GUIContent($"Debug ({debugLog.Count})",
                "Developer tool: logs and verifies every change made here."), style, opts))
            ShowDebugMenu();
    }

    void DrawHelpButton(GUIStyle style, params GUILayoutOption[] opts)
    {
        if (GUILayout.Button(IC("_Help", "", "About & support.", "?"), style, opts))
            AboutPopup.Open(ResolveVersion());
    }

    void DrawAvatarField(string label, float labelWidth)
    {
        float prevLW = EditorGUIUtility.labelWidth;
        if (labelWidth > 0) EditorGUIUtility.labelWidth = labelWidth;
        var prevAvatar = avatar;
        var picked = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent(label, "The avatar whose menu this window edits."), avatar, typeof(GameObject), true);
        EditorGUIUtility.labelWidth = prevLW;
        if (picked != prevAvatar) { var go = picked; Defer(() => SetAvatar(go, true)); }
    }

    void DrawToolbarSearch()
    {
        GUI.SetNextControlName("EEMSearch");
        searchFilter = GUILayout.TextField(searchFilter ?? "", EditorStyles.toolbarSearchField,
                                           GUILayout.MinWidth(60), GUILayout.ExpandWidth(true));
        if (!string.IsNullOrEmpty(searchFilter) &&
            GUILayout.Button(new GUIContent("×", "Clear the search."), EditorStyles.toolbarButton, GUILayout.Width(20)))
        {
            searchFilter = "";
            GUI.FocusControl(null);
        }
    }

    void DrawSlimWarnings(GUIStyle container)
    {
        if (overCapacityMenus.Count == 0) return;
        string tip = $"VRChat shows {MENU_CAPACITY} controls per page; these menus spill onto extra pages:\n" +
                     string.Join("\n", overCapacityMenus.Take(12));
        string text = overCapacityMenus.Count == 1
            ? $" {overCapacityMenus[0]} has more than {MENU_CAPACITY} controls"
            : $" {overCapacityMenus.Count} menus have more than {MENU_CAPACITY} controls";
        EditorGUILayout.BeginHorizontal(container);
        GUILayout.Label(IC("console.warnicon.sml", text, tip, "⚠" + text), EditorStyles.miniLabel,
                        GUILayout.MinWidth(20), GUILayout.ExpandWidth(true), GUILayout.Height(18));
        EditorGUILayout.EndHorizontal();
    }

    // ── edit building blocks ──────────────────────────────────────────────────
    void IconField(ExpressionNode n, float size)
    {
        var newIco = (Texture2D)EditorGUILayout.ObjectField(GUIContent.none, n.icon, typeof(Texture2D), false,
                         GUILayout.Width(size), GUILayout.Height(size));
        if (newIco != n.icon) { pendingIconNode = n; pendingIcon = newIco; }
    }

    void NameField(ExpressionNode n)
    {
        GUI.SetNextControlName("EditPanelName");
        string prev = n.rawName ?? "";
        string next = EditorGUILayout.DelayedTextField(prev);
        if (!string.IsNullOrEmpty(next) && !string.Equals(next, prev, System.StringComparison.Ordinal))
        {
            var cap = n; string nm = next;
            Defer(() => DoRename(cap, nm));
        }
    }

    void ActionMove(ExpressionNode n, GUIContent c, GUIStyle st, params GUILayoutOption[] o)
    {
        if (GUILayout.Button(c, st, o)) OpenMoveDialog(n);
    }

    void ActionDuplicate(ExpressionNode n, GUIContent c, GUIStyle st, params GUILayoutOption[] o)
    {
        using (new EditorGUI.DisabledScope(!CanDuplicate(n)))
            if (GUILayout.Button(c, st, o)) OpenDuplicateDialog(n);
    }

    void ActionDelete(ExpressionNode n, GUIContent c, GUIStyle st, params GUILayoutOption[] o)
    {
        using (new EditorGUI.DisabledScope(!NodeHasDeletableSource(n)))
            if (GUILayout.Button(c, st, o)) { var cap = n; Defer(() => ConfirmAndDelete(cap)); }
    }

    void ActionLocate(ExpressionNode n, GUIContent c, GUIStyle st, params GUILayoutOption[] o)
    {
        var refs = DragRefs(n);
        using (new EditorGUI.DisabledScope(refs.Length == 0))
            if (GUILayout.Button(c, st, o))
            {
                Selection.activeObject = refs[0];
                EditorGUIUtility.PingObject(refs[0]);
            }
    }
}
