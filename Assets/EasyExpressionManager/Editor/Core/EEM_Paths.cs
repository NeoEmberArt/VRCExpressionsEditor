// ═══════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — Path utilities
//
// v2.2.0 path model
// ─────────────────
// Two representations exist and they are NOT interchangeable:
//
//  • INTERNAL path  — segments joined with '/', and any literal '/' inside a
//                     segment escaped as "\/" (backslash itself escaped as "\\").
//                     Used for every comparison, tree lookup and bookkeeping.
//                     This is what makes a control literally named
//                     "Wetness/Shine" addressable instead of looking like a
//                     folder called "Wetness" holding "Shine".
//
//  • VRCFURY path   — segments joined with '/', with literal '/' escaped as '\/'.
//                     Other backslashes are literal. Produced when writing fields.
//
// Bookkeeping comparisons are ORDINAL. Menu lookup tries exact names first,
// then VRCFury's case/rich-text fallback rules.
// ═══════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal static class EEMPath
{
    static readonly Regex RichText = new Regex("<[^>]*>", RegexOptions.Compiled);

    // ── display ───────────────────────────────────────────────────────────────

    /// Rich-text stripped, for DRAWING ONLY. Never use for identity or paths.
    public static string Strip(string v) =>
        string.IsNullOrEmpty(v) ? v : RichText.Replace(v, "").Trim();

    /// What to actually put on a row. Falls back to a placeholder when a name is
    /// nothing but markup (GoGo Loco does this), instead of drawing a blank row.
    public static string Display(string rawName, NodeType type, int siblingIndex)
    {
        string s = Strip(rawName ?? "");
        if (!string.IsNullOrWhiteSpace(s)) return s;
        return "(unnamed " + type + (siblingIndex >= 0 ? " #" + (siblingIndex + 1) : "") + ")";
    }

    /// How VRCFury compares menu names when it looks one up: rich text removed,
    /// case ignored, line breaks and repeated spaces collapsed
    /// (MenuManager.FindControlsWithName). Use for "same name?" checks.
    public static string MatchKey(string raw)
    {
        string s = Strip(raw ?? "").Replace("\\n", " ").Replace("\n", " ").ToLowerInvariant();
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// Trim and drop a stray "name:" prefix (seen in some hand-edited YAML).
    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Trim();
        if (s.ToLowerInvariant().StartsWith("name:")) s = s.Substring(5);
        return s.Trim();
    }

    // ── escaping ──────────────────────────────────────────────────────────────

    public static string EscapeSeg(string seg)
    {
        // Keep an empty control distinct from the root (zero segments).
        // A literal backslash followed by zero is escaped normally, so it
        // cannot collide with this internal empty-segment representation.
        if (string.IsNullOrEmpty(seg)) return "\\0";
        return seg.Replace("\\", "\\\\").Replace("/", "\\/");
    }

    public static string UnescapeSeg(string seg)
    {
        if (seg == "\\0") return "";
        if (string.IsNullOrEmpty(seg)) return seg ?? "";
        var sb = new StringBuilder();
        for (int i = 0; i < seg.Length; i++)
        {
            if (seg[i] == '\\' && i + 1 < seg.Length) { sb.Append(seg[i + 1]); i++; }
            else sb.Append(seg[i]);
        }
        return sb.ToString();
    }

    public static bool SegmentHasSeparator(string rawSegment) =>
        !string.IsNullOrEmpty(rawSegment) && rawSegment.Contains("/");

    // ── split / join ──────────────────────────────────────────────────────────

    /// Splits an INTERNAL path into RAW (unescaped) segments.
    public static string[] SplitPath(string internalPath)
    {
        if (string.IsNullOrEmpty(internalPath)) return new string[0];
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < internalPath.Length; i++)
        {
            char c = internalPath[i];
            if (c == '\\' && i + 1 < internalPath.Length) { sb.Append(c).Append(internalPath[i + 1]); i++; continue; }
            if (c == '/')
            {
                parts.Add(UnescapeSeg(sb.ToString()));
                sb.Length = 0;
                continue;
            }
            sb.Append(c);
        }
        parts.Add(UnescapeSeg(sb.ToString()));
        return parts.ToArray();
    }

    /// Joins RAW segments into an INTERNAL path.
    public static string JoinPath(IEnumerable<string> rawParts)
    {
        if (rawParts == null) return "";
        return string.Join("/", rawParts.Select(EscapeSeg));
    }

    /// Canonical internal form.
    public static string Norm(string internalPath) => JoinPath(SplitPath(internalPath));

    // ── VRCFury interop ───────────────────────────────────────────────────────

    /// INTERNAL → VRCFury path, escaping literal slashes in each segment.
    public static string ToVrcf(string internalPath) =>
        string.Join("/", SplitPath(internalPath).Select(segment => segment.Replace("/", "\\/")));

    /// VRCFury path → INTERNAL, preserving escaped slashes and literal backslashes.
    public static string FromVrcf(string vrcfPath)
    {
        if (string.IsNullOrEmpty(vrcfPath)) return "";
        var parts = new List<string>();
        var segment = new StringBuilder();
        for (int i = 0; i < vrcfPath.Length; i++)
        {
            if (vrcfPath[i] == '\\' && i + 1 < vrcfPath.Length && vrcfPath[i + 1] == '/')
            { segment.Append('/'); i++; }
            else if (vrcfPath[i] == '/')
            { parts.Add(segment.ToString()); segment.Length = 0; }
            else segment.Append(vrcfPath[i]);
        }
        parts.Add(segment.ToString());
        return JoinPath(parts);
    }

    /// A control-targeting field can name a blank root control. A folder path
    /// or a move's blank destination still uses FromVrcf("") for the root/delete.
    public static string FromVrcfControl(string vrcfPath) =>
        string.IsNullOrEmpty(vrcfPath) ? JoinPath(new[] { "" }) : FromVrcf(vrcfPath);

    /// Returns true when a raw menu name contains syntax reserved by VRCFury
    /// for selecting a duplicate ancestor folder. This is deliberately about a
    /// raw name, not an already-generated EEM path: a generated "Name.dup.1"
    /// selector is valid, while a literal raw folder with that spelling cannot
    /// be addressed through VRCFury's path API.
    public static bool IsVrcfReservedAncestorSegment(string rawSegment) =>
        !string.IsNullOrEmpty(rawSegment) &&
        rawSegment.IndexOf(".dup.", System.StringComparison.Ordinal) >= 0;

    /// Encodes raw menu-name segments using the installed VRCFury path grammar.
    /// VRCFury treats a backslash immediately before the next path separator as
    /// the escape sequence for a literal slash. A raw ancestor ending in '\\'
    /// therefore cannot be represented when another segment follows. Raw
    /// ancestor names containing '.dup.' are also rejected because VRCFury
    /// reserves that spelling for duplicate-folder selectors. A final control
    /// named '.dup.N' remains valid and literal; callers moving a submenu must
    /// reject that case separately because submenu destinations are ancestor
    /// paths in VRCFury.
    public static bool TryEncodeVrcfRawPath(IEnumerable<string> rawParts,
                                             out string vrcfPath,
                                             out string reason)
    {
        var parts = (rawParts ?? Enumerable.Empty<string>())
            .Select(part => part ?? "")
            .ToList();
        for (int i = 0; i < parts.Count - 1; i++)
        {
            string part = parts[i];
            if (IsVrcfReservedAncestorSegment(part))
            {
                vrcfPath = null;
                reason = "The ancestor name '" + part + "' contains VRCFury's reserved .dup. selector syntax.";
                return false;
            }
            if (part.EndsWith("\\", System.StringComparison.Ordinal))
            {
                vrcfPath = null;
                reason = "The ancestor name '" + part + "' ends in a backslash and cannot be represented in a VRCFury path.";
                return false;
            }
        }

        vrcfPath = string.Join("/", parts.Select(part => part.Replace("/", "\\/")));
        reason = null;
        return true;
    }

    // ── comparison ────────────────────────────────────────────────────────────

    public static bool PathEq(string a, string b) =>
        string.Equals(Norm(a), Norm(b), System.StringComparison.Ordinal);

    /// Case-insensitive compare — used ONLY to warn about case-only collisions,
    /// never to decide whether two paths are the same thing.
    public static bool PathEqIgnoreCase(string a, string b) =>
        string.Equals(Norm(a), Norm(b), System.StringComparison.OrdinalIgnoreCase);

    public static bool IsUnder(string path, string parent)
    {
        string p = Norm(path), par = Norm(parent);
        if (string.IsNullOrEmpty(par)) return !string.IsNullOrEmpty(p);
        return p.StartsWith(par + "/", System.StringComparison.Ordinal);
    }

    public static bool IsAtOrUnder(string path, string parent) =>
        PathEq(path, parent) || IsUnder(path, parent);

    // ── mutation ──────────────────────────────────────────────────────────────

    public static string RewriteSeg(string path, string oldPfx, string newPfx)
    {
        string np = Norm(path), op = Norm(oldPfx), np2 = Norm(newPfx);
        if (string.IsNullOrEmpty(op)) return np;
        if (PathEq(np, op)) return np2;
        if (IsUnder(np, op))
        {
            string suffix = np.Substring(op.Length);          // begins with '/'
            return string.IsNullOrEmpty(np2) ? suffix.TrimStart('/') : np2 + suffix;
        }
        return np;
    }

    public static string ReplaceLastSegment(string internalPath, string newRawLeaf)
    {
        var parts = SplitPath(internalPath);
        if (parts.Length == 0) return JoinPath(new[] { newRawLeaf });
        parts[parts.Length - 1] = newRawLeaf;
        return JoinPath(parts);
    }

    public static string ParentPath(string internalPath)
    {
        var parts = SplitPath(internalPath);
        if (parts.Length <= 1) return "";
        return JoinPath(parts.Take(parts.Length - 1));
    }

    /// RAW leaf name.
    public static string LeafOf(string internalPath)
    {
        var parts = SplitPath(internalPath);
        return parts.Length == 0 ? "" : parts[parts.Length - 1];
    }

    public static string Combine(string folderInternalPath, string rawLeaf)
    {
        var parts = SplitPath(folderInternalPath).ToList();
        parts.Add(rawLeaf);
        return JoinPath(parts);
    }

    public static int Depth(string internalPath) => SplitPath(internalPath).Length;
}
