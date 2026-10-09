// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — footer status bar
// ═════════════════════════════════════════════════════════════════════════════
//
// Messages appear in a slim bar at the bottom of the window instead of Unity's
// centre-of-window notification, which sat right on top of the menu tree.
// Information fades after a few seconds; warnings stay until dismissed.
//
// ShowNotification is re-declared here, so every existing call in the window
// lands in the footer automatically.

using UnityEngine;
using UnityEditor;

public partial class EASYExpressionManager
{
    [System.NonSerialized] string statusText;
    [System.NonSerialized] bool   statusWarn;
    [System.NonSerialized] double statusUntil;
    // what this frame draws — fixed at Layout so the footer never appears
    // half-way through a pass (that would desync IMGUI's layout)
    [System.NonSerialized] string uiStatusText;
    [System.NonSerialized] bool   uiStatusWarn;

    const double STATUS_SECONDS = 8.0;

    /// Routes the window's notifications into the footer.
    public new void ShowNotification(GUIContent notification) => SetStatus(notification?.text, false);

    void SetStatus(string text, bool warn)
    {
        statusText  = string.IsNullOrWhiteSpace(text) ? null : text;
        statusWarn  = warn;
        statusUntil = warn ? double.MaxValue : EditorApplication.timeSinceStartup + STATUS_SECONDS;
        EditorApplication.update -= StatusTick;
        if (statusText != null && !warn) EditorApplication.update += StatusTick;
        Repaint();
    }

    void StatusTick()
    {
        if (EditorApplication.timeSinceStartup < statusUntil) return;
        EditorApplication.update -= StatusTick;
        statusText = null;
        if (this != null) Repaint();
    }

    void SnapshotStatus()
    {
        if (Event.current.type != EventType.Layout) return;
        uiStatusText = statusText;
        uiStatusWarn = statusWarn;
    }

    void DrawFooter()
    {
        if (string.IsNullOrEmpty(uiStatusText)) return;

        Fill(GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)), LineCol);
        var area = EditorGUILayout.BeginHorizontal(new GUIStyle { padding = new RectOffset(6, 4, 3, 3) });
        Fill(area, HeaderBg);
        if (uiStatusWarn)
        {
            var wi = Ico("console.warnicon.sml");
            if (wi) GUILayout.Label(wi, GUILayout.Width(16), GUILayout.Height(16));
        }
        GUILayout.Label(new GUIContent(uiStatusText, uiStatusText), EditorStyles.wordWrappedMiniLabel,
                        GUILayout.MinWidth(20), GUILayout.ExpandWidth(true));
        if (GUILayout.Button(new GUIContent("×", "Dismiss"), EditorStyles.miniButton, GUILayout.Width(20)))
            Defer(() => { statusText = null; EditorApplication.update -= StatusTick; });
        EditorGUILayout.EndHorizontal();
    }
}
