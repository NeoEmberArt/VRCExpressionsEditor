// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — icon assignment
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

    /// True when this item's icon can only be set through an Override Menu Icon
    /// record (a path string) — folders, virtual menus, generated folders.
    /// Fury toggles, native controls and Full Controller controls are edited
    /// directly on their source.
    bool IconIsPathBased(ExpressionNode n) =>
        n != null &&
        (n.hasManagedIcon || n.mergedSources.Count > 0 ||
        (!(n.sourceControl != null && n.sourceMenu != null && !n.isFromFullController) &&
        !(n.isFromFullController && n.sourceControl != null) &&
        n.sourceObject == null));

    bool ApplyIconChange(ExpressionNode node, Texture2D newIcon)
    {
        if (node == null)
            throw new System.InvalidOperationException("The icon target no longer exists. Rescan before editing it.");
        node.icon = newIcon;
        if (node.hasManagedIcon || node.mergedSources.Count > 0)
        {
            if (node.sameNameCount > 1 && !EditorUtility.DisplayDialog("Change every one?",
                $"{node.sameNameCount} items here are called \"{DisplayOf(node)}\". VRCFury's icon override applies to all of them.",
                "Change all", "Cancel"))
            {
                QueueRescan();
                return false;
            }
            WriteOverrideMenuIcon(PathOf(node), newIcon);
            return true;
        }

        bool handledBySource = false;

        if (node.sourceControl != null && node.sourceMenu != null && !node.isFromFullController)
        {
            var menu = EnsureWritableNativeMenu(node);
            if (!menu || node.sourceControl == null)
                throw new System.InvalidOperationException("The native icon target changed before it could be edited. Rescan and try again.");
            Undo.RecordObject(node.sourceMenu, "Change Icon");
            node.sourceControl.icon = newIcon;
            EditorUtility.SetDirty(node.sourceMenu);
            handledBySource = true;
        }
        else if (node.isFromFullController && node.sourceControl != null)
        {
            var writable = EnsureWritableFCMenu(node, out int idx);
            if (writable == null || idx < 0 || idx >= writable.controls.Count)
                throw new System.InvalidOperationException("The Full Controller icon target changed before it could be edited. Rescan and try again.");
            Undo.RecordObject(writable, "Change Icon");
            writable.controls[idx].icon = newIcon;
            EditorUtility.SetDirty(writable);
            handledBySource = true;
        }

        if (node.sourceObject != null)
        {
            var so = FreshSO(node.sourceObject);
            if (so?.targetObject == null)
                throw new System.InvalidOperationException("The VRCFury icon target changed before it could be edited. Rescan and try again.");
            var enableIcon = so.FindProperty("content.enableIcon");
            var objRef = so.FindProperty("content.icon.objRef");
            if (enableIcon == null && objRef == null)
                throw new System.InvalidOperationException("The VRCFury icon target has no writable icon properties.");
            Undo.RecordObject(so.targetObject, "Change Icon");
            enableIcon?.SetBool(newIcon != null);
            if (objRef != null) objRef.objectReferenceValue = newIcon;
            so.ApplyModifiedProperties();
            node.sourceObject = so;
            EditorUtility.SetDirty(so.targetObject);
            handledBySource = true;
        }

        if (!handledBySource && node.sameNameCount > 1 &&
            !EditorUtility.DisplayDialog("Change every one?",
                $"{node.sameNameCount} items here are called \"{DisplayOf(node)}\". This kind of item gets its icon " +
                "by name, so VRCFury will give all of them this icon.", "Change all", "Cancel"))
        {
            node.icon = null;   // the next scan restores what's really there
            QueueRescan();
            return false;
        }

        if (!handledBySource)
        {
            string path = PathOf(node);
            if (string.IsNullOrEmpty(path))
                throw new System.InvalidOperationException("The icon target has no addressable menu path.");
            WriteOverrideMenuIcon(path, newIcon);
        }
        Repaint();
        return true;
    }

    void WriteOverrideMenuIcon(string internalPath, Texture2D icon)
    {
        var fType    = FindOverrideMenuIconType();
        var vrcfType = ConcreteVrcfType();
        if (fType == null || vrcfType == null)
        {
            throw new System.InvalidOperationException("VRCFury's icon-override feature was not found; the folder icon was not changed.");
        }
        GuardAddressable(internalPath, "write icon override");
        var go = GetOrCreateManager();
        if (!go) throw new System.InvalidOperationException("Could not create the ExpressionsManager object for the folder icon.");
        string vrcfPath = EEMPath.ToVrcf(internalPath);

        bool updated = false;
        foreach (var existing in EnumerateVrcfComponents().Select(e => e.comp))
        {
            if (!existing) continue;
            var eso = new SerializedObject(existing); eso.Update();
            if (eso.FindProperty("content.fromPath") != null) continue;
            if (!TryOverrideIconProps(eso, out var pp, out var ip, out string foundPath)) continue;
            if (!EEMPath.PathEq(EEMPath.FromVrcfControl(foundPath), internalPath)) continue;
            Undo.RecordObject(existing, "Update Override Menu Icon");
            pp.stringValue = vrcfPath;
            ip.objectReferenceValue = icon;
            eso.ApplyModifiedProperties();
            EditorUtility.SetDirty(existing);
            updated = true;
        }
        if (updated) return;

        var comp = AddFeatureComponent(go, fType);
        if (!comp) throw new System.InvalidOperationException("Could not create VRCFury's icon-override feature; the icon was not changed.");
        var so = new SerializedObject(comp); so.Update();
        if (cachedIconMenuPathProp == null) DiscoverOverrideIconProps(so, fType);
        SerializedProperty pathP = null, icoP = null;
        if (cachedIconMenuPathProp != null) pathP = so.FindProperty(cachedIconMenuPathProp);
        if (cachedIconTextureProp  != null) icoP  = so.FindProperty(cachedIconTextureProp);
        if (pathP == null || icoP == null) TryOverrideIconProps(so, out pathP, out icoP, out _);
        if (pathP == null || icoP == null)
        {
            Undo.DestroyObjectImmediate(comp);
            throw new System.InvalidOperationException("VRCFury's icon-override feature has no writable path/icon properties.");
        }
        pathP.stringValue = vrcfPath;
        icoP.objectReferenceValue = icon;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(comp);
        EditorUtility.SetDirty(go);
    }

    void FlushPendingChanges()
    {
        if (pendingIconNode == null) return;
        var n = ResolveLiveNode(pendingIconNode); var ico = pendingIcon;
        pendingIconNode = null; pendingIcon = null;
        if (n == null)
        {
            SetStatus("The selected icon control changed or was removed. Rescan before editing it.", true);
            return;
        }
        string oldIco = n.icon ? n.icon.name : "(none)";
        string newIco = ico ? ico.name : "(none)";
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Change icon");
        int grp = Undo.GetCurrentGroup();
        LogOp("IconChange", PathOf(n), n.type.ToString(), oldIco, newIco, () =>
        {
            var live = ResolveLiveNode(n);
            if (live == null) throw new System.InvalidOperationException("The selected icon control changed during the operation; rescan before editing it.");
            if (!ApplyIconChange(live, ico))
                throw new System.InvalidOperationException("Icon change cancelled; no icon changes were kept.");
            QueueRescan();
        }, new Dictionary<string, object>
        {
            ["expectedIconId"] = ico ? ico.GetInstanceID() : 0,
            ["expectedIconTargets"] = new List<object> { new Dictionary<string, object>
            {
                ["stableKey"] = n.stableKey,
                ["path"] = PathOf(n, n.IsFolderLike),
            } },
        });
        Undo.CollapseUndoOperations(grp);
        FlushRescan();
    }

}
