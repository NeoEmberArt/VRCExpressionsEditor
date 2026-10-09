// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — tree row drawing
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

    void DrawRootRow()
    {
        GUI.backgroundColor = new Color(0.12f, 0.22f, 0.32f);
        var boxStyle = new GUIStyle(EditorStyles.helpBox) { margin = new RectOffset(0, 4, 2, 2) };
        EditorGUILayout.BeginVertical(boxStyle);
        EditorGUILayout.BeginHorizontal();

        var rootStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold, fixedHeight = 22 };
        bool rootOver = IsOverCapacity(root);
        rootStyle.normal.textColor = rootOver ? new Color(1f, 0.4f, 0.4f) : Color.white;
        GUILayout.Label(new GUIContent("  Expression Menu", DefaultIcon(NodeType.Folder)), rootStyle, GUILayout.Height(22));

        if (rootOver)
            GUILayout.Label(new GUIContent($"⚠ {root.children.Count}/{MENU_CAPACITY}",
                $"VRChat shows {MENU_CAPACITY} controls per page — this menu has {root.children.Count}."),
                new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 0.45f, 0.35f) }, fontStyle = FontStyle.Bold },
                GUILayout.Width(52));

        GUILayout.FlexibleSpace();
        EditorGUILayout.LabelField(root != null ? $"{allNodesFlatList.Count} items" : "",
            EditorStyles.miniLabel, GUILayout.Width(70));
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
        var rowRect = GUILayoutUtility.GetLastRect();
        GUI.backgroundColor = Color.white;

        // dropping onto the header moves an item to the top level
        HandleRootDrop(rowRect);
    }

    void DrawNode(ExpressionNode n, int indent)
    {
        bool isSearching = !string.IsNullOrWhiteSpace(searchFilter);
        if (isSearching && !searchVisibleNodes.Contains(n)) return;

        bool isMultiSelected = selectionMode && selectedNodes.Contains(n);
        Color baseColor = selected == n   ? new Color(0.2f, 0.5f, 0.9f)
                        : isMultiSelected ? new Color(0.4f, 0.65f, 0.3f)
                        : RowColor(n.type);
        if (n.isVirtualFolder) baseColor = new Color(0.35f, 0.25f, 0.45f);

        var boxStyle = new GUIStyle(EditorStyles.helpBox) { margin = new RectOffset(indent * 16, 4, 2, 2) };
        GUI.backgroundColor = baseColor;

        EditorGUILayout.BeginVertical();
        EditorGUILayout.BeginVertical(boxStyle);
        EditorGUILayout.BeginHorizontal();

        if (selectionMode)
        {
            bool was = selectedNodes.Contains(n);
            bool now = EditorGUILayout.Toggle(was, GUILayout.Width(16));
            if (now != was)
            {
                if (now) { selectedNodes.Add(n); selectionAnchor = n; }
                else selectedNodes.Remove(n);
            }
        }

        if (n.IsFolderLike)
        {
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = Color.white;
            bool next = EditorGUILayout.Foldout(n.isExpanded, GUIContent.none, true);
            GUI.backgroundColor = prevBg;
            if (next != n.isExpanded) { n.isExpanded = next; expansionStates[n.stableKey ?? ""] = next; }
        }
        else GUILayout.Space(15);

        bool overCapacity = IsOverCapacity(n);
        var labelStyle = new GUIStyle(EditorStyles.label)
            { fontStyle = FontStyle.Bold, fixedHeight = 22, richText = false };
        labelStyle.normal.textColor =
            overCapacity          ? new Color(1f, 0.40f, 0.35f) :
            n.isVirtualFolder     ? new Color(0.75f, 0.55f, 1f) :
                                    LabelColor(n.type);
        Texture icon = n.icon ? n.icon : DefaultIcon(n.type);

        if (inlineEditingNode == n)
        {
            GUILayout.Label(new GUIContent("", icon), GUILayout.Width(20), GUILayout.Height(22));
            GUI.SetNextControlName("InlineRename");
            inlineRenameBuffer = EditorGUILayout.TextField(inlineRenameBuffer,
                GUILayout.MinWidth(80), GUILayout.Height(22));
            if (GUILayout.Button(new GUIContent("Done", "Confirm the new name."), GUILayout.Width(46), GUILayout.Height(22)))
                FinishInlineRename();
            var inEv = Event.current;
            if (inEv.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == "InlineRename")
            {
                if (inEv.keyCode == KeyCode.Return || inEv.keyCode == KeyCode.KeypadEnter)
                    { FinishInlineRename(); inEv.Use(); }
                else if (inEv.keyCode == KeyCode.Escape)
                    { CancelInlineRename(); inEv.Use(); }
            }
        }
        else
        {
            string prefix = n.isVirtualFolder ? "📁 " : "";
            string tooltip = BuildRowTooltip(n);
            labelStyle.richText = RowIsRich(n);
            GUILayout.Label(new GUIContent(prefix + " " + RowText(n), icon, tooltip), labelStyle, GUILayout.Height(22));
        }

        GUILayout.FlexibleSpace();

        // ── capacity warning ─────────────────────────────────────────────────
        if (overCapacity)
            GUILayout.Label(new GUIContent($"⚠ {n.children.Count}/{MENU_CAPACITY}",
                    $"VRChat shows {MENU_CAPACITY} controls per page — this menu has {n.children.Count}, " +
                    "so the rest spill onto extra pages."),
                new GUIStyle(EditorStyles.miniLabel)
                    { normal = { textColor = new Color(1f, 0.45f, 0.35f) }, fontStyle = FontStyle.Bold },
                GUILayout.Width(52));

        // ── badges ───────────────────────────────────────────────────────────
        if (n.sourceControl != null && !n.isFromFullController)
            DrawBadge("VRC", new Color(0.2f, 0.6f, 1.0f), "Native VRC Expressions Menu control");
        if (n.sourceObject != null)
            DrawBadge("Fury", new Color(0.7f, 0.3f, 1.0f), "VRCFury Toggle component");
        if (n.isFromFullController)
            DrawBadge("FC", new Color(1.0f, 0.6f, 0.2f), "VRCFury Full Controller menu");
        if (n.isFCPrefixFolder)
            DrawBadge("FC⌂", new Color(0.9f, 0.5f, 0.15f), "Folder created by a Full Controller entry prefix");
        if (n.isVirtualFolder)
            DrawBadge("VM", new Color(0.5f, 0.3f, 0.7f), "Plugin-only Virtual Menu (no VRC asset)");
        if (n.hasManagedMove)
            DrawBadge("→", new Color(0.3f, 0.8f, 0.4f), "Position managed by a Move Menu Item override");
        if (n.hasManagedIcon)
            DrawBadge("🖼", new Color(0.8f, 0.6f, 0.1f), "Icon managed by an Override Menu Icon");
        if (n.sameNameCount > 1)
            GUILayout.Label(new GUIContent("×" + n.sameNameCount, SameNameTooltip(n)),
                new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = DimText } }, GUILayout.ExpandWidth(false));
        if (n.duplicateSourceCount > 1)
            DrawBadge("×" + n.duplicateSourceCount, new Color(0.9f, 0.3f, 0.3f),
                $"{n.duplicateSourceCount} separate components resolve to this same menu path");
        if (n.nameContainsSeparator)
            DrawBadge("/!", new Color(0.9f, 0.35f, 0.2f),
                "This name contains a literal '/'. The tool escapes it in VRCFury paths.");

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
        Rect rowRect = GUILayoutUtility.GetLastRect();
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndVertical();

        HandleNodeEvents(n, rowRect);
        ScrollRowIntoView(n, rowRect);

        if (n.isExpanded || isSearching)
            foreach (var child in n.children) DrawNode(child, indent + 1);
    }

    string BuildRowTooltip(ExpressionNode n)
    {
        var sb = new List<string>();
        string display = DisplayOf(n);
        if (!string.Equals(display, n.rawName, System.StringComparison.Ordinal))
            sb.Add("Stored name: " + n.rawName);
        sb.Add("Path: " + EEMPath.ToVrcf(PathOf(n)));
        sb.Add("Source: " + SourceCategoryOf(n));
        return string.Join("\n", sb);
    }

    void DrawBadge(string label, Color color, string tooltip)
    {
        var prev = GUI.backgroundColor;
        GUI.backgroundColor = color;
        GUILayout.Label(new GUIContent(label, tooltip), new GUIStyle(EditorStyles.miniLabel)
        {
            fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white }
        }, GUILayout.Width(30));
        GUI.backgroundColor = prev;
    }

}
