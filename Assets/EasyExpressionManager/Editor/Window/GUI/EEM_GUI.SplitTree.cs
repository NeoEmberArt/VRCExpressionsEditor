// Easy Expression Manager: split view tree rows and status markers.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public partial class EASYExpressionManager
{
    // ── list ──────────────────────────────────────────────────────────────────
    List<ExpressionNode> VisibleChildren(ExpressionNode n)
    {
        // copied: a drop or rename inside this pass may rebuild the tree
        return IsSearching ? n.children.Where(c => searchVisibleNodes.Contains(c)).ToList()
                           : new List<ExpressionNode>(n.children);
    }

    void DrawSplitTree()
    {
        if (root == null)
        {
            DrawListHint(avatar
                ? "Press Scan to load this avatar's menu."
                : "Drag an avatar with a VRC Avatar Descriptor into the Avatar field to load its menu.");
            return;
        }

        UpdateSearchCache();
        DrawSplitRootRow();

        var kids = VisibleChildren(root);
        foreach (var k in kids) DrawSplitNode(k, 0);

        if (kids.Count == 0)
            DrawListHint(IsSearching ? $"Nothing matches \"{searchFilter}\"."
                                     : "This menu is empty. Add controls on the avatar, then press Scan.");
    }

    void DrawListHint(string msg)
    {
        GUILayout.Space(18);
        GUILayout.Label(msg, S.hint);
    }

    void DrawSplitRootRow()
    {
        var s = S; var e = Event.current;
        Rect row = GUILayoutUtility.GetRect(10f, ROOT_H, GUILayout.ExpandWidth(true));
        bool drop = draggedNode != null && row.Contains(e.mousePosition);

        Fill(row, HeaderBg);
        Fill(new Rect(row.x, row.yMax - 1, row.width, 1), LineCol);
        if (drop) { Fill(row, WithA(DropCol, 0.15f)); Outline(row, DropCol); }

        float cy = row.center.y;
        float x  = row.x + PAD_L;
        var ico = Ico("Folder Icon") ?? DefaultIcon(NodeType.Folder);
        if (ico && e.type == EventType.Repaint)
            GUI.DrawTexture(new Rect(x, cy - 8, 16, 16), ico, ScaleMode.ScaleToFit);
        x += 21;

        float right = row.xMax - PAD_R;
        string count = ItemsText(allNodesFlatList.Count);
        float cw = s.rightText.CalcSize(new GUIContent(count)).x;
        GUI.Label(new Rect(right - cw, row.y, cw, row.height), count, s.rightText);
        right -= cw + 8;

        bool over = IsOverCapacity(root);
        if (over) right = DrawCapacityWarning(root, right, cy);

        GUI.Label(new Rect(x, row.y, Mathf.Max(10, right - x), row.height),
                  new GUIContent("Expression Menu", "Top level of the avatar's menu. Drop an item here to move it to the top."),
                  over ? s.labelWarn : s.rootLabel);

        HandleRootDrop(row);
    }

    float DrawCapacityWarning(ExpressionNode n, float right, float cy)
    {
        var s = S;
        string txt = $"{n.children.Count}/{MENU_CAPACITY}";
        string tip = $"VRChat shows {MENU_CAPACITY} controls per page; this menu has {n.children.Count}, so the rest spill onto extra pages.";
        float w = s.warnMini.CalcSize(new GUIContent(txt)).x;
        GUI.Label(new Rect(right - w, cy - 8, w, 16), new GUIContent(txt, tip), s.warnMini);
        right -= w + 2;
        var wi = Ico("console.warnicon.sml");
        if (wi)
        {
            var r = new Rect(right - 14, cy - 7, 14, 14);
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(r, wi, ScaleMode.ScaleToFit);
            GUI.Label(r, new GUIContent("", tip));
            right -= 16;
        }
        return right - 4;
    }

    /// The quiet duplicate marker: a grey "×2" in the same muted style as the
    /// item counts, explained on hover. Never coloured, never in the warnings.
    float DrawSameNameMarker(ExpressionNode n, Rect row, float right, bool isSel)
    {
        if (n.sameNameCount <= 1) return right;
        var s = S;
        var st = isSel ? s.rightSel : s.rightText;
        var c = new GUIContent("×" + n.sameNameCount, SameNameTooltip(n));
        float w = st.CalcSize(c).x;
        GUI.Label(new Rect(right - w, row.y, w, row.height), c, st);
        return right - w - 8;
    }

    string SameNameTooltip(ExpressionNode n) =>
        n.sameNameCount == 2
            ? $"Another item in this folder is also called \"{DisplayOf(n)}\"."
            : $"{n.sameNameCount} items in this folder are called \"{DisplayOf(n)}\".";

    void DrawSplitNode(ExpressionNode n, int depth)
    {
        var s = S; var e = Event.current;
        Rect row = GUILayoutUtility.GetRect(10f, ROW_H, GUILayout.ExpandWidth(true));

        bool isSel   = selected == n;
        bool isMulti = selectionMode && selectedNodes.Contains(n);
        bool inside  = row.Contains(e.mousePosition);
        bool drop    = inside && draggedNode != null && CanDropOn(n);
        bool over    = IsOverCapacity(n);
        bool hasKids = n.children.Count > 0;

        float selOff = selectionMode ? 18f : 0f;
        float x      = row.x + PAD_L + selOff + depth * INDENT;
        float cy     = row.center.y;

        if (e.type == EventType.Repaint)
        {
            if (isSel)                            EditorGUI.DrawRect(row, SelBg);
            else if (isMulti)                     EditorGUI.DrawRect(row, SelSoft);
            else if (inside && draggedNode == null) EditorGUI.DrawRect(row, HoverBg);
            if (drop) { EditorGUI.DrawRect(row, WithA(DropCol, 0.14f)); Outline(row, DropCol); }
        }

        // multi-select checkbox
        if (selectionMode)
        {
            bool now = EditorGUI.Toggle(new Rect(row.x + PAD_L, cy - 8, 16, 16), isMulti);
            if (now != isMulti)
            {
                if (now) { selectedNodes.Add(n); selectionAnchor = n; }
                else selectedNodes.Remove(n);
            }
        }

        // foldout (hidden for empty folders and while searching — search shows everything)
        if (n.IsFolderLike && hasKids && !IsSearching)
        {
            bool next = EditorGUI.Foldout(new Rect(x, cy - 8, 14, 16), n.isExpanded, GUIContent.none, false);
            if (next != n.isExpanded)
            {
                var cap = n; bool alt = e.alt;
                // Alt-click opens / closes the whole branch, like the Hierarchy.
                Defer(() =>
                {
                    if (alt) SetAllExpanded(cap, next);
                    else { cap.isExpanded = next; expansionStates[cap.stableKey ?? ""] = next; }
                });
            }
        }
        x += 14f;

        // icon
        Texture tex = TypeIcon(n);
        if (tex && e.type == EventType.Repaint)
            GUI.DrawTexture(new Rect(x + 1, cy - 8, 16, 16), tex, ScaleMode.ScaleToFit);
        x += 22f;

        // right side: item count / type, or the over-capacity warning
        float right = row.xMax - PAD_R;
        right = DrawSameNameMarker(n, row, right, isSel);
        if (over) right = DrawCapacityWarning(n, right, cy);
        else
        {
            string info = n.IsFolderLike ? ItemsText(n.children.Count) : KindShort(n);
            var st = isSel ? s.rightSel : s.rightText;
            float w = st.CalcSize(new GUIContent(info)).x;
            GUI.Label(new Rect(right - w, row.y, w, row.height), info, st);
            right -= w + 6;
        }

        // label / inline rename
        if (inlineEditingNode == n)
        {
            var tf = new Rect(x, cy - 9, Mathf.Max(60, right - x - 52), 18);
            GUI.SetNextControlName("InlineRename");
            inlineRenameBuffer = EditorGUI.TextField(tf, inlineRenameBuffer);
            if (inlineFocusedFor != n)
            {
                inlineFocusedFor = n;
                EditorGUI.FocusTextInControl("InlineRename");
                Repaint();
            }
            if (GUI.Button(new Rect(tf.xMax + 4, cy - 9, 46, 18),
                    new GUIContent("Done", "Confirm the new name (Enter). Esc cancels."), EditorStyles.miniButton))
                FinishInlineRename();
        }
        else
        {
            string tip = inside ? BuildRowTooltip(n) : null;
            var st = over ? s.labelWarn : (isSel ? s.labelSel : s.label);
            st.richText = RowIsRich(n);
            GUI.Label(new Rect(x, row.y, Mathf.Max(10, right - x - 4), row.height), new GUIContent(RowText(n), tip), st);
        }

        HandleNodeEvents(n, row);
        ScrollRowIntoView(n, row);

        if ((n.isExpanded || IsSearching) && hasKids)
            foreach (var k in VisibleChildren(n)) DrawSplitNode(k, depth + 1);
    }

}
