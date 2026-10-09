// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — auto icon: what to photograph, saving, and the panel
// ═════════════════════════════════════════════════════════════════════════════
//
// The generator opens inline under the item's icon field (it never replaces
// it). Split View stacks it vertically in the details pane; Classic lays it out
// wide in the edit panel.
//
// What gets photographed: for a VRCFury Toggle, every object its "Turn On"
// actions switch on (Toggle-mode objects that are currently off count too).
// If it has none, the renderers its material / blendshape actions touch —
// and material swaps are applied, so the icon shows what the toggle does.
// Anything else (Full Controller items, native controls) starts empty. Dropping
// an object onto the Object field always replaces the pick; Reset goes back.
//
// Files: <folder>/<avatar>/<item>.png. "Save as new" never touches an existing
// file ("Hat (2).png" …). "Overwrite previous" only ever writes over an icon
// this generator made for that item.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    [System.NonSerialized] IconGenSettings iconSettings;
    IconGenSettings IG => iconSettings ?? (iconSettings = IconGenSettings.Load());

    [System.NonSerialized] EEMIconRenderer   iconRenderer;
    [System.NonSerialized] Texture2D         iconPreview;
    [System.NonSerialized] bool              iconPreviewDirty = true;
    [System.NonSerialized] ExpressionNode    iconTargetsNode;
    [System.NonSerialized] List<GameObject>  iconTargets = new List<GameObject>();
    [System.NonSerialized] bool              iconTargetsAuto = true;
    [System.NonSerialized] Dictionary<Renderer, Dictionary<int, Material>> iconSwaps;
    [System.NonSerialized] bool              iconShowAll, iconShowMore;
    [System.NonSerialized] Dictionary<string, List<GameObject>> iconOverrides = new Dictionary<string, List<GameObject>>();
    static Texture2D checkerTex;

    const int PREVIEW_SIZE = 192;

    // ── lifecycle ─────────────────────────────────────────────────────────────

    EEMIconRenderer IconRenderer => iconRenderer ?? (iconRenderer = new EEMIconRenderer());

    void DisposeIconGen()
    {
        iconRenderer?.Dispose(); iconRenderer = null;
        animIndexCache.Clear();
        iconRenderQueued = false;
        if (iconPreview) DestroyImmediate(iconPreview);
        iconPreview = null; iconTargetsNode = null;
    }

    [System.NonSerialized] bool iconRenderQueued;

    void RenderIconPreviewNow()
    {
        iconRenderQueued = false;
        if (this == null || iconRenderer == null || !iconPreviewDirty) return;
        iconPreviewDirty = false;
        if (iconPreview) DestroyImmediate(iconPreview);
        try { iconPreview = iconRenderer.Render(IG, PREVIEW_SIZE, 2); }
        catch (System.Exception ex) { iconPreview = null; Debug.LogWarning("[EASY] Icon preview failed: " + ex.Message); }
        Repaint();
    }

    // ── the Auto button (sits under the icon field) ───────────────────────────

    void DrawAutoIconButton(float width)
    {
        var c = IC("Camera Icon", " Auto", "Make an icon by photographing the item itself.", "Auto");
        var prev = GUI.backgroundColor;
        if (IG.open) GUI.backgroundColor = new Color(0.55f, 0.78f, 1f);
        if (GUILayout.Button(c, EditorStyles.miniButton, GUILayout.Width(width)))
            Defer(() => { IG.open = !IG.open; IG.Save(); });   // layout changes after this pass
        GUI.backgroundColor = prev;
    }

    // ── the panel ─────────────────────────────────────────────────────────────

    void DrawIconGenerator(ExpressionNode n, bool wide)
    {
        if (!IG.open || n == null || n == root) return;
        EnsureIconTargets(n);

        GUILayout.Space(8);
        Fill(GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)), LineCol);
        GUILayout.Space(4);
        GUILayout.Label("Icon generator", EditorStyles.boldLabel);

        if (wide)
        {
            EditorGUILayout.BeginHorizontal();
            DrawIconPreview(GUILayoutUtility.GetRect(140f, 140f, GUILayout.Width(140f), GUILayout.Height(140f)), n);
            GUILayout.Space(10);
            EditorGUILayout.BeginVertical();
            DrawIconControls(n);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }
        else
        {
            var r = GUILayoutUtility.GetAspectRect(1f, GUILayout.MaxWidth(240f), GUILayout.MaxHeight(240f));
            DrawIconPreview(r, n);
            GUILayout.Space(6);
            DrawIconControls(n);
        }
    }

    void DrawIconPreview(Rect r, ExpressionNode n)
    {
        var e = Event.current;

        if (e.type == EventType.Repaint)
        {
            if (IG.background == IconBackground.Colour) EditorGUI.DrawRect(r, IG.bgColor);
            else GUI.DrawTextureWithTexCoords(r, CheckerTex, new Rect(0, 0, r.width / 16f, r.height / 16f));
            Outline(r, LineCol);

            // Never render a camera in the middle of drawing the window — it
            // disturbs the GUI's render state. Render right after, then repaint.
            if (iconPreviewDirty && IconRenderer.HasContent && !iconRenderQueued)
            {
                iconRenderQueued = true;
                EditorApplication.delayCall += RenderIconPreviewNow;
            }
            if (IconRenderer.HasContent && iconPreview)
                GUI.DrawTexture(r, iconPreview, ScaleMode.ScaleToFit, true);
        }

        if (!IconRenderer.HasContent)
        {
            GUI.Label(new Rect(r.x + 10, r.y, r.width - 20, r.height),
                n.IsFolderLike
                    ? "Nothing directly inside this folder switches an object on. Drag the object to photograph into the Object field below."
                    : "Couldn't find an object this item switches on. Drag the object to photograph into the Object field below.",
                S.hint);
            return;
        }

        if (r.Contains(e.mousePosition))
        {
            EditorGUIUtility.AddCursorRect(r, MouseCursor.Orbit);
            GUI.Label(new Rect(r.x + 4, r.yMax - 16, r.width - 8, 14), "drag to orbit",
                      new GUIStyle(S.rightText) { alignment = TextAnchor.LowerRight });
        }

        int id = GUIUtility.GetControlID(FocusType.Passive);
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (e.button == 0 && r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; e.Use(); }
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == id)
                {
                    IG.yaw   = Mathf.Repeat(IG.yaw + e.delta.x * 0.6f + 180f, 360f) - 180f;
                    IG.pitch = Mathf.Clamp(IG.pitch + e.delta.y * 0.5f, -85f, 85f);
                    iconPreviewDirty = true;
                    e.Use(); Repaint();
                }
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; IG.Save(); e.Use(); }
                break;
        }
    }

    static readonly string[] AngleNames  = { "Front", "3/4", "Side", "Back", "Top" };
    static readonly Vector2[] AngleValues =
        { new Vector2(0f, 8f), new Vector2(35f, 15f), new Vector2(90f, 5f), new Vector2(180f, 8f), new Vector2(0f, 80f) };
    static readonly string[] StyleNames   = { "Full texture", "Silhouette", "Shaded", "Outline only" };
    static readonly string[] BgNames      = { "Transparent", "Colour" };
    static readonly string[] OutlineNames = { "No outline", "Thin outline", "Medium outline", "Thick outline", "Custom" };
    static readonly string[] SizeNames    = { "128 px", "256 px", "512 px" };
    static readonly int[]    Sizes        = { 128, 256, 512 };

    void DrawIconControls(ExpressionNode n)
    {
        // angle presets
        EditorGUILayout.BeginHorizontal();
        for (int i = 0; i < AngleNames.Length; i++)
        {
            var st = i == 0 ? EditorStyles.miniButtonLeft : i == AngleNames.Length - 1 ? EditorStyles.miniButtonRight
                                                                                       : EditorStyles.miniButtonMid;
            bool on = Mathf.Abs(Mathf.DeltaAngle(IG.yaw, AngleValues[i].x)) < 0.5f &&
                      Mathf.Abs(IG.pitch - AngleValues[i].y) < 0.5f;
            if (GUILayout.Toggle(on, AngleNames[i], st) && !on)
            {
                IG.yaw = AngleValues[i].x; IG.pitch = AngleValues[i].y;
                IG.Save(); iconPreviewDirty = true;
            }
        }
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(4);

        DrawIconObjectField(n);
        GUILayout.Space(2);

        EditorGUI.BeginChangeCheck();
        float lw = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 70f;

        EditorGUILayout.BeginHorizontal();
        var previousStyle = IG.style;
        IG.style = (IconStyle)EditorGUILayout.Popup("Style", (int)IG.style, StyleNames);
        if (IG.style == IconStyle.OutlineOnly && previousStyle != IconStyle.OutlineOnly && IG.outline == IconOutline.None)
            IG.outline = IconOutline.Thin;
        if (IG.style == IconStyle.Silhouette || IG.style == IconStyle.Shaded)
            IG.tint = EditorGUILayout.ColorField(GUIContent.none, IG.tint, false, false, false, GUILayout.Width(40));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        IG.background = (IconBackground)EditorGUILayout.Popup("Background", (int)IG.background, BgNames);
        if (IG.background == IconBackground.Colour) IG.bgColor = EditorGUILayout.ColorField(GUIContent.none, IG.bgColor, false, false, false, GUILayout.Width(40));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        IG.outline = (IconOutline)EditorGUILayout.Popup("Outline", (int)IG.outline, OutlineNames);
        if (IG.outline != IconOutline.None) IG.outlineColor = EditorGUILayout.ColorField(GUIContent.none, IG.outlineColor, false, false, false, GUILayout.Width(40));
        if (IG.outline == IconOutline.Custom)
        {
            IG.customOutlinePercent = Mathf.Clamp(EditorGUILayout.FloatField(IG.customOutlinePercent, GUILayout.Width(52)), 0f, 20f);
            GUILayout.Label("%", GUILayout.Width(12));
        }
        EditorGUILayout.EndHorizontal();

        iconShowMore = EditorGUILayout.Foldout(iconShowMore, "More", true);
        if (iconShowMore)
        {
            int si = System.Array.IndexOf(Sizes, IG.size); if (si < 0) si = 1;
            IG.size    = Sizes[EditorGUILayout.Popup("Size", si, SizeNames)];
            IG.padding = EditorGUILayout.Slider("Padding", IG.padding * 100f, 0f, 30f) / 100f;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Save to", "Icons go in a folder per avatar inside this folder."));
            GUILayout.Label(new GUIContent(IG.folder + "/<avatar>", IG.folder), EditorStyles.miniLabel,
                            GUILayout.MinWidth(20), GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Change…", EditorStyles.miniButtonLeft, GUILayout.ExpandWidth(false)))
                Defer(PickIconFolder);
            if (GUILayout.Button(new GUIContent("Show", "Show this avatar's icon folder in the Project window."),
                    EditorStyles.miniButtonRight, GUILayout.ExpandWidth(false)))
                ShowIconFolder();
            EditorGUILayout.EndHorizontal();
        }

        EditorGUIUtility.labelWidth = lw;
        if (EditorGUI.EndChangeCheck()) { IG.Save(); iconPreviewDirty = true; }

        GUILayout.Space(6);
        DrawSnapButton(n);
    }

    void DrawIconObjectField(ExpressionNode n)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent("Object", "What gets photographed. Drop an object here to use only that one."),
                        GUILayout.Width(66));
        var first = iconTargets.Count > 0 ? iconTargets[0] : null;
        var picked = (GameObject)EditorGUILayout.ObjectField(first, typeof(GameObject), true, GUILayout.MinWidth(60));
        if (picked != first)
        {
            var cap = n; var obj = picked;
            Defer(() => SetIconOverride(cap, obj ? new List<GameObject> { obj } : new List<GameObject>()));
        }
        if (iconTargets.Count > 1)
            GUILayout.Label("+" + (iconTargets.Count - 1), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
        if (!iconTargetsAuto &&
            GUILayout.Button(new GUIContent("Reset", "Go back to the objects this item switches on."),
                EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
        { var cap = n; Defer(() => SetIconOverride(cap, null)); }
        EditorGUILayout.EndHorizontal();

        if (iconTargetsAuto && iconTargets.Count > 0)
        {
            string what = iconPickReason ?? "found automatically";
            string from = iconTargetsFrom != null && iconTargetsFrom != n
                ? $", from \"{DisplayOf(iconTargetsFrom)}\" (first item in this folder)" : "";
            GUILayout.Label($"Auto: {what}{from}" + (iconTargets.Count > 1 ? $" ({iconTargets.Count} objects)." : "."),
                            S.subtitle);
        }

        if (iconTargets.Count > 1 || !iconTargetsAuto)
        {
            iconShowAll = EditorGUILayout.Foldout(iconShowAll, "All objects", true);
            if (iconShowAll)
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < iconTargets.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    var cur = iconTargets[i];
                    var rep = (GameObject)EditorGUILayout.ObjectField(cur, typeof(GameObject), true);
                    int idx = i;
                    if (rep != cur)
                    { var cap = n; var list = new List<GameObject>(iconTargets); if (rep) list[idx] = rep; else list.RemoveAt(idx); Defer(() => SetIconOverride(cap, list)); }
                    if (GUILayout.Button(new GUIContent("×", "Leave this one out."), EditorStyles.miniButton, GUILayout.Width(20)))
                    { var cap = n; var list = new List<GameObject>(iconTargets); list.RemoveAt(idx); Defer(() => SetIconOverride(cap, list)); }
                    EditorGUILayout.EndHorizontal();
                }
                var add = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Add"), null, typeof(GameObject), true);
                if (add && !iconTargets.Contains(add))
                { var cap = n; var list = new List<GameObject>(iconTargets) { add }; Defer(() => SetIconOverride(cap, list)); }
                EditorGUI.indentLevel--;
            }
        }
    }

    void DrawSnapButton(ExpressionNode n)
    {
        bool canOverwrite = CanOverwriteIcon(n);
        bool overwriting  = IG.overwrite && canOverwrite;

        var r     = GUILayoutUtility.GetRect(10f, 26f, GUILayout.ExpandWidth(true));
        var main  = new Rect(r.x, r.y, r.width - 22f, r.height);
        var arrow = new Rect(main.xMax, r.y, 22f, r.height);

        using (new EditorGUI.DisabledScope(!IconRenderer.HasContent))
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.45f, 0.72f, 1f);
            var label = IC("Camera Icon", overwriting ? " Snap and replace icon file" : " Snap and set icon",
                overwriting ? "Photograph the item and write over this item's previous generated icon."
                            : "Photograph the item, save it as a new file and set it as the icon.",
                overwriting ? "Snap and replace icon file" : "Snap and set icon");
            if (GUI.Button(main, label, Seg("ButtonLeft")))
            { var cap = n; EditorApplication.delayCall += () => { if (this != null) SnapIcon(cap); }; }
            GUI.backgroundColor = prev;
        }

        if (EditorGUI.DropdownButton(arrow, new GUIContent("▾", "Save as a new file or overwrite the previous one."),
                FocusType.Passive, Seg("ButtonRight")))
        {
            var m = new GenericMenu();
            m.AddItem(new GUIContent("Save as new file"), !IG.overwrite, () => { IG.overwrite = false; IG.Save(); Repaint(); });
            // Overwrite is a standing preference: it still shows as chosen when
            // this item has no generated icon yet (the first snap makes one).
            m.AddItem(new GUIContent(canOverwrite ? "Overwrite previous" : "Overwrite previous  (first snap saves a new file)"),
                      IG.overwrite, () => { IG.overwrite = true; IG.Save(); Repaint(); });
            m.DropDown(arrow);
        }
    }

    static GUIStyle Seg(string name) => GUI.skin.FindStyle(name) ?? GUI.skin.button;

    static Texture2D CheckerTex
    {
        get
        {
            if (checkerTex) return checkerTex;
            checkerTex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            Color a = Sk(new Color(0.27f, 0.27f, 0.27f), new Color(0.85f, 0.85f, 0.85f));
            Color b = Sk(new Color(0.22f, 0.22f, 0.22f), new Color(0.75f, 0.75f, 0.75f));
            checkerTex.SetPixels(new[] { a, b, b, a });
            checkerTex.Apply();
            return checkerTex;
        }
    }

}
