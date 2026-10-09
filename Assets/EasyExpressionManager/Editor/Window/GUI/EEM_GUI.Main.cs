// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — top-level OnGUI, toolbar, warnings bar
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

    void OnGUI()
    {
        if (externalChangesPending && Event.current.type == EventType.Layout)
        {
            LoadVirtualFolders();
            Scan();
        }
        SnapshotStatus();
        HandleGlobalInlineRenameKeys();
        HandleKeyboardShortcuts();
        if (!avatar && root == null) TryAutoSelectAvatar(true);
        EnforceWindowWidth();

        // Every layout reads the same window state; only the drawing differs,
        // so switching designs is instant and loses nothing.
        if (Design == EEMDesign.Classic) { wantsMouseMove = false; DrawClassicLayout(); }
        else DrawStyledLayout();

        if (Event.current.type == EventType.DragExited) { draggedNode = null; dragCandidate = null; }
        DrawDragOverlay();

        RunDeferredUi();
        FlushPendingChanges();
        FlushRescan();
    }

    /// The original v2 layout, kept intact so it can be compared against.
    void DrawClassicLayout()
    {
        DrawHeader();

        var prevAvatar = avatar;
        var picked = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Avatar Source", "The avatar whose menu this window edits."),
            avatar, typeof(GameObject), true);
        if (picked != prevAvatar) SetAvatar(picked, true);

        // search bar
        EditorGUILayout.BeginHorizontal();
        GUI.SetNextControlName("EEMSearch");
        searchFilter = EditorGUILayout.TextField(GUIContent.none, searchFilter, "SearchTextField");
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(searchFilter)))
            if (GUILayout.Button(new GUIContent("Clear", "Clear the search."), GUILayout.Width(50))) searchFilter = "";
        EditorGUILayout.EndHorizontal();

        DrawToolbar();
        DrawGestureManagerPlayButton();
        DrawSelectionBar();
        DrawWarningsBar();
        DrawEditPanel();

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        // "+ Virtual Menu" sits right above the Expression Menu row it adds to.
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor = new Color(0.45f, 0.75f, 1f);
        if (GUILayout.Button(new GUIContent("+ Virtual Menu",
                "Create a plugin-only folder for organizing items."), GUILayout.Height(22), GUILayout.Width(120)))
            CreateVirtualMenuInteractive();
        GUI.backgroundColor = Color.white;
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        scroll = EditorGUILayout.BeginScrollView(scroll);
        if (root != null)
        {
            UpdateSearchCache();
            DrawRootRow();
            foreach (var child in root.children) DrawNode(child, 1);
        }
        else
        {
            EditorGUILayout.HelpBox("Assign an avatar and scan to begin.", MessageType.Info);
        }
        EditorGUILayout.EndScrollView();
        if (Event.current.type == EventType.Repaint) treeViewHeight = GUILayoutUtility.GetLastRect().height;
        EditorGUILayout.EndVertical();
        DrawFooter();
    }

    void DrawHeader()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Easy Expression Manager",
            new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });
        GUILayout.FlexibleSpace();
        DrawDesignPicker(EditorStyles.popup);
        EditorGUILayout.LabelField("v" + ResolveVersion(), EditorStyles.miniLabel, GUILayout.Width(48));
        EditorGUILayout.EndHorizontal();
    }

    void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal();

        GUI.backgroundColor = new Color(0.7f, 1f, 0.7f);
        if (GUILayout.Button(new GUIContent("SCAN", "Rescan the avatar."),
                GUILayout.Height(28), GUILayout.Width(64))) Scan();
        GUI.backgroundColor = Color.white;

        if (GUILayout.Button(new GUIContent("Expand", "Expand all folders."),
                GUILayout.Height(28), GUILayout.Width(58))) SetAllExpanded(root, true);
        if (GUILayout.Button(new GUIContent("Collapse", "Collapse all folders."),
                GUILayout.Height(28), GUILayout.Width(64))) SetAllExpanded(root, false);

        using (new EditorGUI.DisabledScope(!avatar))
        {
            GUI.backgroundColor = new Color(0.55f, 0.85f, 0.75f);
            if (GUILayout.Button(new GUIContent("Snapshot ▾",
                    "Save or restore the avatar's full menu state."),
                    GUILayout.Height(28), GUILayout.Width(88)))
                ShowSnapshotMenu();

            GUI.backgroundColor = new Color(0.75f, 0.75f, 0.95f);
            if (GUILayout.Button(new GUIContent("Structure ▾",
                    "Copy this avatar's menu layout onto another avatar with similar content — matches items " +
                    "by name and shows a preview before changing anything."),
                    GUILayout.Height(28), GUILayout.Width(92)))
                ShowStructureMenu();
            GUI.backgroundColor = Color.white;
        }

        if (selectionMode)
        {
            GUI.backgroundColor = new Color(1f, 0.5f, 0.3f);
            if (GUILayout.Button(new GUIContent($"✕ Exit Select ({selectedNodes.Count})",
                    "Leave multi-select mode."), GUILayout.Height(28)))
                ExitSelectionMode();
            GUI.backgroundColor = Color.white;
        }

        GUILayout.FlexibleSpace();

        if (danglingRecordDescriptions.Count > 0)
        {
            GUI.backgroundColor = new Color(1f, 0.75f, 0.2f);
            if (GUILayout.Button(new GUIContent($"Records ⚠ {danglingRecordDescriptions.Count}",
                    "Move / icon records that don't resolve to anything in the tree.\n" +
                    string.Join("\n", danglingRecordDescriptions.Take(8))),
                    GUILayout.Height(28), GUILayout.Width(104)))
                Defer(CleanUpRecordsInteractive);
            GUI.backgroundColor = Color.white;
        }

        if (DeveloperFlag("DebugJSON", defaultValue: false))
        {
            GUI.backgroundColor = new Color(1f, 0.6f, 0.15f);
            if (GUILayout.Button(new GUIContent($"Debug JSON ({debugLog.Count})",
                    "Developer tool: logs and verifies every change made here."),
                    GUILayout.Height(28), GUILayout.Width(140)))
                ShowDebugMenu();
            GUI.backgroundColor = Color.white;
        }

        GUI.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
        if (GUILayout.Button(new GUIContent("?", "About & support."), GUILayout.Height(28), GUILayout.Width(26)))
            AboutPopup.Open(ResolveVersion());
        GUI.backgroundColor = Color.white;

        EditorGUILayout.EndHorizontal();
    }

    void DrawSelectionBar()
    {
        if (!selectionMode || selectedNodes.Count == 0) return;

        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        EditorGUILayout.LabelField($"{selectedNodes.Count} selected", EditorStyles.boldLabel, GUILayout.Width(84));

        GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
        if (GUILayout.Button(new GUIContent("Move Selected…", "Move everything selected into one folder."),
                GUILayout.Height(22), GUILayout.Width(120)))
            OpenBulkMoveDialog();
        GUI.backgroundColor = Color.white;

        EditorGUILayout.LabelField(new GUIContent("Icon:", "Set an icon on everything selected."),
            EditorStyles.miniLabel, GUILayout.Width(32));
        var newBulk = (Texture2D)EditorGUILayout.ObjectField(GUIContent.none, bulkIconPending, typeof(Texture2D), false,
                                                            GUILayout.Width(46), GUILayout.Height(20));
        if (newBulk != bulkIconPending) bulkIconPending = newBulk;

        using (new EditorGUI.DisabledScope(bulkIconPending == null))
        {
            GUI.backgroundColor = new Color(0.85f, 0.7f, 0.3f);
            if (GUILayout.Button(new GUIContent($"Apply to {selectedNodes.Count}", "Apply the icon above to every selected item."),
                    GUILayout.Height(22), GUILayout.Width(94)))
                ApplyBulkIcon(bulkIconPending);
            GUI.backgroundColor = Color.white;
        }
        if (GUILayout.Button(new GUIContent("Clear Icons", "Remove the icon from every selected item."),
                             GUILayout.Height(22), GUILayout.Width(80)))
            ApplyBulkIcon(null);

        GUILayout.FlexibleSpace();
        GUI.backgroundColor = new Color(1f, 0.35f, 0.35f);
        if (GUILayout.Button(new GUIContent("Delete Selected", "Delete everything selected."),
                GUILayout.Height(22), GUILayout.Width(110))) DeleteSelectedNodes();
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();
    }

    void DrawWarningsBar()
    {
        if (overCapacityMenus.Count == 0) return;
        string first = overCapacityMenus[0];
        string extra = overCapacityMenus.Count > 1 ? $" (and {overCapacityMenus.Count - 1} more)" : "";
        EditorGUILayout.HelpBox(
            $"{overCapacityMenus.Count} menu(s) hold more than {MENU_CAPACITY} controls, so VRChat will " +
            $"split them across pages: {first}{extra}", MessageType.Warning);
    }

}
