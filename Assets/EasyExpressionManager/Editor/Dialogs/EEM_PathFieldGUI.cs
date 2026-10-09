// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — shared autocomplete path field
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

internal class PathFieldState
{
    public List<string> suggestions = new List<string>();
    public bool focused;
    public Vector2 scroll;
}

internal static class PathFieldGUI
{
    const int MAX_SUGGEST = 6;

    public static string Draw(string value, List<string> allPaths, PathFieldState state, string controlName)
    {
        if (!string.IsNullOrEmpty(value))
        {
            EditorGUILayout.BeginHorizontal();
            var parts  = value.Split('/');
            var colors = new Color[]
            {
                new Color(0.65f, 0.65f, 0.65f),
                new Color(0.55f, 0.78f, 1.00f),
                new Color(0.55f, 1.00f, 0.65f),
                new Color(1.00f, 0.85f, 0.50f),
            };
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                var col = colors[Mathf.Min(i, colors.Length - 1)];
                GUILayout.Label(parts[i], new GUIStyle(EditorStyles.miniLabel)
                    { fontStyle = FontStyle.Bold, normal = { textColor = col } });
                if (i < parts.Length - 1)
                    GUILayout.Label("/", new GUIStyle(EditorStyles.miniLabel)
                        { normal = { textColor = new Color(0.4f, 0.4f, 0.4f) } });
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        GUI.SetNextControlName(controlName);
        string newVal    = EditorGUILayout.TextField(value);
        bool   isFocused = GUI.GetNameOfFocusedControl() == controlName;
        if (newVal != value) { value = newVal; Update(value, allPaths, state); }
        if (isFocused && !state.focused) { state.focused = true; Update(value, allPaths, state); }
        if (!isFocused) state.focused = false;

        if (isFocused && state.suggestions.Count > 0)
        {
            float h        = Mathf.Min(state.suggestions.Count, MAX_SUGGEST) * 18f;
            var scrollRect = EditorGUILayout.GetControlRect(false, h + 2);
            GUI.Box(scrollRect, GUIContent.none, EditorStyles.helpBox);
            var inner = new Rect(scrollRect.x + 2, scrollRect.y + 2, scrollRect.width - 4, h - 2);
            var view  = new Rect(0, 0, inner.width - 16, state.suggestions.Count * 18f);
            state.scroll = GUI.BeginScrollView(inner, state.scroll, view);
            for (int i = 0; i < state.suggestions.Count; i++)
            {
                var r = new Rect(0, i * 18f, view.width, 18f);
                if (GUI.Button(r, state.suggestions[i], EditorStyles.miniLabel))
                {
                    value = state.suggestions[i];
                    state.suggestions.Clear();
                    GUI.FocusControl(null);
                }
            }
            GUI.EndScrollView();
        }
        return value;
    }

    static void Update(string input, List<string> allPaths, PathFieldState state)
    {
        if (string.IsNullOrEmpty(input) || allPaths == null) { state.suggestions = new List<string>(); return; }
        string low = input.ToLowerInvariant();
        state.suggestions = allPaths
            .Where(p => p.ToLowerInvariant().Contains(low) && p != input)
            .OrderBy(p => p)
            .Take(MAX_SUGGEST)
            .ToList();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// MOVE DIALOG
// ═══════════════════════════════════════════════════════════════════════════════

