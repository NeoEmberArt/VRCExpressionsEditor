// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — generic text prompt (snapshot label, etc.)
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public class TextPromptDialog : EditorWindow
{
    private string value = "";
    private string message = "";
    private string okLabel = "OK";
    private System.Action<string> onConfirm;

    public static void Open(string title, string message, string initial, string okLabel,
                            System.Action<string> onConfirm)
    {
        var w = GetWindow<TextPromptDialog>(true, title, true);
        w.message   = message;
        w.value     = initial ?? "";
        w.okLabel   = okLabel;
        w.onConfirm = onConfirm;
        w.minSize   = w.maxSize = new Vector2(380, 160);
        w.ShowUtility();
    }

    void OnGUI()
    {
        GUILayout.Space(8);
        EditorGUILayout.LabelField(message, EditorStyles.wordWrappedMiniLabel);
        GUILayout.Space(6);
        GUI.SetNextControlName("PromptValue");
        value = EditorGUILayout.TextField(value);
        GUILayout.Space(8);
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor = new Color(0.6f, 0.9f, 1f);
        if (GUILayout.Button(okLabel, GUILayout.Height(26))) { onConfirm?.Invoke((value ?? "").Trim()); Close(); }
        GUI.backgroundColor = Color.white;
        if (GUILayout.Button("Cancel", GUILayout.Height(26))) Close();
        EditorGUILayout.EndHorizontal();
        EditorGUI.FocusTextInControl("PromptValue");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ABOUT POPUP
// ═══════════════════════════════════════════════════════════════════════════════

