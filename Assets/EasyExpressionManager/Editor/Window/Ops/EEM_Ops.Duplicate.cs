// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — duplicate
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

    bool CanDuplicate(ExpressionNode n)
    {
        if (n == null || n == root) return false;
        if (IsDirectToggle(n)) return true;
        if (n.sourceControl != null && n.sourceMenu != null && !n.isFromFullController)
            return NativeDuplicateMatchesSource(n, new HashSet<VRCExpressionsMenu> { n.sourceMenu });
        return false;
    }

    bool NativeDuplicateMatchesSource(ExpressionNode node, HashSet<VRCExpressionsMenu> visited)
    {
        if (node.mergedSources.Count > 0 || node.isVirtualFolder || node.isFromFullController || node.sourceObject != null) return false;
        var submenu = node.sourceControl?.subMenu;
        if (!submenu || !visited.Add(submenu)) return true;
        var expected = submenu.controls.Where(c => c != null).ToList();
        if (expected.Count != node.children.Count) return false;
        foreach (var child in node.children)
        {
            if (child.sourceMenu != submenu || child.sourceControl == null || !expected.Contains(child.sourceControl) ||
                child.rawName != (child.sourceControl.name ?? "") || child.hasManagedIcon ||
                !NativeDuplicateMatchesSource(child, new HashSet<VRCExpressionsMenu>(visited))) return false;
        }
        return true;
    }

    void OpenDuplicateDialog(ExpressionNode n)
    {
        if (!CanDuplicate(n)) return;
        DuplicateDialog.Open(DisplayOf(n) + " (Copy)", newName =>
        {
            if (string.IsNullOrWhiteSpace(newName)) return;
            SnapshotExpansionStates(root);
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName($"Duplicate \"{DisplayOf(n)}\"");
            int undoGroup = Undo.GetCurrentGroup();
            string logFrom = PathOf(n);
            string logTo   = EEMPath.ReplaceLastSegment(logFrom, EEMPath.Clean(newName));
            var ctx = new Dictionary<string, object>
            {
                ["childCountBefore"] = n.children.Count,
                ["previousMatchCount"] = FindAllNodesAtPath(root, logTo).Count,
                ["previousPopulatedMatchCount"] = FindAllNodesAtPath(root, logTo).Count(item => item.children.Count > 0),
            };
            LogOp("Duplicate", logFrom, n.type.ToString(), logFrom, logTo, () =>
            {
                n = ResolveLiveNode(n);
                if (!CanDuplicate(n)) throw new System.InvalidOperationException("The source changed before it could be duplicated. Rescan and try again.");
                if (IsDirectToggle(n)) DuplicateVRCFuryToggle(n, EEMPath.Clean(newName));
                else if (n.sourceControl != null && n.sourceMenu != null) DuplicateVRCNativeControl(n, EEMPath.Clean(newName));
                QueueRescan();
            }, ctx);
            Undo.CollapseUndoOperations(undoGroup);
            FlushRescan();
            Repaint();
        });
    }

    void DuplicateVRCFuryToggle(ExpressionNode n, string newLeafName)
    {
        if (n == null || n.sourceObject?.targetObject == null)
            throw new System.InvalidOperationException("The VRCFury source component is missing; duplicate was not created.");
        var srcComp = n.sourceObject.targetObject as Component;
        if (!srcComp)
            throw new System.InvalidOperationException("The VRCFury source is not a component; duplicate was not created.");


        var targetGO = srcComp.gameObject;
        var vrcfType = ConcreteVrcfType();
        if (vrcfType == null)
            throw new System.InvalidOperationException("VRCFury is no longer available; duplicate was not created.");
        RequireDurableAvatarState("duplicating a VRCFury toggle");

        var newComp = Undo.AddComponent(targetGO, vrcfType);
        if (!newComp)
            throw new System.InvalidOperationException("Unity could not add the VRCFury component; duplicate was not created.");
        RegisterPluginAddition(newComp);

        var srcSO2 = new SerializedObject(srcComp); srcSO2.Update();
        var newSO  = new SerializedObject(newComp); newSO.Update();
        var srcContent = srcSO2.FindProperty("content");
        var dstContent = newSO.FindProperty("content");
        if (srcContent == null || dstContent == null)
            throw new System.InvalidOperationException("The VRCFury content could not be copied; duplicate was not created.");

        if (srcContent.propertyType == SerializedPropertyType.ManagedReference && srcContent.managedReferenceValue != null)
        {
            var featureType = srcContent.managedReferenceValue.GetType();
            try
            {
                string json   = JsonUtility.ToJson(srcContent.managedReferenceValue);
                var    cloned = System.Activator.CreateInstance(featureType);
                JsonUtility.FromJsonOverwrite(json, cloned);
                dstContent.managedReferenceValue = cloned;
                newSO.ApplyModifiedProperties();
            }
            catch
            {
                CopySerializedPropertyDeep(srcContent, dstContent);
                newSO.ApplyModifiedProperties();
            }
        }
        else { CopySerializedPropertyDeep(srcContent, dstContent); newSO.ApplyModifiedProperties(); }

        newSO.Update();
        var nameProp = newSO.FindProperty("content.name");
        if (nameProp == null || nameProp.propertyType != SerializedPropertyType.String)
            throw new System.InvalidOperationException("The VRCFury duplicate has no writable content name; duplicate was not created.");
        string cur = n.hasManagedMove ? PathOf(n) : EEMPath.FromVrcf(nameProp.stringValue);
        nameProp.stringValue = EEMPath.ToVrcf(
            string.IsNullOrEmpty(cur) ? EEMPath.JoinPath(new[] { newLeafName })
                                      : EEMPath.ReplaceLastSegment(cur, newLeafName));
        newSO.ApplyModifiedProperties();
        if (n.hasManagedIcon)
        {
            newSO.FindProperty("content.enableIcon")?.SetBool(n.icon != null);
            var icon = newSO.FindProperty("content.icon.objRef");
            if (icon != null) icon.objectReferenceValue = n.icon;
            newSO.ApplyModifiedProperties();
        }
        EditorUtility.SetDirty(newComp);
    }

    void DuplicateVRCNativeControl(ExpressionNode n, string newLeafName)
    {
        if (n == null || !n.sourceMenu)
            throw new System.InvalidOperationException("The native source menu is missing; duplicate was not created.");
        if (n.sourceMenu.controls.Count >= MENU_CAPACITY)
            throw new System.InvalidOperationException($"Native expression menus are limited to {MENU_CAPACITY} controls; duplicate was not created.");
        var menu = EnsureWritableNativeMenu(n);
        if (!menu)
            throw new System.InvalidOperationException("The native menu could not be made writable; duplicate was not created.");
        if (n.sourceControl == null)
            throw new System.InvalidOperationException("The native source control is missing; duplicate was not created.");
        if (menu.controls.Count >= MENU_CAPACITY)
            throw new System.InvalidOperationException($"Native expression menus are limited to {MENU_CAPACITY} controls; duplicate was not created.");
        Undo.RecordObject(menu, $"Duplicate VRC Control \"{DisplayOf(n)}\"");
        var original = n.sourceControl;
        int idx = menu.controls.IndexOf(original);
        if (idx < 0) throw new System.InvalidOperationException("The source control changed before it could be duplicated. Rescan and try again.");
        string destination = EEMPath.Combine(PathOf(n.parent, true), newLeafName);
        int ordinal = n.IsFolderLike ? MatchVrcControls(n.parent.children, newLeafName, true).Count : 0;
        // Existing records can immediately move/delete a new control at its
        // requested name. Keep the copy individually addressable until those
        // records have run, then place it beside the displayed original.
        bool stageCopy = ReadMoveRecords().Count > 0;
        string sourceName = stageCopy ? "__EEM_copy_" + System.Guid.NewGuid().ToString("N") : newLeafName;

        // v2.1.0 copied the subMenu reference, so the "copy" shared one asset with
        // the original and editing either one changed both.
        var subMenuCopy = original.subMenu != null ? DeepCopyMenuAsset(original.subMenu, newLeafName) : null;

        var copy = new VRCExpressionsMenu.Control
        {
            name          = sourceName,
            icon          = n.icon,
            type          = original.type,
            parameter     = original.parameter != null ? new VRCExpressionsMenu.Control.Parameter { name = original.parameter.name } : null,
            value         = original.value,
            style         = original.style,
            subMenu       = subMenuCopy,
            subParameters = original.subParameters?.Select(p => p != null ? new VRCExpressionsMenu.Control.Parameter { name = p.name } : null).ToArray(),
            labels        = original.labels?.ToArray()
        };
        menu.controls.Insert(idx + 1, copy);
        EditorUtility.SetDirty(menu);
        if (stageCopy)
        {
            RefreshUnnamedMovePreview();
            var staged = ResolveLiveNode(new ExpressionNode
            {
                sourceMenu = menu, sourceControl = copy, type = NodeTypeOf(copy), parent = n.parent,
            });
            if (staged == null) throw new System.InvalidOperationException("The duplicate could not be located after copying its source.");
            if (n.IsFolderLike && (ordinal > 0 || string.IsNullOrEmpty(newLeafName)))
                destination = EEMPath.ReplaceLastSegment(destination, newLeafName + ".dup." + ordinal);
            CommitMove(PathOf(staged), destination, staged);
        }
    }

    string NativePathOf(ExpressionNode node)
    {
        // A merged destination can contain controls from several original
        // folders. Reversing every matching path prefix picks the wrong source.
        if (node?.nativePath != null) return node.nativePath;
        string path = PathOf(node);
        foreach (var record in OrderMovesByDependency(ReadMoveRecords()).AsEnumerable().Reverse())
            if (!string.IsNullOrEmpty(record.to) && EEMPath.IsAtOrUnder(path, record.to))
                path = EEMPath.RewriteSeg(path, record.to, record.from);
        return path;
    }

    /// Recursively copies a menu asset and everything it points at, so the copy is
    /// genuinely independent.
    VRCExpressionsMenu DeepCopyMenuAsset(VRCExpressionsMenu source, string label)
    {
        // Preserve cycles and repeated references inside the copy; never link back
        // to the original graph because of a depth limit or a failed copy.
        var copies = new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();
        var pending = new Queue<VRCExpressionsMenu>();
        VRCExpressionsMenu Copy(VRCExpressionsMenu menu)
        {
            if (!menu) return null;
            if (copies.TryGetValue(menu, out var existing)) return existing;
            var clone = CloneMenuAsset(menu);
            if (!clone) throw new System.InvalidOperationException("Could not duplicate menu '" + menu.name + "'.");
            copies[menu] = clone;
            pending.Enqueue(menu);
            return clone;
        }
        var result = Copy(source);
        while (pending.Count > 0)
        {
            var original = pending.Dequeue();
            var clone = copies[original];
            foreach (var c in clone.controls)
                if (c != null && c.subMenu) c.subMenu = Copy(c.subMenu);
            EditorUtility.SetDirty(clone);
        }
        return result;
    }

    static void CopySerializedPropertyDeep(SerializedProperty src, SerializedProperty dst)
    {
        var srcIter = src.Copy();
        var srcEnd  = src.GetEndProperty();
        bool enter  = true;
        while (srcIter.NextVisible(enter) && !SerializedProperty.EqualContents(srcIter, srcEnd))
        {
            enter = true;
            string relPath = srcIter.propertyPath.Substring(src.propertyPath.Length).TrimStart('.');
            var    dstProp = dst.serializedObject.FindProperty(dst.propertyPath + "." + relPath);
            if (dstProp == null) continue;
            switch (srcIter.propertyType)
            {
                case SerializedPropertyType.Integer:          dstProp.intValue              = srcIter.intValue;              break;
                case SerializedPropertyType.Boolean:          dstProp.boolValue             = srcIter.boolValue;             break;
                case SerializedPropertyType.Float:            dstProp.floatValue            = srcIter.floatValue;            break;
                case SerializedPropertyType.String:           dstProp.stringValue           = srcIter.stringValue;           break;
                case SerializedPropertyType.Color:            dstProp.colorValue            = srcIter.colorValue;            break;
                case SerializedPropertyType.ObjectReference:  dstProp.objectReferenceValue  = srcIter.objectReferenceValue;  break;
                case SerializedPropertyType.LayerMask:        dstProp.intValue              = srcIter.intValue;              break;
                case SerializedPropertyType.Enum:             dstProp.enumValueIndex        = srcIter.enumValueIndex;        break;
                case SerializedPropertyType.Vector2:          dstProp.vector2Value          = srcIter.vector2Value;          break;
                case SerializedPropertyType.Vector3:          dstProp.vector3Value          = srcIter.vector3Value;          break;
                case SerializedPropertyType.Vector4:          dstProp.vector4Value          = srcIter.vector4Value;          break;
                case SerializedPropertyType.Rect:             dstProp.rectValue             = srcIter.rectValue;             break;
                case SerializedPropertyType.ArraySize:        dstProp.intValue              = srcIter.intValue; enter = false; break;
                case SerializedPropertyType.AnimationCurve:   dstProp.animationCurveValue   = srcIter.animationCurveValue;   break;
                case SerializedPropertyType.Bounds:           dstProp.boundsValue           = srcIter.boundsValue;           break;
                case SerializedPropertyType.Quaternion:       dstProp.quaternionValue       = srcIter.quaternionValue;       break;
                case SerializedPropertyType.Vector2Int:       dstProp.vector2IntValue       = srcIter.vector2IntValue;       break;
                case SerializedPropertyType.Vector3Int:       dstProp.vector3IntValue       = srcIter.vector3IntValue;       break;
                case SerializedPropertyType.RectInt:          dstProp.rectIntValue          = srcIter.rectIntValue;          break;
                case SerializedPropertyType.BoundsInt:        dstProp.boundsIntValue        = srcIter.boundsIntValue;        break;
                default: break;
            }
        }
        dst.serializedObject.ApplyModifiedProperties();
    }

}
