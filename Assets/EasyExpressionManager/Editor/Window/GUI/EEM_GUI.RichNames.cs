// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — rich-text names (display only)
// ═════════════════════════════════════════════════════════════════════════════
//
// Menu names often carry VRChat/TMP markup ("<b><size=23><color=#00EDFF>…").
// When "Show name formatting" is on, rows and the details title render the
// BOLD and COLOUR parts of that markup and drop everything else (size, voffset,
// line-height, italics, …) — sizes in particular wreck row layout. The stored
// name is never touched; the name field always shows and edits the raw text.
//
// Unity's editor prints malformed rich text literally, so the markup is rebuilt
// from scratch: only <b> and <color=…> are re-emitted, closers that don't match
// the innermost open tag are dropped, and anything left open is closed at the
// end. Colours are nudged just enough to stay readable on the editor skin.

using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;

public partial class EASYExpressionManager
{
    const string RICH_NAMES_PREF = "EASY_RichNames";

    [System.NonSerialized] bool richNamesLoaded, richNamesValue;

    bool RichNames
    {
        get
        {
            if (!richNamesLoaded) { richNamesValue = EditorPrefs.GetBool(RICH_NAMES_PREF, true); richNamesLoaded = true; }
            return richNamesValue;
        }
        set
        {
            richNamesValue = value; richNamesLoaded = true;
            EditorPrefs.SetBool(RICH_NAMES_PREF, value);
            richCache.Clear();
            Repaint();
        }
    }

    static readonly Regex AnyTag = new Regex("<[^>]*>", RegexOptions.Compiled);
    static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

    [System.NonSerialized] Dictionary<string, string> richCache = new Dictionary<string, string>();
    [System.NonSerialized] bool richCacheSkin;

    /// Text to draw for a row: formatted when the setting is on and the name has
    /// formatting, otherwise the plain display name.
    string RowText(ExpressionNode n)
    {
        if (!RichNames || n == null || n == root) return DisplayOf(n);
        string rich = RichMarkup(n.rawName);
        return string.IsNullOrEmpty(rich) ? DisplayOf(n) : rich;
    }

    /// Whether a style drawing RowText(n) needs rich text switched on.
    bool RowIsRich(ExpressionNode n) => RichNames && n != null && n != root && !string.IsNullOrEmpty(RichMarkup(n.rawName));

    /// Rebuilt markup containing only <b> and <color>, or null when the name
    /// has no formatting worth showing (or no visible text at all).
    string RichMarkup(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('<') < 0) return null;
        if (richCacheSkin != Pro) { richCache.Clear(); richCacheSkin = Pro; }
        if (richCache.TryGetValue(raw, out var hit)) return hit;

        var sb    = new StringBuilder();
        var open  = new List<string>();     // "b" / "color", innermost last
        bool any  = false, visible = false;
        int  pos  = 0;

        foreach (Match m in AnyTag.Matches(raw))
        {
            visible |= AppendText(sb, raw.Substring(pos, m.Index - pos));
            pos = m.Index + m.Length;

            string inner   = m.Value.Substring(1, m.Value.Length - 2).Trim();
            bool   closing = inner.StartsWith("/");
            if (closing) inner = inner.Substring(1).Trim();
            int    eq      = inner.IndexOf('=');
            string tag     = (eq >= 0 ? inner.Substring(0, eq) : inner).Trim().ToLowerInvariant();
            string value   = eq >= 0 ? inner.Substring(eq + 1).Trim().Trim('"', '\'') : null;

            if (tag != "b" && tag != "color") continue;   // everything else is dropped

            if (closing)
            {
                if (open.Count > 0 && open[open.Count - 1] == tag)
                {
                    sb.Append("</").Append(tag).Append('>');
                    open.RemoveAt(open.Count - 1);
                }
                continue;   // mismatched closer — ignore rather than print it
            }

            if (tag == "b")
            {
                sb.Append("<b>"); open.Add("b"); any = true;
            }
            else if (value != null && ColorUtility.TryParseHtmlString(value, out var c))
            {
                sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(Readable(c))).Append('>');
                open.Add("color"); any = true;
            }
        }
        visible |= AppendText(sb, raw.Substring(pos));
        for (int i = open.Count - 1; i >= 0; i--) sb.Append("</").Append(open[i]).Append('>');

        string result = any && visible ? Spaces.Replace(sb.ToString(), " ").Trim() : null;
        richCache[raw] = result;
        return result;
    }

    /// Appends plain text with line breaks flattened. True if it had anything visible.
    static bool AppendText(StringBuilder sb, string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        text = text.Replace("\\n", " ").Replace("\n", " ").Replace("\r", " ");
        sb.Append(text);
        return !string.IsNullOrWhiteSpace(text);
    }

    /// Lightens colours that would vanish on the dark skin (and darkens ones that
    /// would vanish on the light skin), keeping the hue. Alpha is ignored.
    static Color Readable(Color c)
    {
        c.a = 1f;
        float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        // only colours that would genuinely disappear are touched (navy, dark
        // purple on the dark skin; yellow, cyan on the light one) — saturated
        // reds, greens, oranges are left exactly as authored
        if (Pro && lum < 0.18f)
            c = Color.Lerp(c, Color.white, (0.30f - lum) / Mathf.Max(0.0001f, 1f - lum));
        else if (!Pro && lum > 0.62f)
            c = Color.Lerp(c, Color.black, (lum - 0.50f) / Mathf.Max(0.0001f, lum));
        c.a = 1f;
        return c;
    }
}
