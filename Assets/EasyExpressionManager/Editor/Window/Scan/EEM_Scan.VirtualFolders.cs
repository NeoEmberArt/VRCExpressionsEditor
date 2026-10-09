// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — virtual folder injection
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

    void InjectVirtualFolders(ExpressionNode treeRoot)
    {
        // A registration for B.dup.1 can precede B after a rename. Recreate
        // lower ordinals first while preserving ordinary registration order.
        var paths = virtualFolderPaths.OrderBy(path => EEMPath.SplitPath(path).Sum(part =>
        {
            int selector = part.IndexOf(".dup.", System.StringComparison.Ordinal);
            return selector >= 0 && int.TryParse(part.Substring(selector + 5), out int ordinal) ? ordinal : 0;
        })).ToArray();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var parts = EEMPath.SplitPath(path);
            if (parts.Length == 0) continue;
            var cur = treeRoot;
            for (int i = 0; i < parts.Length; i++)
            {
                string raw    = parts[i];
                bool   isLast = i == parts.Length - 1;
                var found = DestinationFolder(cur, raw, false);
                if (found == null)
                {
                    found = DestinationFolder(cur, raw, true);
                    found.isVirtualIntermediate = !isLast;
                }
                if (isLast) { found.isVirtualFolder = true; found.virtualPath = EEMPath.Norm(path); }
                cur = found;
            }
        }
    }

}
