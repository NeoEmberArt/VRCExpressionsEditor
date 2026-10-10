// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — object-reference resolution
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
    List<object> CaptureHierarchy(Transform transform)
    {
        var result = new List<object>();
        if (!avatar || !transform || !transform.IsChildOf(avatar.transform)) return null;
        for (var current = transform; current != avatar.transform; current = current.parent)
            result.Insert(0, new Dictionary<string, object>
            {
                ["name"] = current.name, ["index"] = current.GetSiblingIndex(),
            });
        return result;
    }

    Transform ResolveHierarchy(Dictionary<string, object> descriptor)
    {
        if (!avatar || descriptor == null || !descriptor.TryGetValue("hierarchy", out var hierarchy) || !(hierarchy is List<object>)) return null;
        var current = avatar.transform;
        foreach (var part in MiniJson.List(descriptor, "hierarchy"))
        {
            var segment = part as Dictionary<string, object>;
            int index = MiniJson.Int(segment, "index", -1);
            if (index < 0 || index >= current.childCount) return null;
            current = current.GetChild(index);
            if (current.name != MiniJson.Str(segment, "name")) return null;
        }
        return current;
    }

    Object ResolveDescriptor(Dictionary<string, object> d)
    {
        if (d == null) return null;
        string kind = MiniJson.Str(d, "kind");

        // Snapshots captured before this fix never wrote "kind" for asset
        // references (icons, subMenus) — every one of them silently failed to
        // resolve on restore as a result. A missing kind with a populated
        // guid is unambiguously an asset descriptor (scene references use
        // "path", never "guid"), so treat it as one rather than leaving
        // already-captured snapshots permanently broken.
        if (string.IsNullOrEmpty(kind) && !string.IsNullOrEmpty(MiniJson.Str(d, "guid")))
            kind = "asset";

        if (kind == "nonasset")
        {
            // Non-asset menus only exist as the private copies allocated by
            // RestoreMenuAssets. They must resolve through the snapshot key;
            // there is no AssetDatabase identity to fall back to.
            return restoredMenuCopies.TryGetValue(MenuDescriptorKey(d), out var privateMenu)
                ? privateMenu
                : null;
        }

        if (kind == "asset")
        {
            if (restoredMenuCopies.TryGetValue(MenuDescriptorKey(d), out var privateMenu)) return privateMenu;
            string guid = MiniJson.Str(d, "guid");
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            string wantLocal = MiniJson.Str(d, "localId");
            foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (candidate == null) continue;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out _, out long lid) &&
                    lid.ToString() == wantLocal) return candidate;
            }
            // Never substitute the main asset for a missing subasset.
            if (!string.IsNullOrEmpty(wantLocal)) return null;
            return AssetDatabase.LoadAssetAtPath(path, typeof(Object));
        }

        if (kind == "sceneObject" || kind == "sceneComponent")
        {
            var stable = FromStableId(MiniJson.Str(d, "gid"));
            var stableTransform = stable is GameObject go ? go.transform : (stable is Component c ? c.transform : null);
            if (stableTransform && avatar && stableTransform.IsChildOf(avatar.transform)) return stable;
            var t = ResolveHierarchy(d) ?? FindTransformByRelativePath(MiniJson.Str(d, "path"));
            if (t == null) return null;
            if (kind == "sceneObject") return t.gameObject;
            string typeName = MiniJson.Str(d, "type");
            int wantOrder = MiniJson.Int(d, "order");
            int seen = 0;
            foreach (var comp in t.GetComponents<Component>())
            {
                if (comp == null) continue;
                if ((comp.GetType().FullName ?? "") != typeName) continue;
                if (seen == wantOrder) return comp;
                seen++;
            }
            return null;
        }
        return null;
    }


    Transform FindTransformByRelativePath(string relPath)
    {
        if (!avatar) return null;
        if (string.IsNullOrEmpty(relPath)) return avatar.transform;
        var current = avatar.transform;
        foreach (string name in relPath.Split('/'))
        {
            var matches = current.Cast<Transform>().Where(t => t.name == name).ToList();
            if (matches.Count != 1) return null;
            current = matches[0];
        }
        return current;
    }

    string RemapInstanceIds(string json, Dictionary<string, object> refs, List<string> problems)
    {
        if (string.IsNullOrEmpty(json)) return json;
        return InstanceIdPattern.Replace(json, m =>
        {
            string idStr = m.Groups[1].Value;
            if (!int.TryParse(idStr, out int id) || id == 0) return m.Value;
            if (refs == null || !refs.TryGetValue(idStr, out var descObj))
            {
                problems.Add("snapshot did not record a stable descriptor for instanceID " + idStr +
                    "; the reference was cleared during restore");
                return "\"instanceID\": 0";
            }
            var resolved = ResolveDescriptor(descObj as Dictionary<string, object>);
            if (resolved == null)
            {
                var d = descObj as Dictionary<string, object>;
                string detail = MiniJson.Str(d, "name", "instanceID " + idStr);
                if (MiniJson.Str(d, "kind") == "unresolved") detail += " (no stable identity was captured)";
                problems.Add("could not re-link reference to " + detail);
                return "\"instanceID\": 0";
            }
            return "\"instanceID\": " + resolved.GetInstanceID();
        });
    }

}
