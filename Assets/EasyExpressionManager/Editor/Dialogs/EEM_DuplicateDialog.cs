// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — duplicate dialog
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public class DuplicateDialog : EditorWindow
{
    private string newName;
    private System.Action<string> onConfirm;

    public static void Open(string suggestedName, System.Action<string> onConfirm)
    {
        var w = GetWindow<DuplicateDialog>(true, "Duplicate Menu Item", true);
        w.newName   = suggestedName;
        w.onConfirm = onConfirm;
        w.minSize   = w.maxSize = new Vector2(380, 160);
        w.ShowUtility();
    }

    void OnGUI()
    {
        GUILayout.Space(8);
        EditorGUILayout.LabelField(
            "Enter a name for the duplicate. It will be placed in the same folder as the original.",
            EditorStyles.wordWrappedMiniLabel);
        GUILayout.Space(6);
        EditorGUILayout.LabelField("New Name", EditorStyles.boldLabel);
        GUI.SetNextControlName("DupName");
        newName = EditorGUILayout.TextField(newName);
        GUILayout.Space(8);
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor = new Color(1f, 0.85f, 0.3f);
        if (GUILayout.Button("Duplicate", GUILayout.Height(26))) { onConfirm?.Invoke((newName ?? "").Trim()); Close(); }
        GUI.backgroundColor = Color.white;
        if (GUILayout.Button("Cancel",    GUILayout.Height(26))) Close();
        EditorGUILayout.EndHorizontal();
        EditorGUI.FocusTextInControl("DupName");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// CREATE VIRTUAL MENU DIALOG
// ═══════════════════════════════════════════════════════════════════════════════

