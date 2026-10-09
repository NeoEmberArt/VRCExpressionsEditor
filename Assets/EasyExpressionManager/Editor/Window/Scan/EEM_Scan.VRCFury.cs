// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — VRCFury toggle & Full Controller scanning
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

    void ScanVRCFury(Transform t, ExpressionNode rootNode)
    {
        if (!t) return;
        if (t.name == MANAGER_OBJECT_NAME && t.parent == avatar?.transform) return;

        foreach (var comp in t.GetComponents<Component>())
        {
            if (!comp || comp is Transform) continue;
            string fn = comp.GetType().FullName ?? "";
            if (!fn.Contains("VRCFury") && !fn.Contains("VF.")) continue;

            var so        = new SerializedObject(comp);
            var nameProp  = so.FindProperty("content.name");
            var menusProp = so.FindProperty("content.menus");

            if (nameProp?.propertyType == SerializedPropertyType.String && !string.IsNullOrEmpty(nameProp.stringValue))
                ScanToggle(so, nameProp, rootNode);
            else if (menusProp != null && menusProp.isArray && menusProp.arraySize > 0)
                ScanFullController(so, menusProp, rootNode);
        }
        foreach (Transform child in t) ScanVRCFury(child, rootNode);
    }

    void ScanToggle(SerializedObject so, SerializedProperty nameProp, ExpressionNode rootNode)
    {
        string vrcfPath = EEMPath.Clean(nameProp.stringValue);
        if (string.IsNullOrEmpty(vrcfPath)) return;

        Texture2D icon = null;
        var enableProp = so.FindProperty("content.enableIcon");
        if (enableProp != null && enableProp.boolValue)
        {
            var objRef = so.FindProperty("content.icon.objRef");
            if (objRef?.objectReferenceValue is Texture2D t2d) icon = t2d;
        }

        string internalPath = EEMPath.FromVrcf(vrcfPath);
        string[] parts = EEMPath.SplitPath(internalPath);
        if (parts.Length == 0) return;

        var leaf = new ExpressionNode
        {
            fullPath = vrcfPath,
            rawName  = parts[parts.Length - 1],
            type     = NodeType.Toggle,
            sourceObject = so,
            sourceKey = "fury:" + so.targetObject.GetInstanceID(),
            icon     = icon
        };
        AttachToggle(rootNode, parts, leaf);
    }

    void ScanFullController(SerializedObject so, SerializedProperty menusProp, ExpressionNode rootNode)
    {
        for (int i = 0; i < menusProp.arraySize; i++)
        {
            var entry      = menusProp.GetArrayElementAtIndex(i);
            var menuObjRef = entry.FindPropertyRelative("menu.objRef");
            if (!(menuObjRef?.objectReferenceValue is VRCExpressionsMenu fcMenu)) continue;

            string prefix = EEMPath.Clean(entry.FindPropertyRelative("prefix")?.stringValue ?? "");
            var parent = rootNode;

            if (!string.IsNullOrEmpty(prefix))
            {
                int prefixDepth = 0;
                foreach (string rawPart in EEMPath.SplitPath(EEMPath.FromVrcf(prefix)))
                {
                    var found = DestinationFolder(parent, rawPart, true);
                    found.hasNativeFolderSource = true;
                    // Remember EVERY (component, prefix) pair that materialises
                    // this folder — not just one owner. Two different Full
                    // Controller components can each own an entry that lands
                    // under the same folder path (e.g. one owns "Setting/autoSet",
                    // another owns bare "Setting"), and deleting the folder has
                    // to clean up entries on ALL of them, not just whichever one
                    // the scan happened to visit last.
                    if (found.sourceKey == "") found.sourceKey = "prefix:" + so.targetObject.GetInstanceID() + ":" + i + ":" + prefixDepth;
                    prefixDepth++;
                    found.isFCPrefixFolder = true;
                    var gen = new FCPrefixGenerator(so, prefix);
                    if (!found.fcPrefixGenerators.Any(g => g.owner?.targetObject == so.targetObject && g.prefix == prefix))
                        found.fcPrefixGenerators.Add(gen);
                    parent = found;
                }
            }

            var chain = new List<VRCExpressionsMenu>();
            BuildFullControllerMenu(parent, fcMenu, so, i, chain, new HashSet<VRCExpressionsMenu>());
        }
    }

    void BuildFullControllerMenu(ExpressionNode parent, VRCExpressionsMenu menu, SerializedObject fcSO,
                                  int entryIndex, List<VRCExpressionsMenu> chain,
                                  HashSet<VRCExpressionsMenu> branchVisited, string route = "")
    {
        if (menu == null || branchVisited.Contains(menu)) return;
        var visited   = new HashSet<VRCExpressionsMenu>(branchVisited) { menu };
        var thisChain = new List<VRCExpressionsMenu>(chain) { menu };

        var folderOccurrences = new Dictionary<string, int>();
        foreach (var c in menu.controls)
        {
            if (c == null) continue;
            var node = new ExpressionNode
            {
                rawName = c.name ?? "",
                type = NodeTypeOf(c),
                sourceControl = c,
                sourceControlCount = menu.controls.Count,
                sourceKey = "fc:" + fcSO.targetObject.GetInstanceID() + ":" + entryIndex + ":" + route + "/" + menu.controls.IndexOf(c),
                sourceFullControllerMenu = menu,
                sourceFullControllerObject = fcSO,
                fcEntryIndex = entryIndex,
                fcChain = thisChain,
                isFromFullController = true,
                parent = parent,
                icon = c.icon
            };
            if (c.subMenu != null)
                BuildFullControllerMenu(node, c.subMenu, fcSO, entryIndex, thisChain, visited, route + "/" + menu.controls.IndexOf(c));
            if (node.IsFolderLike)
            {
                folderOccurrences.TryGetValue(node.rawName, out int occurrence);
                folderOccurrences[node.rawName] = occurrence + 1;
                var sameFolders = MatchVrcControls(parent.children, node.rawName, true);
                if (occurrence < sameFolders.Count) MergeNodes(sameFolders[occurrence], node);
                else parent.children.Add(node);
            }
            else parent.children.Add(node);
        }
    }

    void AttachToggle(ExpressionNode rootNode, string[] rawParts, ExpressionNode leaf)
    {
        var cur = rootNode;
        for (int i = 0; i < rawParts.Length - 1; i++)
        {
            string raw = rawParts[i];
            var found = DestinationFolder(cur, raw, false);
            if (found == null)
            {
                found = DestinationFolder(cur, raw, true);
            }
            cur = found;
            cur.hasNativeFolderSource = true;
        }

        // Two Fury toggles with the same path are two menu items in VRChat —
        // VRCFury's NewMenuItem always appends a control. Show both, side by
        // side, exactly as they'll appear (they used to be merged into one row).
        string leafRaw = rawParts[rawParts.Length - 1];
        leaf.parent = cur; leaf.rawName = leafRaw; cur.children.Add(leaf);
    }

    void Attach(ExpressionNode rootNode, string[] rawParts, ExpressionNode leaf, Component generator)
    {
        var cur = rootNode;
        for (int i = 0; i < rawParts.Length; i++)
        {
            string raw = rawParts[i];
            bool isLast = i == rawParts.Length - 1;
            var found = !isLast || leaf.IsFolderLike ? DestinationFolder(cur, raw, false) : null;
            if (found == null)
            {
                found = isLast ? leaf : DestinationFolder(cur, raw, true);
                found.parent  = cur;
                if (isLast) found.rawName = leaf.IsFolderLike && raw.Contains(".dup.")
                    ? raw.Substring(0, raw.IndexOf(".dup.", System.StringComparison.Ordinal)) : raw;
                if (!isLast && generator != null)
                {
                    // This folder exists only because a MoveMenuItem points into it.
                    found.isMoveGeneratedFolder = true;
                    if (!found.moveGenerators.Contains(generator)) found.moveGenerators.Add(generator);
                }
                if (isLast) cur.children.Add(found);
            }
            else if (isLast && !(found.IsFolderLike && leaf.IsFolderLike))
            {
                // Same rule as VRCFury's MenuManager.MergeMenu: a moved folder
                // merges into a same-name folder, but any other item is simply
                // appended next to the one already there.
                leaf.parent  = cur;
                leaf.rawName = raw;
                cur.children.Add(leaf);
                found = leaf;
            }
            else
            {
                if (!isLast && generator != null && !found.moveGenerators.Contains(generator))
                    found.moveGenerators.Add(generator);
                if (isLast) MergeNodes(found, leaf);
            }
            cur = found;
        }
    }

    void MergeNodes(ExpressionNode target, ExpressionNode incoming)
    {
        if (target == null || incoming == null || target == incoming) return;

        // two independent components resolving to one path — surfaced as a badge
        // A merged folder has several backing controls. Keep every source so
        // deleting the combined folder can remove all originals, and keep leaf
        // controls as separate rows even when their names match.
        target.mergedSources.Add(incoming);
        if (target.type == NodeType.Folder) target.type = incoming.type;
        if (target.icon == null) target.icon = incoming.icon;
        target.isFCPrefixFolder |= incoming.isFCPrefixFolder;
        foreach (var gen in incoming.fcPrefixGenerators)
            if (!target.fcPrefixGenerators.Any(g => g.owner?.targetObject == gen.owner?.targetObject && g.prefix == gen.prefix))
                target.fcPrefixGenerators.Add(gen);
        target.isMoveGeneratedFolder |= incoming.isMoveGeneratedFolder;
        foreach (var g in incoming.moveGenerators)
            if (!target.moveGenerators.Contains(g)) target.moveGenerators.Add(g);
        foreach (var g in incoming.appliedMoveGenerators)
            if (!target.appliedMoveGenerators.Contains(g)) target.appliedMoveGenerators.Add(g);
        target.isVirtualFolder |= incoming.isVirtualFolder;
        if (string.IsNullOrEmpty(target.virtualPath)) target.virtualPath = incoming.virtualPath;
        target.isVirtualIntermediate &= incoming.isVirtualIntermediate;
        target.hasManagedIcon  |= incoming.hasManagedIcon;

        var folderOccurrences = new Dictionary<string, int>();
        foreach (var child in incoming.children.ToArray())
        {
            ExpressionNode ex = null;
            if (child.IsFolderLike)
            {
                folderOccurrences.TryGetValue(child.rawName, out int occurrence);
                folderOccurrences[child.rawName] = occurrence + 1;
                var folders = MatchVrcControls(target.children, child.rawName, true);
                if (occurrence < folders.Count) ex = folders[occurrence];
            }
            if (ex == null) { incoming.children.Remove(child); child.parent = target; target.children.Add(child); }
            else MergeNodes(ex, child);
        }
    }

}
