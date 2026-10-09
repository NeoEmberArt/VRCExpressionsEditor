// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — create virtual menu dialog
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public class CreateFolderDialog : EditorWindow
{
    private string folderName = "New Virtual Menu";
    private System.Action<string> onConfirm;

    public static void Open(System.Action<string> onConfirm)
    {
        var w = GetWindow<CreateFolderDialog>(true, "Create Virtual Menu", true);
        w.folderName = "New Virtual Menu";
        w.onConfirm  = onConfirm;
        w.minSize    = w.maxSize = new Vector2(360, 170);
        w.ShowUtility();
    }

    void OnGUI()
    {
        GUILayout.Space(8);
        EditorGUILayout.LabelField(
            "Creates a plugin-only virtual menu. No VRC asset is created.\nMove items here to update their path prefix.",
            EditorStyles.wordWrappedMiniLabel);
        GUILayout.Space(6);
        EditorGUILayout.LabelField("Menu Name", EditorStyles.boldLabel);
        GUI.SetNextControlName("FolderName");
        folderName = EditorGUILayout.TextField(folderName);
        GUILayout.Space(8);
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor = new Color(0.6f, 0.9f, 1f);
        if (GUILayout.Button("Create", GUILayout.Height(26))) { onConfirm?.Invoke((folderName ?? "").Trim()); Close(); }
        GUI.backgroundColor = Color.white;
        if (GUILayout.Button("Cancel", GUILayout.Height(26))) Close();
        EditorGUILayout.EndHorizontal();
        EditorGUI.FocusTextInControl("FolderName");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// TEXT PROMPT (snapshot label)
// ═══════════════════════════════════════════════════════════════════════════════

