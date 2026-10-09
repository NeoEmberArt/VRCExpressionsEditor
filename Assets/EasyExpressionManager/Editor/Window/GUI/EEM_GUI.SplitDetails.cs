// Easy Expression Manager: single-item and bulk-selection details panels.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public partial class EASYExpressionManager
{
    // ── details pane ──────────────────────────────────────────────────────────
    void DrawDetailsPane()
    {
        var s = S;
        EditorGUILayout.BeginVertical(s.detail);

        if (uiMulti) { DrawBulkDetails(); EditorGUILayout.EndVertical(); return; }

        var n = uiSel;
        if (n == null)
        {
            GUILayout.Space(30);
            GUILayout.Label(root == null
                    ? "Load an avatar to see its menu."
                    : "Select an item in the list to see its details and edit it.\n\n" +
                      "Double-click a row to rename it, drag rows onto folders to move them, and right-click for more.",
                s.hint);
            EditorGUILayout.EndVertical();
            return;
        }

        s.title.richText = RowIsRich(n);
        GUILayout.Label(RowText(n), s.title);
        GUILayout.Label(Describe(n), s.subtitle);
        GUILayout.Space(10);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.BeginVertical(GUILayout.Width(64));
        IconField(n, 64);
        DrawAutoIconButton(64);
        EditorGUILayout.EndVertical();
        GUILayout.Space(10);
        EditorGUILayout.BeginVertical();
        GUILayout.Label("Name", EditorStyles.miniLabel);
        NameField(n);
        GUILayout.Space(4);
        GUILayout.Label("Path", EditorStyles.miniLabel);
        EditorGUILayout.SelectableLabel(EEMPath.ToVrcf(PathOf(n)), EditorStyles.textField, GUILayout.Height(18));
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();

        DrawIconGenerator(n, wide: false);

        DetailSection("Actions");
        EditorGUILayout.BeginHorizontal();
        ActionMove(n,      new GUIContent("Move…",     "Move or rename to any path."),   GUI.skin.button, GUILayout.Height(22));
        ActionDuplicate(n, new GUIContent("Duplicate", "Duplicate this item (Ctrl+D)."), GUI.skin.button, GUILayout.Height(22));
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        ActionLocate(n, new GUIContent("Locate source", "Select the asset or component this comes from."), GUI.skin.button, GUILayout.Height(22));
        if (CanDissolve(n) &&
            GUILayout.Button(new GUIContent("Dissolve", "Move everything inside up one level and remove this folder."),
                GUILayout.Height(22)))
        { var cap = n; Defer(() => DissolveInteractive(cap)); }
        var prevBg = GUI.backgroundColor;
        GUI.backgroundColor = new Color(1f, 0.62f, 0.58f);
        ActionDelete(n, new GUIContent("Delete", "Delete this item (Del)."), GUI.skin.button, GUILayout.Height(22));
        GUI.backgroundColor = prevBg;
        EditorGUILayout.EndHorizontal();

        DetailSection("Details");
        bool over = IsOverCapacity(n);
        if (over)
            DetailLine(WarnText, $"{n.children.Count} items ? extra pages");
        var badges = BadgesOf(n);
        foreach (var b in badges) DetailLine(b.color, b.detail);
        if (n.sameNameCount > 1)
        {
            DetailLine(DimText, $"{n.sameNameCount} items share this name.");
        }
        if (!over && badges.Count == 0 && n.sameNameCount <= 1)
            GUILayout.Label("Nothing unusual about this item.", s.subtitle);

        EditorGUILayout.EndVertical();
    }

    void DrawBulkDetails()
    {
        var s = S;
        GUILayout.Label($"{selectedNodes.Count} items selected", s.title);
        GUILayout.Label("Ctrl-click rows to add or remove them, Shift-click to select a range.", s.subtitle);

        DetailSection("Move");
        if (GUILayout.Button(new GUIContent("Move selected…", "Move everything selected into one folder."), GUILayout.Height(22)))
            OpenBulkMoveDialog();

        DetailSection("Icon");
        EditorGUILayout.BeginHorizontal();
        bulkIconPending = (Texture2D)EditorGUILayout.ObjectField(GUIContent.none, bulkIconPending, typeof(Texture2D), false,
                                                                 GUILayout.Width(64), GUILayout.Height(64));
        GUILayout.Space(10);
        EditorGUILayout.BeginVertical();
        using (new EditorGUI.DisabledScope(bulkIconPending == null))
            if (GUILayout.Button(new GUIContent("Apply to selected", "Apply this icon to every selected item."), GUILayout.Height(22)))
            { var ico = bulkIconPending; Defer(() => ApplyBulkIcon(ico)); }
        if (GUILayout.Button(new GUIContent("Clear icons", "Remove the icon from every selected item."), GUILayout.Height(22)))
            Defer(() => ApplyBulkIcon(null));
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();

        DetailSection("Remove");
        var prevBg = GUI.backgroundColor;
        GUI.backgroundColor = new Color(1f, 0.62f, 0.58f);
        if (GUILayout.Button(new GUIContent("Delete selected", "Delete everything selected."), GUILayout.Height(22)))
            Defer(DeleteSelectedNodes);
        GUI.backgroundColor = prevBg;

        GUILayout.Space(14);
        if (GUILayout.Button(new GUIContent("Done selecting", "Leave multi-select (Esc)."), GUILayout.Height(22)))
            Defer(ExitSelectionMode);
    }

    void DetailSection(string title)
    {
        GUILayout.Space(12);
        GUILayout.Label(title, S.section);
        Fill(GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)), LineCol);
        GUILayout.Space(6);
    }

    void DetailLine(Color c, string text)
    {
        EditorGUILayout.BeginHorizontal();
        var r = GUILayoutUtility.GetRect(12f, 16f, GUILayout.Width(12f), GUILayout.Height(16f));
        Dot(new Rect(r.x + 1, r.y + 5, 7, 7), c);
        GUILayout.Label(text, EditorStyles.wordWrappedLabel);
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(2);
    }
}
