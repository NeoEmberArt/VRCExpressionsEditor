// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — auto icon: renderer
// ═════════════════════════════════════════════════════════════════════════════
//
// Photographs a set of objects on their own. The meshes are drawn into a
// private preview scene (the same mechanism Unity's own asset previews use), so
// nothing else in the scene or on the avatar can appear in the shot, and the
// objects' active/inactive state doesn't matter.
//
// Transparency: the item is rendered twice, once over black and once over
// white. Where the two differ, the background showed through — that difference
// IS the coverage. This gives correct transparency for cut-out hair, glass and
// any avatar shader, without trusting whatever alpha a shader happens to write.
//
// After that, everything is plain pixel work on the CPU: crop to the visible
// pixels and fit them centred into the square with the chosen padding, then
// optional outline and background colour.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;

public sealed class EEMIconRenderer : IDisposable
{
    struct Item { public Mesh mesh; public bool owned; public Matrix4x4 matrix; public Material[] mats; }

    readonly Scene scene;
    readonly GameObject camGO, keyGO, fillGO;
    readonly Camera cam;
    readonly Light key, fill;
    readonly List<Item> items = new List<Item>();

    Bounds  bounds;
    Vector3 up = Vector3.up, forward = Vector3.forward;
    bool    disposed;

    public bool HasContent => items.Count > 0;

    public EEMIconRenderer()
    {
        scene = EditorSceneManager.NewPreviewScene();

        camGO = EditorUtility.CreateGameObjectWithHideFlags("EEM Icon Camera", HideFlags.HideAndDontSave, typeof(Camera));
        SceneManager.MoveGameObjectToScene(camGO, scene);
        cam = camGO.GetComponent<Camera>();
        cam.cameraType          = CameraType.Preview;
        cam.enabled             = false;
        cam.clearFlags          = CameraClearFlags.SolidColor;
        cam.renderingPath       = RenderingPath.Forward;
        cam.useOcclusionCulling = false;
        cam.allowHDR            = false;
        cam.allowMSAA           = false;
        cam.fieldOfView         = 18f;      // narrow lens = little perspective distortion
        cam.scene               = scene;

        keyGO  = MakeLight("EEM Icon Key Light",  out key);
        key.intensity  = 1.05f; key.color  = new Color(1f, 0.98f, 0.95f);
        fillGO = MakeLight("EEM Icon Fill Light", out fill);
        fill.intensity = 0.45f; fill.color = new Color(0.85f, 0.9f, 1f);
    }

    GameObject MakeLight(string name, out Light l)
    {
        var go = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave, typeof(Light));
        SceneManager.MoveGameObjectToScene(go, scene);
        l = go.GetComponent<Light>();
        l.type    = LightType.Directional;
        l.shadows = LightShadows.None;
        l.enabled = false;
        return go;
    }

    // ── what to photograph ────────────────────────────────────────────────────

    /// Captures the meshes under the given objects in their current pose
    /// (skinned meshes are baked, so blendshapes and bone poses are kept).
    /// `frame` orients the camera (the avatar root: "front" = its front).
    /// `materialSwaps` replaces material slots, e.g. what a toggle swaps in.
    public void SetTargets(IList<GameObject> roots, Transform frame,
                           Dictionary<Renderer, Dictionary<int, Material>> materialSwaps)
    {
        ClearItems();
        if (frame) { up = frame.up; forward = frame.forward; }
        else       { up = Vector3.up; forward = Vector3.forward; }

        if (roots == null) return;
        // First only what's switched on inside the chosen objects. If that
        // leaves nothing (a toggle that switches a renderer or a child on,
        // rather than the object itself), photograph every mesh under them.
        Gather(roots, materialSwaps, strict: true);
        if (items.Count == 0) Gather(roots, materialSwaps, strict: false);
    }

    void Gather(IList<GameObject> roots, Dictionary<Renderer, Dictionary<int, Material>> materialSwaps, bool strict)
    {
        var  seen = new HashSet<Renderer>();
        bool any  = false;
        foreach (var go in roots)
        {
            if (!go) continue;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (!r || !seen.Add(r) || (strict && !VisibleWithin(r, go.transform))) continue;

                var mats = r.sharedMaterials;
                if (materialSwaps != null && materialSwaps.TryGetValue(r, out var swaps))
                {
                    mats = (Material[])mats.Clone();
                    foreach (var kv in swaps)
                        if (kv.Key >= 0 && kv.Key < mats.Length && kv.Value) mats[kv.Key] = kv.Value;
                }

                if (r is SkinnedMeshRenderer smr)
                {
                    if (!smr.sharedMesh) continue;
                    var m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                    try { smr.BakeMesh(m, true); }
                    catch { UnityEngine.Object.DestroyImmediate(m); continue; }
                    if (m.vertexCount == 0) { UnityEngine.Object.DestroyImmediate(m); continue; }
                    var t = smr.transform;
                    Add(new Item { mesh = m, owned = true, mats = mats,
                                   matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one) }, ref any);
                }
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (!mf || !mf.sharedMesh) continue;
                    Add(new Item { mesh = mf.sharedMesh, owned = false, mats = mats,
                                   matrix = r.transform.localToWorldMatrix }, ref any);
                }
            }
        }
    }

    /// The chosen object itself may be switched off (that's what the toggle
    /// turns on), but anything switched off *inside* it stays out of the shot.
    static bool VisibleWithin(Renderer r, Transform root)
    {
        if (!r.enabled) return false;
        for (var t = r.transform; t != null && t != root; t = t.parent)
            if (!t.gameObject.activeSelf) return false;
        return true;
    }

    void Add(Item it, ref bool any)
    {
        items.Add(it);
        var b = TransformBounds(it.mesh.bounds, it.matrix);
        if (!any) { bounds = b; any = true; } else bounds.Encapsulate(b);
    }

    static Bounds TransformBounds(Bounds b, Matrix4x4 m)
    {
        var c = b.center; var e = b.extents;
        var r = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
        for (int i = 0; i < 8; i++)
        {
            var p = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            r.Encapsulate(m.MultiplyPoint3x4(p));
        }
        return r;
    }

    void ClearItems()
    {
        foreach (var it in items) if (it.owned && it.mesh) UnityEngine.Object.DestroyImmediate(it.mesh);
        items.Clear();
    }

    // ── render ────────────────────────────────────────────────────────────────

    /// Returns a new, non-asset Texture2D (caller destroys it), or null when
    /// nothing is visible. `supersample` 2 gives clean edges; 1 is for quick
    /// previews while dragging.
    public Texture2D Render(IconGenSettings s, int outSize, int supersample)
    {
        if (disposed || !HasContent) return null;

        // camera around the item, oriented to the avatar
        var right = Vector3.Cross(up, forward).normalized;
        float pitch = Mathf.Clamp(s.pitch, -85f, 85f);
        var dir = Quaternion.AngleAxis(s.yaw, up) * (Quaternion.AngleAxis(-pitch, right) * forward);
        var center = bounds.center;
        float radius = Mathf.Max(bounds.extents.magnitude, 0.0005f);
        float dist   = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        cam.transform.position = center + dir * dist;
        cam.transform.rotation = Quaternion.LookRotation(-dir, up);
        cam.nearClipPlane = Mathf.Max(dist - radius * 1.25f, dist * 0.01f);
        cam.farClipPlane  = dist + radius * 1.25f;
        key.transform.rotation  = cam.transform.rotation * Quaternion.Euler(28f, -32f, 0f);
        fill.transform.rotation = cam.transform.rotation * Quaternion.Euler(12f, 150f, 0f);

        // internal size: big enough that cropping to the item still leaves detail
        int R = Mathf.Clamp(Mathf.RoundToInt(outSize * 1.6f) * Mathf.Max(1, supersample), 64, 2048);

        bool half = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) &&
                    SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf);
        var rt   = RenderTexture.GetTemporary(R, R, 24,
                       half ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var read = new Texture2D(R, R, half ? TextureFormat.RGBAHalf : TextureFormat.RGBA32, false, true)
                   { hideFlags = HideFlags.HideAndDontSave };
        Color[] onBlack, onWhite;
        try
        {
            // Rebinding materials during camera rendering can invoke editor
            // shader drawers (including incompatible optimized Thry drawers).
            // Use VRCFury's existing scoped native-drawer suppression; shader
            // properties/keywords and normal material inspectors stay intact.
            using (SuppressPreviewMaterialDrawers())
            {
                onBlack = Pass(rt, read, new Color(0f, 0f, 0f, 0f));
                onWhite = Pass(rt, read, new Color(1f, 1f, 1f, 0f));
            }
        }
        finally
        {
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(read);
        }

        return EEMIconPixels.Compose(s, onBlack, onWhite, R, outSize);
    }

    static System.Reflection.MethodInfo suppressDrawers = FindDrawerSuppression();
    static System.Reflection.MethodInfo FindDrawerSuppression()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType("VF.Hooks.UnityFixes.SuppressMaterialPropertyDrawersHook", false);
            var method = type?.GetMethod("Suppress", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method != null) return method;
        }
        return null;
    }

    internal static IDisposable SuppressPreviewMaterialDrawers()
    {
        // Gesture Manager can initialize before VRCFury's editor assembly has
        // been loaded. Retry once at the call site so the safety scope is not
        // silently skipped because EEM's type initialized early.
        if (suppressDrawers == null) suppressDrawers = FindDrawerSuppression();
        return suppressDrawers?.Invoke(null, null) as IDisposable;
    }

    Color[] Pass(RenderTexture rt, Texture2D read, Color background)
    {
        cam.backgroundColor = background;
        foreach (var it in items)
        {
            if (it.mats == null || it.mats.Length == 0) continue;
            int subs = Mathf.Max(1, it.mesh.subMeshCount);
            // like Unity: extra material slots draw the last sub-mesh again
            for (int i = 0; i < Mathf.Max(subs, it.mats.Length); i++)
            {
                var mat = it.mats[Mathf.Min(i, it.mats.Length - 1)];
                if (!mat) continue;
                Graphics.DrawMesh(it.mesh, it.matrix, mat, 0, cam, Mathf.Min(i, subs - 1));
            }
        }

        bool overridden = Unsupported.SetOverrideLightingSettings(scene);
        if (overridden)
        {
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.45f);
            var refl = DefaultReflection();
            if (refl)
            {
                RenderSettings.defaultReflectionMode   = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = refl;
            }
        }

        var prevActive = RenderTexture.active;
        key.enabled = fill.enabled = true;
        cam.targetTexture = rt;
        try
        {
            cam.Render();
            RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            return read.GetPixels();
        }
        finally
        {
            RenderTexture.active = prevActive;
            cam.targetTexture = null;
            key.enabled = fill.enabled = false;
            if (overridden) Unsupported.RestoreOverrideLightingSettings();
        }
    }

    static Texture reflection;
    static Texture DefaultReflection()
    {
        if (reflection) return reflection;
        bool prev = Debug.unityLogger.logEnabled;
        Debug.unityLogger.logEnabled = false;
        try { reflection = EditorGUIUtility.Load("PrefabMode/DefaultReflectionForPrefabMode.exr") as Texture; }
        catch { reflection = null; }
        finally { Debug.unityLogger.logEnabled = prev; }
        if (!reflection) reflection = ReflectionProbe.defaultTexture;
        return reflection;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClearItems();
        if (camGO)  UnityEngine.Object.DestroyImmediate(camGO);
        if (keyGO)  UnityEngine.Object.DestroyImmediate(keyGO);
        if (fillGO) UnityEngine.Object.DestroyImmediate(fillGO);
        if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
    }
}
