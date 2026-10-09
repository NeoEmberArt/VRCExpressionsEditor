// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — keyboard shortcuts
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

    static readonly string[] TextControlNames = { "InlineRename", "EditPanelName", "EEMSearch", "MoveFrom", "MoveTo", "PickFolder", "DupName", "FolderName", "PromptValue" };

    bool IsTypingInTextField()
    {
        if (EditorGUIUtility.editingTextField) return true;
        string focused = GUI.GetNameOfFocusedControl();
        return !string.IsNullOrEmpty(focused) && TextControlNames.Contains(focused);
    }

    // ── arrow-key navigation (like the Hierarchy) ──────────────────────────────
    //   ↑ ↓          previous / next visible row
    //   Home / End   first / last row
    //   →            open a folder; if already open, step into it
    //   ←            close a folder; otherwise jump to its parent
    // Every change is deferred to the end of the frame (expanding a folder
    // mid-pass would desync IMGUI's layout), and the list scrolls to follow.

    [System.NonSerialized] float          treeViewHeight = 300f;
    [System.NonSerialized] ExpressionNode pendingScrollTo;

    List<ExpressionNode> VisibleOrder()
    {
        var list = new List<ExpressionNode>();
        bool searching = !string.IsNullOrWhiteSpace(searchFilter);
        void Walk(ExpressionNode n)
        {
            foreach (var c in n.children)
            {
                if (searching && !searchVisibleNodes.Contains(c)) continue;
                list.Add(c);
                if ((c.isExpanded || searching) && c.children.Count > 0) Walk(c);
            }
        }
        if (root != null) Walk(root);
        return list;
    }

    bool HandleArrowKeys(Event e)
    {
        var k = e.keyCode;
        if (k != KeyCode.UpArrow && k != KeyCode.DownArrow && k != KeyCode.LeftArrow &&
            k != KeyCode.RightArrow && k != KeyCode.Home && k != KeyCode.End) return false;
        if (root == null || e.control || e.command || e.alt) return false;

        var order = VisibleOrder();
        e.Use();
        if (order.Count == 0) return true;

        bool searching = !string.IsNullOrWhiteSpace(searchFilter);
        var  cur  = selected != null && selected != root ? selected : null;
        int  i    = cur != null ? order.IndexOf(cur) : -1;
        ExpressionNode next = null;

        switch (k)
        {
            case KeyCode.DownArrow: next = i < 0 ? order[0] : order[Mathf.Min(i + 1, order.Count - 1)]; break;
            case KeyCode.UpArrow:   next = i < 0 ? order[0] : order[Mathf.Max(i - 1, 0)];               break;
            case KeyCode.Home:      next = order[0];               break;
            case KeyCode.End:       next = order[order.Count - 1]; break;

            case KeyCode.RightArrow:
                if (cur == null) { next = order[0]; break; }
                if (cur.IsFolderLike && cur.children.Count > 0)
                {
                    if (!cur.isExpanded && !searching)
                    {
                        var cap = cur;
                        Defer(() => { cap.isExpanded = true; expansionStates[cap.stableKey ?? ""] = true; });
                    }
                    else if (i >= 0 && i + 1 < order.Count && order[i + 1].parent == cur) next = order[i + 1];
                }
                break;

            case KeyCode.LeftArrow:
                if (cur == null) { next = order[0]; break; }
                if (cur.IsFolderLike && cur.isExpanded && cur.children.Count > 0 && !searching)
                {
                    var cap = cur;
                    Defer(() => { cap.isExpanded = false; expansionStates[cap.stableKey ?? ""] = false; });
                }
                else if (cur.parent != null && cur.parent != root) next = cur.parent;
                break;
        }

        if (next != null && next != cur)
        {
            var target = next;
            Defer(() => { selected = target; pendingScrollTo = target; });
        }
        Repaint();
        return true;
    }

    /// Called while drawing a row: if it's the row we just moved to, nudge the
    /// list so it's fully visible.
    void ScrollRowIntoView(ExpressionNode n, Rect row)
    {
        if (pendingScrollTo != n || Event.current.type != EventType.Repaint) return;
        float h = Mathf.Max(40f, treeViewHeight);
        if (row.y < scroll.y)               scroll.y = Mathf.Max(0f, row.y - 4f);
        else if (row.yMax > scroll.y + h)   scroll.y = row.yMax - h + 4f;
        pendingScrollTo = null;
        Repaint();
    }

    void HandleKeyboardShortcuts()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown) return;
        if (inlineEditingNode != null) return;            // handled by the inline editor
        if (IsTypingInTextField()) return;                // never steal keys from a text box

        bool ctrl = e.control || e.command;

        if (HandleArrowKeys(e)) return;

        // Escape — leave selection mode
        if (e.keyCode == KeyCode.Escape && selectionMode)
        {
            ExitSelectionMode();
            e.Use();
            return;
        }

        // Delete (and Cmd+Backspace, the Mac equivalent)
        if (e.keyCode == KeyCode.Delete || (e.keyCode == KeyCode.Backspace && e.command))
        {
            if (selectionMode && selectedNodes.Count > 0) { DeleteSelectedNodes(); e.Use(); return; }
            if (selected != null && selected != root && NodeHasDeletableSource(selected))
            {
                ConfirmAndDelete(selected);
                e.Use();
                return;
            }
        }

        // Ctrl/Cmd + D — duplicate
        if (ctrl && e.keyCode == KeyCode.D)
        {
            if (selected != null && CanDuplicate(selected)) { OpenDuplicateDialog(selected); e.Use(); }
            return;
        }

        // Ctrl/Cmd + A — select all (only meaningful in selection mode)
        if (ctrl && e.keyCode == KeyCode.A && selectionMode)
        {
            selectedNodes = new HashSet<ExpressionNode>(allNodesFlatList);
            e.Use();
            Repaint();
            return;
        }

        // F2 — rename
        if (e.keyCode == KeyCode.F2 && selected != null && selected != root)
        {
            StartInlineRename(selected);
            e.Use();
        }
    }

}
