// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — folder picker (multi-select move destination)
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public class PickFolderDialog : EditorWindow
{
    private string targetPath = "";
    private string header = "";
    private List<string> itemPreview = new List<string>();
    private List<string> folderPaths = new List<string>();
    private System.Action<string> onConfirm;
    private PathFieldState state = new PathFieldState();
    private Vector2 listScroll;

    public static void Open(string header, List<string> itemsBeingMoved, List<string> folderPaths,
                            string suggested, System.Action<string> onConfirm)
    {
        var w = GetWindow<PickFolderDialog>(true, "Move Selected Items", true);
        w.header      = header;
        w.itemPreview = itemsBeingMoved ?? new List<string>();
        w.folderPaths = folderPaths ?? new List<string>();
        w.targetPath  = suggested ?? "";
        w.onConfirm   = onConfirm;
        w.minSize     = w.maxSize = new Vector2(500, 380);
        w.ShowUtility();
    }

    void OnGUI()
    {
        GUILayout.Space(8);
        EditorGUILayout.LabelField(header, EditorStyles.wordWrappedMiniLabel);
        GUILayout.Space(6);

        EditorGUILayout.LabelField("Destination folder  (blank = top level)", EditorStyles.boldLabel);
        targetPath = PathFieldGUI.Draw(targetPath, folderPaths, state, "PickFolder");

        GUILayout.Space(6);
        EditorGUILayout.LabelField($"Moving {itemPreview.Count} item(s):", EditorStyles.miniBoldLabel);
        listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.Height(120));
        foreach (var p in itemPreview) EditorGUILayout.LabelField("• " + p, EditorStyles.miniLabel);
        EditorGUILayout.EndScrollView();

        GUILayout.Space(8);
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor = Color.cyan;
        if (GUILayout.Button("Move", GUILayout.Height(26))) { onConfirm?.Invoke((targetPath ?? "").Trim()); Close(); }
        GUI.backgroundColor = Color.white;
        if (GUILayout.Button("Cancel", GUILayout.Height(26))) Close();
        EditorGUILayout.EndHorizontal();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// DUPLICATE DIALOG
// ═══════════════════════════════════════════════════════════════════════════════

