// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — right-click context menu
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

    void ShowContextMenu(ExpressionNode n)
    {
        selected = n;
        var menu = new GenericMenu();

        menu.AddItem(new GUIContent("Rename"), false, () => StartInlineRename(n));

        if (CanDuplicate(n)) menu.AddItem(new GUIContent("Duplicate"), false, () => OpenDuplicateDialog(n));
        else menu.AddDisabledItem(new GUIContent("Duplicate"));

        menu.AddItem(new GUIContent("Move…"), false, () => OpenMoveDialog(n));
        if (CanNativeReorder(n))
        {
            menu.AddSeparator("");
            if (CanNativeReorder(n, -1))
                menu.AddItem(new GUIContent("Move Up (Native)"), false, () => ReorderNativeSibling(n, -1));
            else menu.AddDisabledItem(new GUIContent("Move Up (Native)"));
            if (CanNativeReorder(n, 1))
                menu.AddItem(new GUIContent("Move Down (Native)"), false, () => ReorderNativeSibling(n, 1));
            else menu.AddDisabledItem(new GUIContent("Move Down (Native)"));
        }
        if (CanDissolve(n)) menu.AddItem(new GUIContent("Dissolve Folder"), false, () => DissolveInteractive(n));
        menu.AddSeparator("");

        if (!selectionMode)
            menu.AddItem(new GUIContent("Select Multiple"), false, () =>
            {
                selectionMode = true;
                selectedNodes.Add(n);
                selectionAnchor = n;
                Repaint();
            });
        else
            menu.AddItem(new GUIContent("Exit Selection"), false, ExitSelectionMode);

        var refs = DragRefs(n);
        if (refs.Length > 0)
            menu.AddItem(new GUIContent("Locate Source"), false, () =>
            {
                Selection.activeObject = refs[0];
                EditorGUIUtility.PingObject(refs[0]);
            });
        else menu.AddDisabledItem(new GUIContent("Locate Source"));

        menu.AddSeparator("");

        // v2.1.0 greyed this out for generated folders — which were exactly the
        // ones that needed removing.
        if (NodeHasDeletableSource(n)) menu.AddItem(new GUIContent("Delete"), false, () => ConfirmAndDelete(n));
        else menu.AddDisabledItem(new GUIContent("Delete  (nothing to remove)"));

        menu.ShowAsContext();
    }

}
