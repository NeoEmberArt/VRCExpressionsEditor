// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — snapshot capture
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

    string WriteSnapshot(string label, bool isBaseline, bool automatic = false)
    {
        if (!avatar) return null;
        RefreshAvatarIdentity();
        AssetDatabase.SaveAssetIfDirty(AvatarState());
        string dir = SnapshotFolder();
        if (dir == null) return null;

        var payload = BuildSnapshotPayload(label, isBaseline);
        string safeLabel = string.Join("_", (label ?? "snapshot").Split(Path.GetInvalidFileNameChars()));
        if (safeLabel.Length > 40) safeLabel = safeLabel.Substring(0, 40);
        string prefix = isBaseline ? "baseline_" : automatic ? "auto_" : "snap_";
        string file = dir + "/" + prefix + System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + safeLabel + ".json";
        file = AssetDatabase.GenerateUniqueAssetPath(file);

        File.WriteAllText(ProjectToAbsolute(file), MiniJson.Pretty(payload));
        AssetDatabase.ImportAsset(file);
        if (automatic) PruneAutomaticSnapshots(dir);
        return file;
    }

    Dictionary<string, object> BuildSnapshotPayload(string label, bool isBaseline)
    {
        var comps = new List<object>();
        foreach (var entry in EnumerateVrcfComponents())
            comps.Add(CaptureComponent(entry.comp, entry.path, entry.typeName, entry.order));

        var menus = new List<object>();
        foreach (var menu in EnumerateReachableMenus())
            menus.Add(CaptureMenuAsset(menu));

        return new Dictionary<string, object>
        {
            ["format"]         = SNAPSHOT_FORMAT,
            ["pluginVersion"]  = ResolveVersion(),
            ["createdAt"]      = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["label"]          = label ?? "",
            ["isBaseline"]     = isBaseline,
            ["avatarName"]     = avatar ? avatar.name : null,
            ["avatarKey"]      = AvatarKey(),
            ["virtualFolders"] = virtualFolderPaths.Cast<object>().ToList(),
            ["vrcfComponents"] = comps,
            ["menuAssets"]     = menus,
            ["nativeMenu"]     = AssetRefDescriptor(avatar.GetComponent<VRCAvatarDescriptor>()?.expressionsMenu),
        };
    }

    struct VrcfEntry { public Component comp; public string path; public string typeName; public int order; }

    IEnumerable<VrcfEntry> EnumerateVrcfComponents()
    {
        var result = new List<VrcfEntry>();
        if (!avatar) return result;

        void Walk(Transform t)
        {
            string path = RelativeHierarchyPath(t);
            var counters = new Dictionary<string, int>(System.StringComparer.Ordinal);
            foreach (var comp in t.GetComponents<Component>())
            {
                if (!comp || comp is Transform) continue;
                var type = comp.GetType();
                string fn = type.FullName ?? "";
                if (!fn.Contains("VRCFury") && !fn.Contains("VF.")) continue;
                counters.TryGetValue(fn, out int order);
                counters[fn] = order + 1;
                result.Add(new VrcfEntry { comp = comp, path = path, typeName = fn, order = order });
            }
            foreach (Transform c in t) Walk(c);
        }
        Walk(avatar.transform);
        return result;
    }

    string RelativeHierarchyPath(Transform t)
    {
        if (t == null || avatar == null) return "";
        if (t == avatar.transform) return "";
        var parts = new List<string>();
        for (var c = t; c != null && c != avatar.transform; c = c.parent) parts.Insert(0, c.name);
        return string.Join("/", parts);
    }

    Dictionary<string, object> CaptureComponent(Component comp, string path, string typeName, int order)
    {
        string json = "";
        try { json = EditorJsonUtility.ToJson(comp); }
        catch (System.Exception ex) { Debug.LogWarning($"[EASY] Could not serialise {typeName} on '{path}': {ex.Message}"); }

        // instanceIDs are only meaningful inside this editor session — swap them
        // for descriptors that survive a restart.
        var refs = new Dictionary<string, object>();
        foreach (Match m in InstanceIdPattern.Matches(json))
        {
            string idStr = m.Groups[1].Value;
            if (refs.ContainsKey(idStr)) continue;
            if (!int.TryParse(idStr, out int id) || id == 0) continue;
            var descriptor = DescribeObjectReference(id);
            if (descriptor != null) refs[idStr] = descriptor;
        }

        // Identity that survives the object being renamed or moved, so restore
        // can still find it. (Unpacking a prefab changes these ids; the
        // object-name fallback in restore covers that case.)
        var go = comp ? comp.gameObject : null;
        return new Dictionary<string, object>
        {
            ["path"]       = path,
            ["typeName"]   = typeName,
            ["order"]      = order,
            ["json"]       = json,
            ["refs"]       = refs,
            ["gid"]        = StableId(comp),
            ["componentSession"] = OwnershipSession,
            ["componentInstanceId"] = comp ? comp.GetInstanceID() : 0,
            ["objGid"]     = StableId(go),
            ["objName"]    = go ? go.name : "",
            ["parentName"] = go && go.transform.parent ? go.transform.parent.name : "",
            ["hierarchy"]  = CaptureHierarchy(go ? go.transform : null),
        };
    }

    Dictionary<string, object> DescribeObjectReference(int instanceId)
    {
        var obj = EditorUtility.InstanceIDToObject(instanceId);
        if (obj == null) return null;

        if (EditorUtility.IsPersistent(obj))
        {
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long localId))
                return new Dictionary<string, object>
                {
                    ["kind"] = "asset",
                    ["guid"] = guid,
                    ["localId"] = localId.ToString(),
                    ["type"] = obj.GetType().FullName,
                    ["name"] = obj.name,
                };
            return null;
        }

        Transform t = obj is GameObject go ? go.transform : (obj is Component c ? c.transform : null);
        if (t == null) return null;

        var d = new Dictionary<string, object>
        {
            ["kind"] = obj is GameObject ? "sceneObject" : "sceneComponent",
            ["path"] = RelativeHierarchyPath(t),
            ["type"] = obj.GetType().FullName,
            ["name"] = obj.name,
            ["gid"] = StableId(obj),
            ["hierarchy"] = CaptureHierarchy(t),
        };
        if (obj is Component comp2)
        {
            int idx = 0;
            foreach (var other in t.GetComponents<Component>())
            {
                if (other == comp2) break;
                if (other != null && other.GetType() == comp2.GetType()) idx++;
            }
            d["order"] = idx;
        }
        return d;
    }

    IEnumerable<VRCExpressionsMenu> EnumerateReachableMenus()
    {
        var found = new List<VRCExpressionsMenu>();
        var seen = new HashSet<VRCExpressionsMenu>();
        if (!avatar) return found;

        void Collect(VRCExpressionsMenu m, int depth)
        {
            var pending = new Stack<VRCExpressionsMenu>();
            if (m) pending.Push(m);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!current || !seen.Add(current)) continue;
                found.Add(current);
                foreach (var c in current.controls)
                    if (c?.subMenu) pending.Push(c.subMenu);
            }
        }

        var desc = avatar.GetComponent<VRCAvatarDescriptor>();
        if (desc?.expressionsMenu != null) Collect(desc.expressionsMenu, 0);

        foreach (var comp in avatar.GetComponentsInChildren<Component>(true))
        {
            if (!comp || comp is Transform) continue;
            string fn = comp.GetType().FullName ?? "";
            if (!fn.Contains("VRCFury") && !fn.Contains("VF.")) continue;
            var so = new SerializedObject(comp);
            var menusProp = so.FindProperty("content.menus");
            if (menusProp == null || !menusProp.isArray) continue;
            for (int i = 0; i < menusProp.arraySize; i++)
            {
                var objRef = menusProp.GetArrayElementAtIndex(i).FindPropertyRelative("menu.objRef");
                if (objRef?.objectReferenceValue is VRCExpressionsMenu m) Collect(m, 0);
            }
        }
        return found;
    }

    Dictionary<string, object> CaptureMenuAsset(VRCExpressionsMenu menu)
    {
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(menu, out string guid, out long localId);
        var controls = new List<object>();
        foreach (var c in menu.controls)
        {
            if (c == null) continue;
            controls.Add(new Dictionary<string, object>
            {
                ["name"]          = c.name ?? "",
                ["type"]          = c.type.ToString(),
                ["style"]         = StyleToInt(c),
                ["value"]         = c.value,
                ["parameter"]     = c.parameter?.name ?? "",
                ["icon"]          = AssetRefDescriptor(c.icon),
                ["subMenu"]       = AssetRefDescriptor(c.subMenu),
                ["subParameters"] = (c.subParameters ?? new VRCExpressionsMenu.Control.Parameter[0])
                                        .Select(p => (object)(p?.name ?? "")).ToList(),
                // labels are Label objects (name + icon), not plain strings
                ["labels"]        = (c.labels ?? new VRCExpressionsMenu.Control.Label[0])
                                        .Select(l => (object)new Dictionary<string, object>
                                        {
                                            ["name"] = l.name ?? "",
                                            ["icon"] = AssetRefDescriptor(l.icon),
                                        }).ToList(),
            });
        }
        return new Dictionary<string, object>
        {
            ["guid"]     = guid,
            ["localId"]  = localId.ToString(),
            ["name"]     = menu.name,
            ["path"]     = AssetDatabase.GetAssetPath(menu),
            ["parametersAsset"] = AssetRefDescriptor(menu.Parameters),
            ["controls"] = controls,
        };
    }

    /// The SDK's control-style enum has changed name between SDK versions, so it
    /// is read and written by reflection rather than by naming the type.
    static readonly System.Reflection.FieldInfo StyleField =
        typeof(VRCExpressionsMenu.Control).GetField("style");

    static int StyleToInt(VRCExpressionsMenu.Control c)
    {
        if (StyleField == null || c == null) return 0;
        try { return System.Convert.ToInt32(StyleField.GetValue(c)); } catch { return 0; }
    }

    static void ApplyStyle(VRCExpressionsMenu.Control c, int value)
    {
        if (StyleField == null || c == null) return;
        try
        {
            object boxed = StyleField.FieldType.IsEnum
                ? System.Enum.ToObject(StyleField.FieldType, value)
                : System.Convert.ChangeType(value, StyleField.FieldType);
            StyleField.SetValue(c, boxed);
        }
        catch { }
    }

    Dictionary<string, object> AssetRefDescriptor(Object obj)
    {
        if (obj == null) return null;
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long localId)) return null;
        return new Dictionary<string, object>
        {
            ["kind"] = "asset",   // ResolveDescriptor branches on this — without it, every icon/subMenu
                                  // reference captured through here silently resolved to null on restore.
            ["guid"] = guid,
            ["localId"] = localId.ToString(),
            ["type"] = obj.GetType().FullName,
        };
    }

}
