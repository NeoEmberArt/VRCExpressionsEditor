// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — clone-on-write for Full Controller menus
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
    // Copy the native graph together, preserving cycles and shared submenus.
    // Rebind every scanned row before a bulk operation edits another control.
    VRCExpressionsMenu EnsureWritableNativeMenu(ExpressionNode node)
    {
        if (node == null || !node.sourceMenu || node.isFromFullController) return null;
        var descriptor = avatar ? avatar.GetComponent<VRCAvatarDescriptor>() : null;
        if (!descriptor || !descriptor.expressionsMenu) return null;
        var graph = CaptureFCControlArrays(new[] { descriptor.expressionsMenu });
        if (!graph.ContainsKey(node.sourceMenu))
            throw new System.InvalidOperationException("This native menu changed; rescan before editing it.");
        if (graph.Keys.All(menu => IsOwnedClone(menu) && !NativeMenuUsedElsewhere(menu, descriptor)))
            return node.sourceMenu;
        var rows = new HashSet<ExpressionNode>();
        void CollectRows(ExpressionNode row)
        {
            if (row == null || !rows.Add(row)) return;
            foreach (var source in row.mergedSources) CollectRows(source);
        }
        foreach (var row in allNodesFlatList) CollectRows(row);
        CollectRows(node);
        var fcArrays = CaptureFCControlArrays(rows.Where(row => row.isFromFullController)
            .Select(row => row.sourceFullControllerMenu));
        var copies = new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();
        foreach (var menu in graph.Keys) copies[menu] = CloneMenuAsset(menu);
        foreach (var pair in copies)
        {
            foreach (var control in pair.Value.controls)
                if (control?.subMenu && copies.TryGetValue(control.subMenu, out var child)) control.subMenu = child;
            EditorUtility.SetDirty(pair.Value);
        }
        foreach (var row in rows)
        {
            if (row.isFromFullController && row.sourceFullControllerMenu && row.sourceControl != null &&
                fcArrays.TryGetValue(row.sourceFullControllerMenu, out var previous))
            {
                int fcIndex = System.Array.IndexOf(previous, row.sourceControl);
                if (fcIndex >= 0 && fcIndex < row.sourceFullControllerMenu.controls.Count)
                    row.sourceControl = row.sourceFullControllerMenu.controls[fcIndex];
            }
            if (row.isFromFullController || !row.sourceMenu || !copies.TryGetValue(row.sourceMenu, out var copy)) continue;
            int index = System.Array.IndexOf(graph[row.sourceMenu], row.sourceControl);
            if (index < 0) throw new System.InvalidOperationException("This native control changed; rescan before editing it.");
            row.sourceMenu = copy;
            row.sourceControl = copy.controls[index];
            row.sourceControlCount = copy.controls.Count;
        }
        Undo.RecordObject(descriptor, "Repoint native expression menu");
        descriptor.expressionsMenu = copies[descriptor.expressionsMenu];
        PrefabUtility.RecordPrefabInstancePropertyModifications(descriptor);
        EditorUtility.SetDirty(descriptor);
        return node.sourceMenu;
    }

    bool NativeMenuUsedElsewhere(VRCExpressionsMenu menu, VRCAvatarDescriptor owner)
    {
        if (Resources.FindObjectsOfTypeAll<VRCAvatarDescriptor>().Any(d => d && d != owner &&
            !EditorUtility.IsPersistent(d) && MenuGraphContains(d.expressionsMenu, menu))) return true;
        foreach (var component in Resources.FindObjectsOfTypeAll<Component>())
        {
            if (!component || EditorUtility.IsPersistent(component)) continue;
            string type = component.GetType().FullName ?? "";
            if (!type.Contains("VRCFury") && !type.StartsWith("VF.")) continue;
            var entries = new SerializedObject(component).FindProperty("content.menus");
            if (entries == null || !entries.isArray) continue;
            for (int i = 0; i < entries.arraySize; i++)
                if (MenuGraphContains(entries.GetArrayElementAtIndex(i).FindPropertyRelative("menu.objRef")?.objectReferenceValue as VRCExpressionsMenu, menu)) return true;
        }
        return false;
    }

    [System.NonSerialized] private string regressionCloneRoot;
    string CloneRoot() => regressionCloneRoot ?? CLONE_ROOT;

    bool IsOwnedClone(Object asset)
    {
        string p = AssetDatabase.GetAssetPath(asset);
        return !string.IsNullOrEmpty(p) && p.StartsWith(OwnedCloneFolder() + "/", System.StringComparison.Ordinal);
    }

    string OwnedCloneFolder()
    {
        string safeName = string.Join("_", (avatar ? avatar.name : "Avatar").Split(Path.GetInvalidFileNameChars()));
        return CloneRoot() + "/" + safeName + "_" + AvatarKey();
    }

    string CloneFolderForAvatar()
    {
        string folder = OwnedCloneFolder();
        EnsureProjectFolder(CloneRoot());
        EnsureProjectFolder(folder);
        return folder;
    }

    VRCExpressionsMenu CloneMenuAsset(VRCExpressionsMenu source)
    {
        if (!source) return null;
        string dest = AssetDatabase.GenerateUniqueAssetPath(
            CloneFolderForAvatar() + "/" + SafeFileName(source.name) + ".asset");
        var clone = Instantiate(source);
        clone.name = source.name;
        try
        {
            AssetDatabase.CreateAsset(clone, dest);
            Undo.RegisterCreatedObjectUndo(clone, "Copy expression menu");
            NoteConsequence("copied menu '" + source.name + "'");
            return clone;
        }
        catch
        {
            if (clone && !EditorUtility.IsPersistent(clone)) DestroyImmediate(clone);
            throw;
        }
    }

    static Dictionary<VRCExpressionsMenu, VRCExpressionsMenu.Control[]> CaptureFCControlArrays(IEnumerable<VRCExpressionsMenu> roots)
    {
        var arrays = new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu.Control[]>();
        var pending = new Stack<VRCExpressionsMenu>(roots.Where(menu => menu));
        while (pending.Count > 0)
        {
            var menu = pending.Pop();
            if (!menu || arrays.ContainsKey(menu)) continue;
            var controls = menu.controls.ToArray();
            arrays[menu] = controls;
            foreach (var control in controls)
                if (control?.subMenu) pending.Push(control.subMenu);
        }
        return arrays;
    }

    void RebindFCControlCopies(Dictionary<VRCExpressionsMenu, VRCExpressionsMenu.Control[]> previous, Object owner, int entry)
    {
        foreach (var pair in previous)
        {
            var current = pair.Key.controls;
            if (pair.Value.SequenceEqual(current)) continue;
            if (pair.Value.Length != current.Count)
                throw new System.InvalidOperationException("A Full Controller menu changed while copying or unpacking it; rescan before editing it.");
            for (int i = 0; i < current.Count; i++)
            {
                var before = pair.Value[i];
                if (before == null || before == current[i]) continue;
                foreach (var mapping in fcControlCloneMap.Where(mapping => mapping.Value == before).ToArray())
                    fcControlCloneMap[mapping.Key] = current[i];
                var key = (owner, entry, before);
                if (!fcControlCloneMap.ContainsKey(key)) fcControlCloneMap[key] = current[i];
            }
        }
    }

    /// Ensures every menu asset from the Full Controller entry root down to the
    /// menu containing this node's control is project-owned, repointing each
    /// reference as it goes. Returns the writable menu that holds the control
    /// and the control's index WITHIN THAT MENU, or null if it failed.
    ///
    /// Two things this has to get right when several controls in the same menu
    /// are edited in one operation (a folder delete, a bulk icon apply):
    ///
    ///  • The clone is cached. Every node's fcChain still points at the original
    ///    package asset, because it was captured during the scan. Without the
    ///    cache the second control would clone the untouched original again and
    ///    repoint the entry at it, resurrecting the first control.
    ///
    ///  • Controls are re-resolved by their copied object identity. Indexes
    ///    captured from the original are wrong the moment a sibling is removed.
    VRCExpressionsMenu EnsureWritableFCMenu(ExpressionNode node, out int controlIndex)
    {
        controlIndex = -1;
        if (node?.sourceFullControllerMenu == null || node.sourceFullControllerObject?.targetObject == null ||
            node.sourceControl == null) return null;
        var owner = node.sourceFullControllerObject.targetObject;
        var chain = node.fcChain.Count > 0 ? node.fcChain : new List<VRCExpressionsMenu> { node.sourceFullControllerMenu };
        // Copying or unpacking can recreate controls on other branches too.
        // Capture the entire source graph, not only this row's ancestor route.
        var graphRoots = chain.Concat(fcCloneMap.Where(pair => pair.Key.owner == owner && pair.Key.entry == node.fcEntryIndex).Select(pair => pair.Value)).ToList();
        var originalControlArrays = CaptureFCControlArrays(graphRoots);
        var sourceControl = node.sourceControl;
        var so = FreshSO(node.sourceFullControllerObject);
        RebindFCControlCopies(originalControlArrays, owner, node.fcEntryIndex);
        var entries = so?.FindProperty("content.menus");
        if (entries == null || node.fcEntryIndex < 0 || node.fcEntryIndex >= entries.arraySize) return null;
        VRCExpressionsMenu parent = null;
        foreach (var original in chain)
        {
            if (!original) return null;
            var key = (owner, node.fcEntryIndex, original);
            if (!fcCloneMap.TryGetValue(key, out var writable) || !writable)
            {
                // CreateAsset can deserialize the original's controls. Keep
                // their pre-copy identities so cached selections still map
                // to the correct control in the private copy afterward.
                var originalControls = originalControlArrays[original];
                var previousArrays = CaptureFCControlArrays(graphRoots.Concat(fcCloneMap.Values));
                writable = IsOwnedClone(original) && !FCMenuUsedByOtherEntry(original, owner, node.fcEntryIndex)
                    ? original : CloneMenuAsset(original);
                if (!writable) return null;
                RebindFCControlCopies(previousArrays, owner, node.fcEntryIndex);
                // Redirect earlier identities from this source to its new copy,
                // including controls recreated before this branch was visited.
                for (int i = 0; i < originalControls.Length && i < writable.controls.Count; i++)
                {
                    var before = originalControls[i];
                    var current = i < original.controls.Count ? original.controls[i] : null;
                    foreach (var mapping in fcControlCloneMap.Where(mapping => mapping.Key.owner == owner && mapping.Key.entry == node.fcEntryIndex &&
                        (mapping.Value == before || mapping.Value == current)).ToArray())
                        fcControlCloneMap[mapping.Key] = writable.controls[i];
                }
                fcCloneMap[key] = writable;
                for (int i = 0; i < originalControls.Length && i < writable.controls.Count; i++)
                    if (originalControls[i] != null)
                        fcControlCloneMap[(owner, node.fcEntryIndex, originalControls[i])] = writable.controls[i];
                for (int i = 0; i < original.controls.Count && i < writable.controls.Count; i++)
                    if (original.controls[i] != null)
                        fcControlCloneMap[(owner, node.fcEntryIndex, original.controls[i])] = writable.controls[i];
            }
            if (parent == null)
            {
                // Creating an asset can refresh serialized state. Reacquire the
                // owner and entry after the copy exists, before repointing it.
                so = ReadSO(node.sourceFullControllerObject);
                entries = so?.FindProperty("content.menus");
                if (entries == null || node.fcEntryIndex >= entries.arraySize) return null;
                var prop = entries.GetArrayElementAtIndex(node.fcEntryIndex).FindPropertyRelative("menu.objRef");
                if (prop == null) return null;
                if (prop.objectReferenceValue != writable)
                {
                    Undo.RecordObject(owner, "Repoint Full Controller menu");
                    prop.objectReferenceValue = writable;
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(owner);
                }
            }
            else if (parent.controls.Any(c => c != null && c.subMenu == original && c.subMenu != writable))
            {
                Undo.RecordObject(parent, "Repoint sub-menu");
                foreach (var c in parent.controls)
                    if (c != null && c.subMenu == original) c.subMenu = writable;
                EditorUtility.SetDirty(parent);
            }
            parent = writable;
        }
        if (!parent) return null;
        var wanted = fcControlCloneMap.TryGetValue((owner, node.fcEntryIndex, sourceControl), out var mapped)
            ? mapped : sourceControl;
        controlIndex = parent.controls.IndexOf(wanted);
        if (controlIndex < 0)
            throw new System.InvalidOperationException("This Full Controller control changed during the operation; rescan before editing it.");
        node.sourceFullControllerObject = so;
        node.sourceFullControllerMenu = parent;
        node.sourceControl = wanted;
        node.sourceControlCount = parent.controls.Count;
        return parent;
    }

    bool FCMenuUsedByOtherEntry(VRCExpressionsMenu menu, Object owner, int entryIndex)
    {
        var descriptors = Resources.FindObjectsOfTypeAll<VRCAvatarDescriptor>()
            .Where(d => d && !EditorUtility.IsPersistent(d))
            .Concat(avatar ? new[] { avatar.GetComponent<VRCAvatarDescriptor>() } : new VRCAvatarDescriptor[0])
            .Where(d => d).Distinct().ToList();
        // A generated FC copy can also be the descriptor's native menu (or
        // a nested submenu). That is another owner, even on this same avatar.
        if (descriptors.Any(d => MenuGraphContains(d.expressionsMenu, menu))) return true;
        var owners = descriptors
            .SelectMany(d => d.GetComponentsInChildren<Component>(true))
            .Concat(avatar ? avatar.GetComponentsInChildren<Component>(true) : new Component[0])
            .Where(c => c && !(c is Transform)).Distinct();
        foreach (var component in owners)
        {
            string type = component.GetType().FullName ?? "";
            if (!type.Contains("VRCFury") && !type.StartsWith("VF.")) continue;
            var entries = new SerializedObject(component).FindProperty("content.menus");
            if (entries == null || !entries.isArray) continue;
            for (int i = 0; i < entries.arraySize; i++)
            {
                if (component == owner && i == entryIndex) continue;
                var rootMenu = entries.GetArrayElementAtIndex(i).FindPropertyRelative("menu.objRef")?.objectReferenceValue as VRCExpressionsMenu;
                if (MenuGraphContains(rootMenu, menu)) return true;
            }
        }
        return false;
    }

    static bool MenuGraphContains(VRCExpressionsMenu rootMenu, VRCExpressionsMenu wanted)
    {
        var seen = new HashSet<VRCExpressionsMenu>();
        var pending = new Stack<VRCExpressionsMenu>();
        if (rootMenu) pending.Push(rootMenu);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!current || !seen.Add(current)) continue;
            if (current == wanted) return true;
            foreach (var control in current.controls)
                if (control?.subMenu) pending.Push(control.subMenu);
        }
        return false;
    }
}
