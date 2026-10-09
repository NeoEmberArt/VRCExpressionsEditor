// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — Split View (default design)
// ═════════════════════════════════════════════════════════════════════════════
//
//   ┌ toolbar: Scan · Snapshot · Structure ········ Records · Design · ? ┐
//   ├──────────── list ─────────────┬──────────── details ──────────────┤
//   │ Avatar [            ]         │ Title                             │
//   │ [search                    ]  │ description                       │
//   │ [+ Virtual Menu]  Expand|Coll │ [icon]  Name / Path               │
//   │ Expression Menu     12 items  │ Actions                           │
//   │   ▸ Clothing                  │ Details                           │
//   └───────────────────────────────┴───────────────────────────────────┘
//
// Rows are laid out with one GetRect each and painted by hand so selection
// and hover run edge to edge like Unity's own lists. Every row still goes
// through HandleNodeEvents, so click / double-click rename / ctrl & shift
// select / drag & drop / right-click behave exactly as in Classic.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public partial class EASYExpressionManager
{
    const float SPLIT_LEFT_MIN  = 230f;
    const float SPLIT_RIGHT_MIN = 285f;
    const float ROW_H           = 22f;
    const float ROOT_H          = 24f;
    const float INDENT          = 14f;
    const float PAD_L           = 6f;
    const float PAD_R           = 10f;

    const string StructureTip =
        "Copy this avatar's menu layout onto another avatar with similar content. " +
        "Matches items by name and shows a preview before changing anything.";

    [System.NonSerialized] Vector2        detailsScroll;
    [System.NonSerialized] float          splitRatio = -1f;
    [System.NonSerialized] ExpressionNode inlineFocusedFor;

    float SplitRatio
    {
        get { if (splitRatio < 0f) splitRatio = Mathf.Clamp(EditorPrefs.GetFloat(SPLIT_PREF, 0.55f), 0.25f, 0.8f); return splitRatio; }
        set { splitRatio = Mathf.Clamp(value, 0.25f, 0.8f); }
    }

    bool IsSearching => !string.IsNullOrWhiteSpace(searchFilter);

    // ── layout ────────────────────────────────────────────────────────────────
    void DrawStyledLayout()
    {
        var e = Event.current;
        wantsMouseMove = true;
        if (e.type == EventType.MouseMove) Repaint();
        if (e.type == EventType.Layout)
        {
            uiSel   = (selected != null && selected != root) ? selected : null;
            uiMulti = selectionMode && selectedNodes.Count > 0;
        }
        if (inlineEditingNode == null) inlineFocusedFor = null;

        DrawSplitLayout();
    }

    void DrawSplitLayout()
    {
        var s  = S;
        var tb = EditorStyles.toolbarButton;

        // toolbar
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        using (new EditorGUI.DisabledScope(!avatar))
        {
            if (GUILayout.Button(IC("Refresh", " Scan", "Rescan the avatar."), tb)) Defer(Scan);
            GUILayout.Space(6);
            if (EditorGUILayout.DropdownButton(new GUIContent("Snapshot", "Save or restore the avatar's full menu state."),
                    FocusType.Passive, EditorStyles.toolbarDropDown)) ShowSnapshotMenu();
            if (EditorGUILayout.DropdownButton(new GUIContent("Structure", StructureTip),
                    FocusType.Passive, EditorStyles.toolbarDropDown)) ShowStructureMenu();
        }
        GUILayout.FlexibleSpace();
        DrawRecordsButton(tb);
        DrawDebugButton(tb);
        DrawDesignPicker(EditorStyles.toolbarDropDown);
        DrawHelpButton(tb, GUILayout.Width(24));
        EditorGUILayout.EndHorizontal();

        DrawGestureManagerPlayButton();
        DrawSlimWarnings(s.bar);

        float total = position.width;
        float leftW = Mathf.Clamp(Mathf.Round(total * SplitRatio), SPLIT_LEFT_MIN,
                                  Mathf.Max(SPLIT_LEFT_MIN, total - SPLIT_RIGHT_MIN));

        EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));

        // left: avatar, search, list tools, list
        EditorGUILayout.BeginVertical(GUILayout.Width(leftW));
        EditorGUILayout.BeginVertical(s.bar);
        DrawAvatarField("Avatar", 46);
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        DrawToolbarSearch();
        EditorGUILayout.EndHorizontal();

        // "+ Virtual Menu" sits right above the Expression Menu row it adds to.
        EditorGUILayout.BeginHorizontal(s.bar);
        if (GUILayout.Button(IC("Toolbar Plus", " Virtual Menu", "Create a plugin-only folder for organizing items.",
                "+ Virtual Menu"), EditorStyles.miniButton))
            CreateVirtualMenuInteractive();
        GUILayout.FlexibleSpace();
        using (new EditorGUI.DisabledScope(root == null))
        {
            if (GUILayout.Button(new GUIContent("Expand", "Expand every folder. Alt-click an arrow to open one branch."),
                    EditorStyles.miniButtonLeft)) Defer(() => SetAllExpanded(root, true));
            if (GUILayout.Button(new GUIContent("Collapse", "Collapse every folder."), EditorStyles.miniButtonRight))
                Defer(() => SetAllExpanded(root, false));
        }
        EditorGUILayout.EndHorizontal();

        scroll = EditorGUILayout.BeginScrollView(scroll);
        DrawSplitTree();
        GUILayout.Space(4);
        EditorGUILayout.EndScrollView();
        if (Event.current.type == EventType.Repaint) treeViewHeight = GUILayoutUtility.GetLastRect().height;
        EditorGUILayout.EndVertical();

        DrawSplitter(total);

        // right: details
        EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        detailsScroll = EditorGUILayout.BeginScrollView(detailsScroll);
        DrawDetailsPane();
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();
        DrawFooter();
    }

    void DrawSplitter(float total)
    {
        Rect r = GUILayoutUtility.GetRect(5f, 5f, GUILayout.Width(5f), GUILayout.ExpandHeight(true));
        Fill(new Rect(r.x + 2, r.y, 1, r.height), LineCol);
        EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeHorizontal);

        int id = GUIUtility.GetControlID(FocusType.Passive);
        var e  = Event.current;
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (e.button == 0 && r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; e.Use(); }
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == id)
                {
                    float maxRatio = (total - SPLIT_RIGHT_MIN) / Mathf.Max(1f, total);
                    float minRatio = SPLIT_LEFT_MIN / Mathf.Max(1f, total);
                    SplitRatio = Mathf.Clamp(e.mousePosition.x / Mathf.Max(1f, total), minRatio, Mathf.Max(minRatio, maxRatio));
                    e.Use();
                    Repaint();
                }
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == id)
                {
                    GUIUtility.hotControl = 0;
                    EditorPrefs.SetFloat(SPLIT_PREF, SplitRatio);
                    e.Use();
                }
                break;
        }
    }

}
