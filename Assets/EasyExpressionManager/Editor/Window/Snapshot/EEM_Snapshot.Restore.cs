// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — snapshot restore
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
    static bool HasNonEmptyString(Dictionary<string, object> data, string key)
    {
        return data != null && data.TryGetValue(key, out var value) &&
            value is string text && !string.IsNullOrWhiteSpace(text);
    }

    static bool IsJsonNumber(object value) =>
        value is byte || value is sbyte || value is short || value is ushort ||
        value is int || value is uint || value is long || value is ulong ||
        value is float || value is double || value is decimal;

    static bool HasValidAssetIdentity(Dictionary<string, object> data)
    {
        if (!HasNonEmptyString(data, "guid") || !HasNonEmptyString(data, "localId")) return false;
        return long.TryParse(MiniJson.Str(data, "localId"), out _);
    }

    static bool IsReferenceDescriptor(object value)
    {
        if (value == null) return true;
        var descriptor = value as Dictionary<string, object>;
        if (descriptor == null) return false;
        string kind = MiniJson.Str(descriptor, "kind");
        if (kind == "nonasset") return HasNonEmptyString(descriptor, "snapshotId");
        if (kind == "unresolved")
            return HasNonEmptyString(descriptor, "instanceId") &&
                int.TryParse(MiniJson.Str(descriptor, "instanceId"), out _);
        if (kind == "asset" || (string.IsNullOrEmpty(kind) &&
            !string.IsNullOrEmpty(MiniJson.Str(descriptor, "guid"))))
            return HasValidAssetIdentity(descriptor);
        if (kind == "sceneObject" || kind == "sceneComponent")
            return HasNonEmptyString(descriptor, "type") &&
                descriptor.TryGetValue("path", out var path) && path is string;
        return false;
    }

    bool ValidateSnapshotPayload(Dictionary<string, object> data, out string error)
    {
        error = null;
        if (data == null) { error = "the root value is not an object"; return false; }
        if (!data.TryGetValue("format", out var format) || !(format is string) ||
            !string.Equals((string)format, SNAPSHOT_FORMAT, System.StringComparison.Ordinal))
        {
            error = "unsupported or missing snapshot format";
            return false;
        }

        bool legacySnapshot = !data.ContainsKey("pluginVersion");
        if (data.ContainsKey("pluginVersion") && !HasNonEmptyString(data, "pluginVersion"))
        {
            error = "pluginVersion must be a non-empty string when present";
            return false;
        }
        // Current snapshots always record the descriptor root. A pre-root
        // snapshot can still restore its components/menus, but the caller must
        // preserve the current native menu rather than guessing a root.
        if (!legacySnapshot && !data.ContainsKey("nativeMenu"))
        {
            error = "current snapshot is missing nativeMenu";
            return false;
        }

        foreach (string key in new[] { "virtualFolders", "vrcfComponents", "menuAssets" })
        {
            if (!data.TryGetValue(key, out var value) || !(value is List<object>))
            {
                error = "required array '" + key + "' is missing or malformed";
                return false;
            }
        }
        if (data.ContainsKey("avatarKey") && !(data["avatarKey"] is string))
        {
            error = "avatarKey must be a string when present";
            return false;
        }
        if (data.ContainsKey("nativeMenu") && !IsReferenceDescriptor(data["nativeMenu"]))
        {
            error = "nativeMenu is malformed";
            return false;
        }
        if (data.ContainsKey("expressionParameters") && !IsReferenceDescriptor(data["expressionParameters"]))
        {
            error = "expressionParameters is malformed";
            return false;
        }
        if (data.ContainsKey("automatic") && !(data["automatic"] is bool))
        {
            error = "automatic must be a boolean when present";
            return false;
        }
        if (data.ContainsKey("isBaseline") && !(data["isBaseline"] is bool))
        {
            error = "isBaseline must be a boolean when present";
            return false;
        }
        foreach (string key in new[] { "label", "createdAt", "avatarName" })
            if (data.ContainsKey(key) && !(data[key] is string) && data[key] != null)
            {
                error = key + " must be a string when present";
                return false;
            }

        foreach (var folder in (List<object>)data["virtualFolders"])
            if (!(folder is string)) { error = "virtualFolders contains a non-string entry"; return false; }

        var componentEntries = (List<object>)data["vrcfComponents"];
        foreach (var item in componentEntries)
        {
            var component = item as Dictionary<string, object>;
            if (component == null) { error = "vrcfComponents contains a non-object entry"; return false; }
            if (!component.TryGetValue("path", out var path) || !(path is string))
            { error = "a VRCFury component is missing its path"; return false; }
            if (!HasNonEmptyString(component, "typeName"))
            { error = "a VRCFury component is missing typeName"; return false; }
            if (!component.TryGetValue("order", out var order) || !IsJsonNumber(order))
            { error = "a VRCFury component is missing its numeric order"; return false; }
            if (!HasNonEmptyString(component, "json"))
            { error = "a VRCFury component has empty serialized data"; return false; }
            try
            {
                if (!(MiniJson.Parse(MiniJson.Str(component, "json")) is Dictionary<string, object>))
                { error = "a VRCFury component contains invalid serialized JSON"; return false; }
            }
            catch (System.Exception ex)
            { error = "a VRCFury component contains invalid serialized JSON: " + ex.Message; return false; }
            if (!component.TryGetValue("refs", out var refs) || !(refs is Dictionary<string, object>))
            { error = "a VRCFury component is missing its reference table"; return false; }
            foreach (var reference in ((Dictionary<string, object>)refs).Values)
                if (!IsReferenceDescriptor(reference))
                { error = "a VRCFury component contains a malformed reference descriptor"; return false; }
        }

        var menuEntries = (List<object>)data["menuAssets"];
        var menuKeys = new HashSet<string>();
        foreach (var item in menuEntries)
        {
            var menu = item as Dictionary<string, object>;
            if (menu == null) { error = "menuAssets contains a non-object entry"; return false; }
            if (!menu.ContainsKey("controls") || !(menu["controls"] is List<object>))
            { error = "a menu asset is missing its controls array"; return false; }
            string menuKind = MiniJson.Str(menu, "kind");
            if (menu.ContainsKey("kind") && menuKind != "asset" && menuKind != "nonasset")
            { error = "a menu asset has an unsupported identity kind"; return false; }
            if (!menu.ContainsKey("kind") && !menu.ContainsKey("guid") && !menu.ContainsKey("localId"))
            { error = "a menu asset has no identity fields"; return false; }
            if (menuKind == "nonasset" &&
                !HasNonEmptyString(menu, "snapshotId"))
            { error = "a non-asset menu is missing snapshotId"; return false; }
            if (menuKind != "nonasset" && !HasValidAssetIdentity(menu))
            { error = "an asset menu is missing a valid GUID/localId identity"; return false; }
            if (menu.ContainsKey("name") && !(menu["name"] is string))
            { error = "a menu asset has a non-string name"; return false; }
            if (!menuKeys.Add(MenuDescriptorKey(menu)))
            { error = "menuAssets contains duplicate identities"; return false; }

            foreach (var controlItem in (List<object>)menu["controls"])
            {
                var control = controlItem as Dictionary<string, object>;
                if (control == null) { error = "a menu contains a non-object control"; return false; }
                if (!control.TryGetValue("name", out var controlName) || !(controlName is string))
                { error = "a menu control is missing its string name"; return false; }
                if (!control.TryGetValue("type", out var controlType) || !(controlType is string) ||
                    !System.Enum.TryParse((string)controlType, out VRCExpressionsMenu.Control.ControlType parsedType) ||
                    !System.Enum.IsDefined(typeof(VRCExpressionsMenu.Control.ControlType), parsedType))
                { error = "a menu control has a missing or undefined type"; return false; }
                if (!control.TryGetValue("style", out var style) || !IsJsonNumber(style))
                { error = "a menu control is missing its numeric style"; return false; }
                if (!control.TryGetValue("value", out var value) || !IsJsonNumber(value))
                { error = "a menu control is missing its numeric value"; return false; }
                if (!control.TryGetValue("parameter", out var parameterName) || !(parameterName is string))
                { error = "a menu control is missing its string parameter"; return false; }
                foreach (string referenceKey in new[] { "icon", "subMenu" })
                    if (!control.TryGetValue(referenceKey, out var reference) || !IsReferenceDescriptor(reference))
                    { error = "a menu control has missing or malformed " + referenceKey + " data"; return false; }
                if (!control.TryGetValue("subParameters", out var subParameters) || !(subParameters is List<object>))
                { error = "a menu control is missing its subParameters array"; return false; }
                foreach (var parameter in (List<object>)subParameters)
                    if (!(parameter is string))
                    { error = "a menu control has a non-string subParameter"; return false; }
                if (!control.TryGetValue("labels", out var labels) || !(labels is List<object>))
                { error = "a menu control is missing its labels array"; return false; }
                foreach (var labelItem in (List<object>)labels)
                {
                    var label = labelItem as Dictionary<string, object>;
                    if (label == null || !label.TryGetValue("name", out var labelName) || !(labelName is string) ||
                        !label.TryGetValue("icon", out var labelIcon) || !IsReferenceDescriptor(labelIcon))
                    { error = "a menu control has malformed label data"; return false; }
                }
            }
            if (menu.ContainsKey("parametersAsset") && !IsReferenceDescriptor(menu["parametersAsset"]))
            { error = "a menu has malformed parametersAsset data"; return false; }
        }

        var menusByKey = menuEntries.Cast<Dictionary<string, object>>()
            .ToDictionary(MenuDescriptorKey, entry => entry);
        // Every submenu must point at a captured menu. This is checked before
        // any restore mutation; cycles are valid and are handled by the graph
        // walk below with a visited set.
        foreach (var menu in menusByKey.Values)
            foreach (var controlItem in (List<object>)menu["controls"])
            {
                var control = (Dictionary<string, object>)controlItem;
                var submenu = MiniJson.Dict(control, "subMenu");
                if (submenu != null && !menusByKey.ContainsKey(MenuDescriptorKey(submenu)))
                {
                    error = "a subMenu reference does not exist in menuAssets";
                    return false;
                }
            }

        // VRChat's native descriptor graph is limited to eight controls per
        // menu. VRCFury Full Controller menus are allowed to exceed eight
        // because VRCFury paginates/merges them; only enforce the limit for
        // menus reachable from the explicit native descriptor root.
        if (data.ContainsKey("nativeMenu") && data["nativeMenu"] != null)
        {
            var native = MiniJson.Dict(data, "nativeMenu");
            if (native == null || !menusByKey.TryGetValue(MenuDescriptorKey(native), out var rootMenu))
            {
                error = "nativeMenu does not identify a menu in menuAssets";
                return false;
            }
            {
                var pending = new Stack<Dictionary<string, object>>();
                var seen = new HashSet<string>();
                pending.Push(rootMenu);
                while (pending.Count > 0)
                {
                    var menu = pending.Pop();
                    string key = MenuDescriptorKey(menu);
                    if (!seen.Add(key)) continue;
                    var controls = (List<object>)menu["controls"];
                    if (controls.Count > 8)
                    {
                        error = "the native expression-menu graph contains a menu with more than 8 controls";
                        return false;
                    }
                    foreach (var controlItem in controls)
                    {
                        var control = controlItem as Dictionary<string, object>;
                        // An orphan reference on a non-SubMenu control is
                        // preserved as snapshot data, not a buildable native
                        // graph edge. Match the SDK/scanner traversal rule.
                        var submenu = control != null && MiniJson.Str(control, "type") == "SubMenu"
                            ? MiniJson.Dict(control, "subMenu") : null;
                        if (submenu != null && menusByKey.TryGetValue(MenuDescriptorKey(submenu), out var child))
                            pending.Push(child);
                    }
                }
            }
        }
        return true;
    }

    void RestoreSnapshotInteractive(string filePath, bool removeUntrackedAdditions = false)
    {
        if (!avatar || string.IsNullOrEmpty(filePath)) return;

        Dictionary<string, object> data;
        try { data = MiniJson.AsDict(MiniJson.Parse(File.ReadAllText(ProjectToAbsolute(filePath)))); }
        catch (System.Exception ex)
        {
            EditorUtility.DisplayDialog("Snapshot unreadable", ex.Message, "OK");
            return;
        }
        if (data == null) { EditorUtility.DisplayDialog("Snapshot unreadable", "Empty or malformed file.", "OK"); return; }
        if (!ValidateSnapshotPayload(data, out string schemaError))
        {
            EditorUtility.DisplayDialog("Snapshot rejected",
                "This snapshot was not changed because its schema is incomplete or unsupported:\n\n" +
                schemaError, "OK");
            return;
        }

        string label = MiniJson.Str(data, "label", "(no label)");
        string when  = MiniJson.Str(data, "createdAt", "?");
        int compCount = MiniJson.List(data, "vrcfComponents").Count;
        int menuCount = MiniJson.List(data, "menuAssets").Count;

        // The snapshot records which avatar it was taken FROM. If that doesn't
        // match the avatar it's about to be applied TO, every object
        // reference in it resolves against the wrong hierarchy — content that
        // doesn't match silently skips (no exception), and content that
        // happens to coincidentally match can land somewhere structurally
        // wrong. That reads as "restore silently did almost nothing" with
        // zero reported problems, which is worse than an outright failure.
        // This can happen even for what looks like "the same" avatar: this
        // plugin's own prefab-unpacking can sever the avatar's OWN prefab
        // connection mid-session, and a re-imported/re-downloaded package can
        // legitimately change the underlying prefab asset's GUID between
        // sessions.
        string snapshotKey = MiniJson.Str(data, "avatarKey", "");
        string currentKey  = AvatarKey();
        bool missingSnapshotIdentity = string.IsNullOrWhiteSpace(snapshotKey);
        bool missingCurrentIdentity = string.IsNullOrWhiteSpace(currentKey);
        if (missingSnapshotIdentity || missingCurrentIdentity)
        {
            string reason = missingSnapshotIdentity && missingCurrentIdentity
                ? "The snapshot and the currently selected avatar both lack a durable identity."
                : missingSnapshotIdentity
                    ? "The snapshot does not record which avatar it came from."
                    : "The currently selected avatar has no durable identity (it may be an unsaved scene object).";
            Debug.LogWarning("[EASY] Snapshot restore has missing avatar identity; explicit confirmation is required.");
            if (!EditorUtility.DisplayDialog("Snapshot identity is missing",
                $"\"{label}\" (taken {when}) cannot be matched safely. {reason} " +
                "Object references may resolve against the wrong hierarchy. Continue only if you have verified the target avatar manually.",
                "Restore Anyway", "Cancel")) return;
        }
        else if (!string.IsNullOrEmpty(currentKey) && snapshotKey != currentKey)
        {
            Debug.LogWarning($"[EASY] Snapshot avatar-key mismatch: this snapshot was taken from key " +
                             $"'{snapshotKey}' but the currently selected avatar's key is '{currentKey}'. " +
                             "Restoring it would resolve object references against a different hierarchy than " +
                             "the one that made them.");
            if (!EditorUtility.DisplayDialog("This snapshot is from a different avatar identity",
                $"\"{label}\" (taken {when}) was captured from a different avatar identity than the one currently " +
                "selected — possibly the same avatar after a reimport, or a genuinely different one.\n\n" +
                "Restoring it anyway will likely leave most content unmatched (silently skipped, not an error) " +
                "and may place anything that DOES match in the wrong part of the tree.\n\n" +
                "Only continue if you're confident this is really the same avatar.",
                "Restore Anyway", "Cancel")) return;
        }

        if (!EditorUtility.DisplayDialog("Restore snapshot?",
            $"\"{label}\"\nTaken {when}\n\n" +
            $"This rewrites {compCount} VRCFury component(s) and {menuCount} menu asset(s) back to how they " +
            "were and re-creates deleted components. " +
            (removeUntrackedAdditions
                ? "ALL unmatched VRCFury components will be removed, including manual additions and duplicates created by older plugin versions.\n\n"
                : "Components created by this tool since the snapshot are removed; other VRCFury components added since are kept and listed.\n\n") +
            "Undo with Ctrl+Z.",
            "Restore", "Cancel")) return;

        if (!data.ContainsKey("nativeMenu"))
        {
            Debug.LogWarning("[EASY] Legacy snapshot has no nativeMenu; the current descriptor menu will be preserved.");
            if (!EditorUtility.DisplayDialog("Legacy snapshot: native menu preserved",
                "This older snapshot has no recorded native expression-menu root. Its native descriptor menu will not be changed; only captured menu assets and components will be restored.",
                "Continue", "Cancel")) return;
        }

        SnapshotExpansionStates(root);
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Restore snapshot");
        int grp = Undo.GetCurrentGroup();

        var problems = new List<string>();
        var problemEntries = new List<object>();
        bool restored = LogOp("Restore", filePath, "Snapshot", "", label, () =>
        {
            try
            {
                RestoreMenuAssets(data, problems);
                RestoreVrcfComponents(data, problems, removeUntrackedAdditions);
            }
            finally { restoredMenuCopies.Clear(); }
            RestoreVirtualFolders(data);
            problemEntries.AddRange(problems.Cast<object>());
            AssetDatabase.SaveAssets();
            QueueRescan();
        }, new Dictionary<string, object>
        {
            ["snapshotFile"] = filePath,
            ["createdAt"]    = when,
            ["problems"] = problemEntries,
        });

        Undo.CollapseUndoOperations(grp);
        FlushRescan();
        if (!restored) return;

        if (problems.Count > 0)
        {
            // one line per distinct problem (an object with two VRCFury
            // components used to be reported twice, word for word)
            var lines = problems.GroupBy(p => p)
                                .Select(g => g.Count() > 1 ? $"{g.Key} (×{g.Count()})" : g.Key);
            Debug.LogWarning("[EASY] Restore finished with " + problems.Count + " issue(s):\n- " +
                             string.Join("\n- ", lines));
            EditorUtility.DisplayDialog("Restored with warnings",
                $"The snapshot was applied, but {problems.Count} thing(s) could not be fully restored. " +
                "See the Console for details.", "OK");
        }
        else ShowNotification(new GUIContent("Snapshot restored"));
    }

    void RestoreVirtualFolders(Dictionary<string, object> data)
    {
        virtualFolderPaths = MiniJson.List(data, "virtualFolders")
            .Select(o => EEMPath.Norm(o as string ?? ""))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        SaveVirtualFolders();
    }

    readonly Dictionary<string, VRCExpressionsMenu> restoredMenuCopies = new Dictionary<string, VRCExpressionsMenu>();
    static string MenuDescriptorKey(Dictionary<string, object> descriptor)
    {
        if (descriptor == null) return "";
        if (MiniJson.Str(descriptor, "kind") == "nonasset")
            return "nonasset:" + MiniJson.Str(descriptor, "snapshotId");
        return MiniJson.Str(descriptor, "guid") + ":" + MiniJson.Str(descriptor, "localId");
    }

    void RestoreMenuAssets(Dictionary<string, object> data, List<string> problems)
    {
        restoredMenuCopies.Clear();
        // Allocate every private asset before resolving links, including cycles.
        foreach (var obj in MiniJson.List(data, "menuAssets"))
        {
            var entry = obj as Dictionary<string, object>;
            if (entry == null) continue;
            var source = ResolveDescriptor(entry) as VRCExpressionsMenu;
            var template = source ? source : CreateInstance<VRCExpressionsMenu>();
            if (!source) template.name = MiniJson.Str(entry, "name", "Restored Menu");
            try { restoredMenuCopies[MenuDescriptorKey(entry)] = CloneMenuAsset(template); }
            finally { if (!source) DestroyImmediate(template); }
        }
        foreach (var entryObj in MiniJson.List(data, "menuAssets"))
        {
            var entry = entryObj as Dictionary<string, object>;
            if (entry == null) continue;
            var menu = restoredMenuCopies[MenuDescriptorKey(entry)];

            Undo.RecordObject(menu, "Restore menu");
            if (entry.ContainsKey("name")) menu.name = MiniJson.Str(entry, "name");
            // Older snapshots never captured this reference. Preserve it when
            // reading those files rather than clearing it during restoration.
            if (entry.ContainsKey("parametersAsset"))
                menu.Parameters = ResolveMenuReference<VRCExpressionParameters>(MiniJson.Dict(entry, "parametersAsset"),
                    $"menu '{menu.name}' parameters", problems);
            menu.controls.Clear();
            foreach (var cObj in MiniJson.List(entry, "controls"))
            {
                var c = cObj as Dictionary<string, object>;
                if (c == null) continue;
                string context = $"menu '{menu.name}' control #{menu.controls.Count + 1} '{MiniJson.Str(c, "name")}'";
                var control = new VRCExpressionsMenu.Control
                {
                    name  = MiniJson.Str(c, "name"),
                    value = MiniJson.Float(c, "value"),
                    icon  = ResolveMenuReference<Texture2D>(MiniJson.Dict(c, "icon"), context + " icon", problems),
                    subMenu = ResolveMenuReference<VRCExpressionsMenu>(MiniJson.Dict(c, "subMenu"), context + " submenu", problems),
                };
                if (System.Enum.TryParse(MiniJson.Str(c, "type"), out VRCExpressionsMenu.Control.ControlType ct))
                    control.type = ct;
                ApplyStyle(control, MiniJson.Int(c, "style"));
                string paramName = MiniJson.Str(c, "parameter");
                control.parameter = new VRCExpressionsMenu.Control.Parameter { name = paramName };
                control.subParameters = MiniJson.List(c, "subParameters")
                    .Select(o => new VRCExpressionsMenu.Control.Parameter { name = o as string ?? "" }).ToArray();
                control.labels = MiniJson.List(c, "labels").Select((o, index) =>
                {
                    var ld = o as Dictionary<string, object>;
                    return new VRCExpressionsMenu.Control.Label
                    {
                        name = MiniJson.Str(ld, "name"),
                        icon = ResolveMenuReference<Texture2D>(MiniJson.Dict(ld, "icon"), context + $" label #{index + 1} icon", problems),
                    };
                }).ToArray();
                menu.controls.Add(control);
            }
            EditorUtility.SetDirty(menu);
        }
        var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
        if (!descriptor) return;
        if (!data.ContainsKey("nativeMenu"))
        {
            problems.Add("legacy snapshot has no nativeMenu; the current avatar native menu was preserved");
            return;
        }
        Dictionary<string, object> native = MiniJson.Dict(data, "nativeMenu");
        Undo.RecordObject(descriptor, "Restore avatar expression references");
        descriptor.expressionsMenu = ResolveMenuReference<VRCExpressionsMenu>(native, "native menu", problems);
        if (data.ContainsKey("expressionParameters"))
            descriptor.expressionParameters = ResolveMenuReference<VRCExpressionParameters>(
                MiniJson.Dict(data, "expressionParameters"), "avatar expression parameters", problems);
        PrefabUtility.RecordPrefabInstancePropertyModifications(descriptor);
        EditorUtility.SetDirty(descriptor);
    }

    T ResolveMenuReference<T>(Dictionary<string, object> descriptor, string context, List<string> problems) where T : Object
    {
        var resolved = ResolveDescriptor(descriptor) as T;
        if (descriptor != null && resolved == null)
            problems.Add(context + " could not be restored (asset " + MiniJson.Str(descriptor, "guid", "unknown") +
                ", local id " + MiniJson.Str(descriptor, "localId", "unknown") + ").");
        return resolved;
    }

    // ── VRCFury components ────────────────────────────────────────────────────
    //
    // Each component in the snapshot is matched to a live one in tiers, most
    // certain first:
    //   1. stable id (GlobalObjectId) — survives renames and moves
    //   2. hierarchy path + type + order — the original rule
    // Name-only fallback is deliberately not used: a unique name can still
    // identify an unrelated object after an avatar edit.
    // Matched components are rewritten to the snapshot's contents.
    //
    // Unmatched plugin additions and legacy manager records are removed.
    // Untracked additions are kept unless exact restore was selected.
    //
    // Snapshot components with no live match are re-created on their object,
    // found by stable object id or exact hierarchy/path only.

    class SnapComp
    {
        public Dictionary<string, object> data;
        public string path, typeName, gid, objGid, objName, parentName;
        public int order;
        public bool used;
    }

    bool CanUseSnapshotFallback(Component component, SnapComp snap)
    {
        // Reusing a tool-created duplicate/restored component is safe. A manual
        // replacement on the same object must not inherit the deleted one's data.
        if (IsPluginAddition(component)) return true;
        int instance = MiniJson.Int(snap.data, "componentInstanceId");
        if (instance != 0 && MiniJson.Str(snap.data, "componentSession") == OwnershipSession &&
            (string.IsNullOrEmpty(snap.objGid) || StableId(component.gameObject) == snap.objGid))
            return component.GetInstanceID() == instance;
        // On an unchanged saved object, a different stable component id means
        // this is a replacement. After prefab unpacking the object id changes,
        // so exact hierarchy/path fallback remains available.
        return string.IsNullOrEmpty(snap.gid) || string.IsNullOrEmpty(snap.objGid) ||
            StableId(component.gameObject) != snap.objGid || StableId(component) == snap.gid;
    }

    void RestoreVrcfComponents(Dictionary<string, object> data, List<string> problems, bool removeUntrackedAdditions = false)
    {
        var snaps = new List<SnapComp>();
        foreach (var o in MiniJson.List(data, "vrcfComponents"))
        {
            if (!(o is Dictionary<string, object> e)) continue;
            string path = MiniJson.Str(e, "path");
            snaps.Add(new SnapComp
            {
                data = e, path = path, typeName = MiniJson.Str(e, "typeName"), order = MiniJson.Int(e, "order"),
                gid = MiniJson.Str(e, "gid"), objGid = MiniJson.Str(e, "objGid"),
                objName = MiniJson.Str(e, "objName", LeafOfPath(path)), parentName = MiniJson.Str(e, "parentName"),
            });
        }

        var live    = EnumerateVrcfComponents().ToList();
        var matched = new Dictionary<Component, SnapComp>();

        // 1 — stable id
        var byGid = snaps.Where(x => !string.IsNullOrEmpty(x.gid)).GroupBy(x => x.gid)
                         .ToDictionary(g => g.Key, g => g.First());
        foreach (var l in live)
            if (byGid.TryGetValue(StableId(l.comp), out var sc) && !sc.used && sc.typeName == l.typeName)
            { matched[l.comp] = sc; sc.used = true; }

        // Instance identity survives component reordering and prefab unpacking
        // during this editor session, including an as-yet-unsaved scene.
        foreach (var l in live)
        {
            if (matched.ContainsKey(l.comp)) continue;
            var sc = snaps.FirstOrDefault(x => !x.used && x.typeName == l.typeName &&
                MiniJson.Str(x.data, "componentSession") == OwnershipSession &&
                MiniJson.Int(x.data, "componentInstanceId") == l.comp.GetInstanceID());
            if (sc != null) { matched[l.comp] = sc; sc.used = true; }
        }

        // 2 — path + type + order (lists: same-named siblings no longer overwrite each other)
        foreach (var l in live)
        {
            if (matched.ContainsKey(l.comp)) continue;
            var sc = snaps.FirstOrDefault(x => !x.used && x.typeName == l.typeName && x.order == l.order && CanUseSnapshotFallback(l.comp, x) &&
                (ResolveHierarchy(x.data) is Transform exact ? exact == l.comp.transform : x.path == l.path));
            if (sc != null) { matched[l.comp] = sc; sc.used = true; }
        }

        // apply matches; settle the rest
        var kept = new List<string>();
        foreach (var l in live)
        {
            if (matched.ContainsKey(l.comp)) continue;
            if (removeUntrackedAdditions || IsPluginAddition(l.comp) || IsToolRecord(l.comp)) { if (l.comp) Undo.DestroyObjectImmediate(l.comp); }
            else kept.Add($"{ShortTypeName(l.typeName)} on '{l.path}'");
        }
        if (kept.Count > 0)
            problems.Add($"{kept.Count} VRCFury component(s) added since the snapshot were kept rather than deleted: " +
                         string.Join(", ", kept.Take(6)) + (kept.Count > 6 ? ", …" : ""));

        // re-create what's missing
        foreach (var sc in snaps.Where(x => !x.used))
        {
            var t = LocateSnapshotObject(sc);
            if (t == null)
            {
                problems.Add($"'{sc.path}' no longer exists, so its {ShortTypeName(sc.typeName)} could not be restored");
                continue;
            }
            var type = ResolveTypeByFullName(sc.typeName);
            if (type == null) { problems.Add($"type {sc.typeName} is not loaded — component on '{sc.path}' skipped"); continue; }
            var comp = Undo.AddComponent(t.gameObject, type);
            if (comp == null) { problems.Add($"could not add {ShortTypeName(sc.typeName)} to '{sc.path}'"); continue; }
            RegisterPluginAddition(comp);
            matched[comp] = sc;
            if (RelativeHierarchyPath(t) != sc.path)
                problems.Add($"{ShortTypeName(sc.typeName)} for '{sc.path}' was restored onto '{RelativeHierarchyPath(t)}' (the object moved or was renamed)");
        }
        // Re-create every component before linking references between them.
        // Put components back in snapshot order so legacy type/order references
        // also resolve to the restored component rather than an added neighbour.
        foreach (var group in matched.GroupBy(pair => (pair.Key.gameObject, pair.Key.GetType())))
        {
            Undo.RegisterCompleteObjectUndo(group.Key.Item1, "Restore component order");
            foreach (var pair in group.OrderByDescending(pair => pair.Value.order))
                while (UnityEditorInternal.ComponentUtility.MoveComponentUp(pair.Key)) { }
        }
        foreach (var pair in matched) ApplyComponentJson(pair.Key, pair.Value.data, problems);
    }

    /// The object a snapshot component belongs on: stable id, then exact
    /// hierarchy/path. A name-only fallback is intentionally not used because
    /// a unique name can still belong to an unrelated object after edits.
    Transform LocateSnapshotObject(SnapComp sc)
    {
        if (!string.IsNullOrEmpty(sc.objGid) && FromStableId(sc.objGid) is GameObject g && avatar &&
            g.transform.IsChildOf(avatar.transform))
            return g.transform;

        return ResolveHierarchy(sc.data) ?? FindTransformByRelativePath(sc.path);
    }

    /// Move / icon records this tool writes. Only these are removed when they
    /// weren't in the snapshot.
    bool IsToolRecord(Component c)
    {
        if (!c) return false;
        if (!avatar || c.transform.parent != avatar.transform || c.gameObject.name != MANAGER_OBJECT_NAME) return false;
        string feature = new SerializedObject(c).FindProperty("content")?.managedReferenceFullTypename ?? "";
        // Recognize legacy records on the plugin's dedicated manager. Other
        // additions, including manual toggles placed there, require ownership.
        return feature.Contains("MoveMenuItem") || feature.Contains("SetIcon") || feature.Contains("OverrideMenuIcon");
    }

    static string LeafOfPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int i = path.LastIndexOf('/');
        return i >= 0 ? path.Substring(i + 1) : path;
    }

    static string StableId(Object o)
    {
        if (!o) return "";
        try { return ValidOwnerId(o); } catch { return ""; }
    }

    static Object FromStableId(string id)
    {
        if (string.IsNullOrEmpty(id) || !GlobalObjectId.TryParse(id, out var gid)) return null;
        try { return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid); } catch { return null; }
    }

    void ApplyComponentJson(Component comp, Dictionary<string, object> snap, List<string> problems)
    {
        if (comp == null) return;
        string json = MiniJson.Str(snap, "json");
        if (string.IsNullOrEmpty(json)) return;
        var refs = MiniJson.Dict(snap, "refs");
        string remapped = RemapInstanceIds(json, refs, problems);
        try
        {
            Undo.RecordObject(comp, "Restore component");
            EditorJsonUtility.FromJsonOverwrite(remapped, comp);
            PrefabUtility.RecordPrefabInstancePropertyModifications(comp);
            EditorUtility.SetDirty(comp);
        }
        catch (System.Exception ex)
        {
            problems.Add($"{ShortTypeName(comp.GetType().FullName)} on '{MiniJson.Str(snap, "path")}': {ex.Message}");
        }
    }

    static string ShortTypeName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return "component";
        int i = fullName.LastIndexOf('.');
        return i >= 0 ? fullName.Substring(i + 1) : fullName;
    }

    static System.Type ResolveTypeByFullName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName, false);
            if (t != null) return t;
        }
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            foreach (var t in SafeTypes(asm))
                if ((t.FullName ?? "") == fullName) return t;
        return null;
    }

}
