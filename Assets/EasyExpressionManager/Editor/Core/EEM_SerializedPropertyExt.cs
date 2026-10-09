// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — tiny SerializedProperty extension
// ═════════════════════════════════════════════════════════════════════════════

using UnityEditor;

// ─── tiny SerializedProperty extension ──────────────────────────────────────
internal static class SPExt
{
    internal static void SetBool(this SerializedProperty p, bool v)
    { if (p != null) p.boolValue = v; }
}
