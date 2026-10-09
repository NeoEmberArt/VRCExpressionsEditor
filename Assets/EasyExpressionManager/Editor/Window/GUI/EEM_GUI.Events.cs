// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — click, drag & drop, selection
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

    void HandleNodeEvents(ExpressionNode n, Rect rowRect)
    {
        var e = Event.current;
        if (e == null) return;

        switch (e.type)
        {
            case EventType.MouseDown:
                if (!rowRect.Contains(e.mousePosition)) break;
                if (e.button == 1) { ShowContextMenu(n); e.Use(); break; }
                if (e.button == 0)
                {
                    if (e.clickCount == 2 && !selectionMode && !e.shift && !e.control && !e.command)
                    {
                        StartInlineRename(n);
                        e.Use();
                        break;
                    }
                    if (e.shift && (selectionAnchor != null || selected != null))
                    {
                        selectionMode = true;
                        if (!e.control && !e.command) selectedNodes.Clear();
                        SelectRange(selectionAnchor ?? selected, n);
                        selected = n;
                        dragCandidate = null;
                        GUI.FocusControl(null);
                        Repaint();
                        e.Use();
                        break;
                    }
                    if (e.control || e.command)
                    {
                        selectionMode = true;
                        if (!selectedNodes.Add(n)) selectedNodes.Remove(n);
                        selectionAnchor = n;
                        selected = n;
                        dragCandidate = null;
                        Repaint();
                        e.Use();
                        break;
                    }
                    // `selected` is deliberately NOT set here. Setting it on
                    // mouse-down would show the edit panel immediately —
                    // before we know whether this is a click or the start of
                    // a drag — and that panel sits above the tree, so it
                    // shifts every row down right as a drag gesture begins.
                    // Resolved on mouse-up instead, once we actually know.
                    dragCandidate  = n;
                    dragStartMouse = e.mousePosition;
                    dragStartTime  = EditorApplication.timeSinceStartup;
                    GUI.FocusControl(null);
                    e.Use();
                }
                break;

            case EventType.MouseDrag:
                if (dragCandidate == n && draggedNode == null)
                {
                    bool farEnough = Vector2.Distance(e.mousePosition, dragStartMouse) > DRAG_START_PIXELS;
                    bool heldLong  = EditorApplication.timeSinceStartup - dragStartTime > DRAG_HOLD_SECONDS;
                    if (farEnough && heldLong) { StartNodeDrag(n); e.Use(); }
                }
                break;

            case EventType.MouseUp:
                if (dragCandidate == n)
                {
                    // Reached mouse-up still holding the candidate — no drag
                    // ever started (StartNodeDrag clears dragCandidate the
                    // moment one does), so this was a plain click. Safe to
                    // show the edit panel now.
                    selected = n;
                    selectionAnchor = n;
                    if (selectionMode) { selectedNodes.Clear(); selectedNodes.Add(n); }
                    dragCandidate = null;
                    Repaint();   // without this, the highlight/edit panel only
                                 // shows up on whatever repaint happens to come
                                 // next, which reads as a delay on selection
                    e.Use();
                }
                break;

            case EventType.DragUpdated:
                if (!rowRect.Contains(e.mousePosition)) break;
                DragAndDrop.visualMode = CanDropOn(n) ? DragAndDropVisualMode.Move : DragAndDropVisualMode.Rejected;
                e.Use();
                break;

            case EventType.DragPerform:
                if (!rowRect.Contains(e.mousePosition)) break;
                if (CanDropOn(n))
                {
                    DragAndDrop.AcceptDrag();
                    // Execute after IMGUI finishes traversing this tree: a rescan
                    // during row drawing leaves later rows bound to the old root.
                    var destination = n;
                    Defer(() => { PerformDropOn(destination); draggedNode = null; dragCandidate = null; });
                }
                else { draggedNode = null; dragCandidate = null; }
                e.Use();
                break;
        }
    }

    void HandleRootDrop(Rect rowRect)
    {
        var e = Event.current;
        if (e == null || draggedNode == null) return;
        if (!rowRect.Contains(e.mousePosition)) return;

        if (e.type == EventType.DragUpdated) { DragAndDrop.visualMode = CanDropOn(root) ? DragAndDropVisualMode.Move : DragAndDropVisualMode.Rejected; e.Use(); }
        else if (e.type == EventType.DragPerform)
        {
            if (CanDropOn(root))
            {
                DragAndDrop.AcceptDrag();
                Defer(() => { PerformDropOn(root); draggedNode = null; dragCandidate = null; });
            }
            else { draggedNode = null; dragCandidate = null; }
            e.Use();
        }
    }

    void SelectRange(ExpressionNode from, ExpressionNode to)
    {
        var order = VisibleOrder();
        int a = order.IndexOf(ResolveLiveNode(from));
        int b = order.IndexOf(ResolveLiveNode(to));
        if (a < 0 || b < 0) { selectedNodes.Add(to); selectionAnchor = to; return; }
        if (a > b) { int t = a; a = b; b = t; }
        for (int i = a; i <= b; i++) selectedNodes.Add(order[i]);
    }

    List<ExpressionNode> DragPayload()
    {
        var live = ResolveLiveNode(draggedNode);
        if (live == null || live == root) return new List<ExpressionNode>();
        return selectionMode && selectedNodes.Contains(live)
            ? NormaliseSelection(selectedNodes) : new List<ExpressionNode> { live };
    }

    bool CanDropOn(ExpressionNode target)
    {
        target = ResolveLiveNode(target);
        if (target == null) return false;
        if (target != root && !target.IsFolderLike) return false;
        var payload = DragPayload();
        return payload.Count > 0 && payload.All(node => target != node && !IsAncestorOf(node, target));
    }

    void StartNodeDrag(ExpressionNode n)
    {
        draggedNode = n;
        dragCandidate = null;
        DragAndDrop.PrepareStartDrag();
        DragAndDrop.objectReferences = DragRefs(n);
        DragAndDrop.SetGenericData("EEMNode", n.stableKey);
        DragAndDrop.StartDrag(DisplayOf(n));
    }

    void PerformDropOn(ExpressionNode target)
    {
        target = ResolveLiveNode(target);
        if (draggedNode == null || target == null) return;
        if (!CanDropOn(target)) return;

        // dragging one of several selected rows moves the whole selection
        var payload = DragPayload();

        string destPath = target == root ? "" : PathOf(target, true);

        if (payload.Count > 1)
        {
            bool anyWillMove = payload.Any(item =>
                !EEMPath.PathEq(PathOf(item), EEMPath.Combine(destPath, item.rawName)));
            if (!anyWillMove) return;   // dropped back where the whole selection already was — nothing to do
            MoveSelectedTo(payload, destPath);
            // Deliberately leave `selected` untouched here too — see the
            // single-item case below for why.
            return;
        }

        var node = payload[0];
        string from = PathOf(node);
        string to   = EEMPath.Combine(destPath, node.rawName);

        if (EEMPath.PathEq(from, to))
        {
            // Dropped back exactly where it already was — nothing to move.
            // Functionally identical to a click, so treat it like one rather
            // than silently doing nothing with no feedback at all.
            selected = node;
            Repaint();
            return;
        }

        SnapshotExpansionStates(root);
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName($"Move \"{DisplayOf(node)}\"");
        int grp = Undo.GetCurrentGroup();

        LogOp("Move", from, node.type.ToString(), from, to, () =>
        {
            PerformMove(node, destPath, target);
        }, new Dictionary<string, object>
        {
            ["sourceCategory"] = SourceCategoryOf(node),
            ["via"] = "drag",
            ["previousMatchCount"] = FindAllNodesAtPath(root, from).Count,
        });

        Undo.CollapseUndoOperations(grp);
        FlushRescan();
        // `selected` is deliberately NOT set to the moved node here. A
        // successful drag just rescanned and rebuilt the whole tree — opening
        // the edit panel on top of that, right as everything else on screen
        // just shifted, is exactly the disorienting behavior this was meant
        // to fix. Whatever was selected before the drag (if anything) stays
        // selected; the moved item can still be clicked normally afterward.
        Repaint();
    }

    void DrawDragOverlay()
    {
        if (draggedNode == null) return;
        var e = Event.current;
        if (e == null || e.type != EventType.Repaint) return;

        string label = DisplayOf(draggedNode);
        if (selectionMode && selectedNodes.Contains(draggedNode) && selectedNodes.Count > 1)
            label = $"{selectedNodes.Count} items";

        var style = new GUIStyle(EditorStyles.helpBox)
            { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        var r = new Rect(e.mousePosition.x + 12, e.mousePosition.y + 8, 220, 24);
        Texture ico = draggedNode.icon ? draggedNode.icon : DefaultIcon(draggedNode.type);
        var prevBg = GUI.backgroundColor;
        GUI.backgroundColor = draggedNode.IsFolderLike
            ? new Color(0.2f, 0.55f, 0.95f) : new Color(0.25f, 0.7f, 0.35f);
        GUI.Box(r, new GUIContent(label, ico), style);
        GUI.backgroundColor = prevBg;
    }

    Object[] DragRefs(ExpressionNode n)
    {
        if (n?.sourceObject?.targetObject is Object s1)               return new[] { s1 };
        if (n?.sourceMenu != null)                                    return new Object[] { n.sourceMenu };
        if (n?.sourceFullControllerObject?.targetObject is Object s2) return new[] { s2 };
        if (n?.sourceFullControllerMenu != null)                      return new Object[] { n.sourceFullControllerMenu };
        return new Object[0];
    }

}
