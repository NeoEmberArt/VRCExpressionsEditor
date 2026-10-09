// Easy Expression Manager: cached UI styles, icons and node descriptions.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public partial class EASYExpressionManager
{
    // ── cached styles ─────────────────────────────────────────────────────────
    class DesignSkin
    {
        public bool pro;
        public GUIStyle label, labelSel, labelWarn, rightText, rightSel, warnMini, rootLabel,
                        title, subtitle, section, bar, detail, hint;
    }

    [System.NonSerialized] DesignSkin skin;

    DesignSkin S
    {
        get
        {
            if (skin == null || skin.pro != EditorGUIUtility.isProSkin) skin = BuildSkin();
            return skin;
        }
    }

    DesignSkin BuildSkin()
    {
        var s = new DesignSkin { pro = EditorGUIUtility.isProSkin };

        var baseLabel = new GUIStyle(EditorStyles.label)
        {
            richText = false, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip,
            padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0)
        };
        s.label     = new GUIStyle(baseLabel);
        s.labelSel  = new GUIStyle(baseLabel); s.labelSel.normal.textColor  = Color.white;
        s.labelWarn = new GUIStyle(baseLabel); s.labelWarn.normal.textColor = WarnText;
        s.rootLabel = new GUIStyle(baseLabel) { fontStyle = FontStyle.Bold };

        s.rightText = new GUIStyle(EditorStyles.miniLabel)
            { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 0, 0) };
        s.rightText.normal.textColor = DimText;
        s.rightSel = new GUIStyle(s.rightText); s.rightSel.normal.textColor = new Color(0.86f, 0.91f, 1f);
        s.warnMini = new GUIStyle(s.rightText) { fontStyle = FontStyle.Bold };
        s.warnMini.normal.textColor = WarnText;

        s.title    = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, richText = false, wordWrap = true };
        s.subtitle = new GUIStyle(EditorStyles.wordWrappedMiniLabel); s.subtitle.normal.textColor = DimText;
        s.section  = new GUIStyle(EditorStyles.boldLabel) { margin = new RectOffset(0, 0, 8, 2) };
        s.bar      = new GUIStyle { padding = new RectOffset(6, 6, 4, 4) };
        s.detail   = new GUIStyle { padding = new RectOffset(14, 14, 12, 12) };
        s.hint     = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };
        return s;
    }

    // ── palette (dark / light skin) ───────────────────────────────────────────
    static bool  Pro => EditorGUIUtility.isProSkin;
    static Color Sk(Color dark, Color light) => Pro ? dark : light;

    static Color SelBg    => Sk(new Color(0.17f, 0.36f, 0.53f),        new Color(0.23f, 0.45f, 0.69f));
    static Color SelSoft  => Sk(new Color(0.17f, 0.36f, 0.53f, 0.45f), new Color(0.23f, 0.45f, 0.69f, 0.32f));
    static Color HoverBg  => Sk(new Color(1f, 1f, 1f, 0.045f),         new Color(0f, 0f, 0f, 0.06f));
    static Color LineCol  => Sk(new Color(0f, 0f, 0f, 0.38f),          new Color(0f, 0f, 0f, 0.16f));
    static Color HeaderBg => Sk(new Color(0f, 0f, 0f, 0.16f),          new Color(0f, 0f, 0f, 0.07f));
    static Color DimText  => Sk(new Color(0.60f, 0.60f, 0.60f),        new Color(0.36f, 0.36f, 0.36f));
    static Color WarnText => Sk(new Color(1f, 0.47f, 0.40f),           new Color(0.72f, 0.16f, 0.10f));
    static Color DropCol  => new Color(0.30f, 0.65f, 1f);

    static Color WithA(Color c, float a) { c.a = a; return c; }

    // ── drawing primitives ────────────────────────────────────────────────────
    static void Fill(Rect r, Color c) { if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(r, c); }

    static void Outline(Rect r, Color c)
    {
        if (Event.current.type != EventType.Repaint) return;
        EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), c);
        EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), c);
        EditorGUI.DrawRect(new Rect(r.x, r.y, 1, r.height), c);
        EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), c);
    }

    static Texture2D dotTex;
    static Texture2D DotTex
    {
        get
        {
            if (dotTex) return dotTex;
            const int n = 16;
            dotTex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            var px = new Color[n * n];
            float r = n * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - r, dy = y + 0.5f - r;
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy)));
                }
            dotTex.SetPixels(px);
            dotTex.Apply();
            return dotTex;
        }
    }

    static void Dot(Rect r, Color c)
    {
        if (Event.current.type != EventType.Repaint) return;
        var prev = GUI.color; GUI.color = c;
        GUI.DrawTexture(r, DotTex, ScaleMode.StretchToFill, true);
        GUI.color = prev;
    }

    // ── built-in editor icons, loaded quietly ─────────────────────────────────
    // IconContent logs an error for names a given Unity version doesn't ship, so
    // lookups run with logging muted and fall back to text when missing.
    static readonly Dictionary<string, Texture> iconCache = new Dictionary<string, Texture>();

    static Texture Ico(string name)
    {
        string key = (Pro ? "d:" : "l:") + name;
        if (iconCache.TryGetValue(key, out var cached)) return cached;
        Texture t = null;
        bool prev = Debug.unityLogger.logEnabled;
        Debug.unityLogger.logEnabled = false;
        try
        {
            if (Pro) t = EditorGUIUtility.IconContent("d_" + name)?.image;
            if (!t)  t = EditorGUIUtility.IconContent(name)?.image;
        }
        catch { t = null; }
        finally { Debug.unityLogger.logEnabled = prev; }
        iconCache[key] = t;
        return t;
    }

    /// Icon + text content that degrades to text alone if the icon is missing.
    static GUIContent IC(string icon, string text, string tip, string fallback = null)
    {
        var tex = string.IsNullOrEmpty(icon) ? null : Ico(icon);
        if (tex) return new GUIContent(text ?? "", tex, tip);
        string t = string.IsNullOrEmpty(text) ? (fallback ?? "") : text.TrimStart();
        return new GUIContent(t, tip);
    }

    Texture TypeIcon(ExpressionNode n)
    {
        if (n.icon) return n.icon;
        Texture t;
        if (n.isVirtualFolder || n.type == NodeType.Folder)
            t = Ico(n.children.Count == 0 ? "FolderEmpty Icon" : (n.isExpanded ? "FolderOpened Icon" : "Folder Icon"));
        else if (n.type == NodeType.SubMenu) t = Ico("VerticalLayoutGroup Icon");
        else if (n.type == NodeType.Slider)  t = Ico("Slider Icon");
        else                                 t = Ico("Toggle Icon");
        return t ? t : DefaultIcon(n.type);
    }

    // ── words ─────────────────────────────────────────────────────────────────
    static string ItemsText(int c) => c == 1 ? "1 item" : $"{c} items";

    string KindShort(ExpressionNode n)
    {
        if (n.isVirtualFolder) return "Virtual menu";
        switch (n.type)
        {
            case NodeType.Folder:  return "Folder";
            case NodeType.SubMenu: return "Sub-menu";
            case NodeType.Slider:  return "Slider";
            default:               return "Toggle";
        }
    }

    string SourceLong(ExpressionNode n)
    {
        switch (SourceCategoryOf(n))
        {
            case "VRCFuryToggle":       return "VRCFury Toggle";
            case "FullController":      return "VRCFury Full Controller";
            case "VirtualFolder":       return "Virtual Menu";
            case "VRCNative":           return "VRC Menu";
            case "FCPrefixFolder":      return "VRCFury Full Controller";
            case "MoveGeneratedFolder": return "Moved Folder";
            default:                    return "Folder";
        }
    }

    string Describe(ExpressionNode n)
    {
        return SourceLong(n);
    }

    // ── badges, explained in full in the details pane ─────────────────────────
    struct BadgeInfo
    {
        public string detail; public Color color;
        public BadgeInfo(Color color, string detail) { this.color = color; this.detail = detail; }
    }

    [System.NonSerialized] List<BadgeInfo> badgeScratch = new List<BadgeInfo>();

    List<BadgeInfo> BadgesOf(ExpressionNode n)
    {
        var b = badgeScratch; b.Clear();
        if (n.sourceControl != null && !n.isFromFullController)
            b.Add(new BadgeInfo(new Color(0.25f, 0.55f, 0.95f), "VRC Menu"));
        if (n.sourceObject != null)
            b.Add(new BadgeInfo(new Color(0.62f, 0.38f, 0.92f), "VRCFury Toggle"));
        if (n.isFromFullController)
            b.Add(new BadgeInfo(new Color(0.93f, 0.56f, 0.20f),
                "VRCFury Full Controller"));
        if (n.isFCPrefixFolder && !n.isFromFullController)
            b.Add(new BadgeInfo(new Color(0.80f, 0.48f, 0.16f), "VRCFury Full Controller"));
        if (n.isVirtualFolder)
            b.Add(new BadgeInfo(new Color(0.25f, 0.71f, 0.66f),
                "Virtual Menu"));
        if (n.hasManagedMove)
            b.Add(new BadgeInfo(new Color(0.30f, 0.70f, 0.40f), "Moved"));
        if (n.hasManagedIcon)
            b.Add(new BadgeInfo(new Color(0.82f, 0.65f, 0.18f), "Custom Icon"));
        if (n.duplicateSourceCount > 1)
            b.Add(new BadgeInfo(new Color(0.88f, 0.30f, 0.30f),
                $"{n.duplicateSourceCount} sources"));
        if (n.nameContainsSeparator)
            b.Add(new BadgeInfo(new Color(0.90f, 0.35f, 0.20f),
                "Name contains /"));
        return b;
    }

}
