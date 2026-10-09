// Easy Expression Manager: choose and override objects photographed for menu icons.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    // ── targets ───────────────────────────────────────────────────────────────

    [System.NonSerialized] ExpressionNode iconTargetsFrom;   // the toggle the auto pick came from

    [System.NonSerialized] string iconPickReason;          // shown under the Object field

    /// Auto pick for any item. A folder uses the first item directly inside it
    /// that yields something (one level only, so big menus stay quick).
    List<GameObject> AutoIconTargets(ExpressionNode n, out Dictionary<Renderer, Dictionary<int, Material>> swaps)
    {
        iconTargetsFrom = null; iconPickReason = null;
        if (n != null && n.sourceObject == null && n.IsFolderLike)
        {
            foreach (var c in n.children)
            {
                if (c.IsFolderLike) continue;
                var found = ItemIconTargets(c, out swaps, out var why);
                if (found.Count > 0) { iconTargetsFrom = c; iconPickReason = why; return found; }
            }
            swaps = new Dictionary<Renderer, Dictionary<int, Material>>();
            var named = ObjectNamedLike(n);   // last resort: an object named like the folder
            if (named) { iconTargetsFrom = n; iconPickReason = $"the object named \"{named.name}\""; return new List<GameObject> { named }; }
            return new List<GameObject>();
        }
        var list = ItemIconTargets(n, out swaps, out iconPickReason);
        if (list.Count > 0) iconTargetsFrom = n;
        return list;
    }

    /// Tries every way of finding the item's object, best first, and stops at
    /// the first one that gives something with a mesh to photograph:
    ///   1. objects its toggle actions / animation switch ON
    ///   2. an object on the avatar with the same name as the item
    ///   3. meshes its actions / animation change (material swaps, blendshapes)
    ///   4. objects it switches between (Toggle mode, currently on)
    ///   5. where its component sits (what "Locate source" selects)
    List<GameObject> ItemIconTargets(ExpressionNode n, out Dictionary<Renderer, Dictionary<int, Material>> swaps,
                                     out string reason)
    {
        swaps = new Dictionary<Renderer, Dictionary<int, Material>>();
        reason = null;
        if (n == null) return new List<GameObject>();

        var on = new List<GameObject>(); var touched = new List<GameObject>(); var flipped = new List<GameObject>();
        if (n.sourceObject != null)       ToggleCandidates(n, swaps, on, touched, flipped);
        else if (n.sourceControl != null) ControlCandidates(n, swaps, on, touched);

        var pick = Photographable(on);
        if (pick.Count > 0) { reason = n.sourceObject != null ? "what this toggle turns on" : "what its animation turns on"; return pick; }

        var named = ObjectNamedLike(n);
        if (named) { reason = $"the object named \"{named.name}\""; return new List<GameObject> { named }; }

        pick = Photographable(touched);
        if (pick.Count > 0) { reason = "the mesh this item changes"; return pick; }

        pick = Photographable(flipped);
        if (pick.Count > 0) { reason = "the object this toggle switches"; return pick; }

        var src = SourceGameObject(n);
        if (src) { reason = $"where its component sits (\"{src.name}\")"; return new List<GameObject> { src }; }

        return new List<GameObject>();
    }

    // ── helpers for the chain ─────────────────────────────────────────────────

    [System.NonSerialized] List<Transform> iconAvatarTransforms;
    [System.NonSerialized] GameObject      iconAvatarFor;

    List<Transform> AvatarTransforms()
    {
        if (iconAvatarTransforms == null || iconAvatarFor != avatar || iconAvatarTransforms.Count == 0 || iconAvatarTransforms.Any(t => !t))
        {
            iconAvatarFor = avatar;
            iconAvatarTransforms = avatar
                ? avatar.GetComponentsInChildren<Transform>(true).Where(t => t && t != avatar.transform).ToList()
                : new List<Transform>();
        }
        return iconAvatarTransforms;
    }

    static bool HasMesh(GameObject go)
    {
        if (!go) return false;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (r is SkinnedMeshRenderer smr && smr.sharedMesh) return true;
            if (r is MeshRenderer) { var mf = r.GetComponent<MeshFilter>(); if (mf && mf.sharedMesh) return true; }
        }
        return false;
    }

    List<GameObject> Photographable(List<GameObject> list) =>
        list.Where(g => g && (!avatar || g != avatar) && HasMesh(g)).Distinct().ToList();

    /// An object on the avatar named like the item ("Heart Glasses" → the
    /// "Heart Glasses" object). Exact names first, then close ones; objects
    /// under the item's own component win ties.
    GameObject ObjectNamedLike(ExpressionNode n)
    {
        string key = EEMPath.MatchKey(n.rawName);
        if (key.Length < 2 || !avatar) return null;
        var src = SourceGameObject(n, requireMesh: false);
        int Score(Transform t) => (src && t.IsChildOf(src.transform) ? 0 : 1000) + Depth(t);

        var exact = AvatarTransforms().Where(t => t && EEMPath.MatchKey(t.name) == key && HasMesh(t.gameObject))
                                      .OrderBy(Score).FirstOrDefault();
        if (exact) return exact.gameObject;
        if (key.Length < 3) return null;

        var close = AvatarTransforms()
            .Where(t => t)
            .Select(t => (t, k: EEMPath.MatchKey(t.name)))
            .Where(x => x.k.Length >= 3 && (x.k.Contains(key) || key.Contains(x.k)) && HasMesh(x.t.gameObject))
            .OrderBy(x => System.Math.Abs(x.k.Length - key.Length)).ThenBy(x => Score(x.t))
            .FirstOrDefault();
        return close.t ? close.t.gameObject : null;
    }

    static int Depth(Transform t) { int d = 0; for (var c = t; c && c.parent; c = c.parent) d++; return d; }

    /// The GameObject "Locate source" would select for this item, if it's in the
    /// scene (VRC menu items point at a menu asset instead, so they have none).
    GameObject SourceGameObject(ExpressionNode n, bool requireMesh = true)
    {
        Component comp = n?.sourceObject?.targetObject as Component;
        if (!comp) comp = n?.sourceFullControllerObject?.targetObject as Component;
        if (!comp) return null;
        var go = comp.gameObject;
        if (avatar && go == avatar) return null;          // that would be the whole avatar
        return !requireMesh || HasMesh(go) ? go : null;
    }

    void ToggleCandidates(ExpressionNode n, Dictionary<Renderer, Dictionary<int, Material>> swaps,
                          List<GameObject> turnedOn, List<GameObject> touched, List<GameObject> flipped)
    {
        var so = ReadSO(n.sourceObject);   // read-only: looking must never unpack a prefab
        var actions = so?.FindProperty("content.state.actions");
        if (actions == null || !actions.isArray) return;

        for (int i = 0; i < actions.arraySize; i++)
        {
            var el   = actions.GetArrayElementAtIndex(i);
            string t = el?.managedReferenceFullTypename ?? "";

            if (t.EndsWith("ObjectToggleAction"))
            {
                var obj  = el.FindPropertyRelative("obj")?.objectReferenceValue as GameObject;
                int mode = el.FindPropertyRelative("mode")?.intValue ?? 0;   // 0 TurnOn, 1 TurnOff, 2 Toggle
                if (!obj || mode == 1) continue;
                var list = mode == 2 && obj.activeSelf ? flipped : turnedOn;
                if (!list.Contains(obj)) list.Add(obj);
            }
            else if (t.EndsWith("MaterialAction") || t.EndsWith("BlendShapeAction") ||
                     t.EndsWith("ShaderInventoryAction") || t.EndsWith("PoiyomiUVTileAction"))
            {
                var r = el.FindPropertyRelative("renderer")?.objectReferenceValue as Renderer;
                if (!r) continue;
                if (!touched.Contains(r.gameObject)) touched.Add(r.gameObject);
                if (t.EndsWith("MaterialAction"))
                {
                    int slot = el.FindPropertyRelative("materialIndex")?.intValue ?? 0;
                    var mat  = el.FindPropertyRelative("mat.objRef")?.objectReferenceValue as Material;
                    if (mat)
                    {
                        if (!swaps.TryGetValue(r, out var d)) swaps[r] = d = new Dictionary<int, Material>();
                        d[slot] = mat;
                    }
                }
            }
        }
    }

    void EnsureIconTargets(ExpressionNode n, bool force = false)
    {
        if (!force && iconTargetsNode == n) return;
        if (force) { animIndexCache.Clear(); iconAvatarTransforms = null; }   // things may have changed since
        iconTargetsNode = n;
        var auto = AutoIconTargets(n, out iconSwaps);
        if (n != null && iconOverrides.TryGetValue(n.stableKey ?? "", out var ov))
        {
            iconTargets = ov.Where(o => o).ToList();
            iconTargetsAuto = false;
        }
        else
        {
            iconTargets = auto;
            iconTargetsAuto = true;
        }
        IconRenderer.SetTargets(iconTargets, avatar ? avatar.transform : null, iconSwaps);
        iconPreviewDirty = true;
    }

    void SetIconOverride(ExpressionNode n, List<GameObject> objs)
    {
        string key = n.stableKey ?? "";
        if (objs == null) iconOverrides.Remove(key);
        else iconOverrides[key] = objs;
        EnsureIconTargets(n, force: true);
    }

}
