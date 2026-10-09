// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — native VRC menu scanning
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

    /// Per-branch visited set: a menu asset referenced from two different places
    /// now renders in both, while cycles still terminate.
    void BuildVRCMenu(ExpressionNode parent, VRCExpressionsMenu menu, HashSet<VRCExpressionsMenu> branchVisited)
    {
        if (menu == null || branchVisited.Contains(menu)) return;
        var visited = new HashSet<VRCExpressionsMenu>(branchVisited) { menu };
        visitedMenus.Add(menu);

        foreach (var c in menu.controls)
        {
            if (c == null) continue;
            var node = new ExpressionNode
            {
                rawName = c.name ?? "",
                type = NodeTypeOf(c),
                sourceMenu = menu, sourceControl = c, parent = parent, icon = c.icon,
                sourceControlCount = menu.controls.Count,
                sourceKey = "vrc:" + parent.sourceKey + ":" + menu.GetInstanceID() + ":" + menu.controls.IndexOf(c)
            };
            if (c.subMenu != null) BuildVRCMenu(node, c.subMenu, visited);
            parent.children.Add(node);
        }
    }

}
