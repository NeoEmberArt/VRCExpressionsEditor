// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — Move / Rename dialog
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public class MoveDialog : EditorWindow
{
    private string fromPath, toPath;
    private System.Action<string, string> onConfirm;
    private List<string> allPaths = new List<string>();
    private PathFieldState fromState = new PathFieldState();
    private PathFieldState toState   = new PathFieldState();

    public static void Open(string from, string suggestedTo,
                            List<string> knownPaths,
                            System.Action<string, string> onConfirm)
    {
        var w = GetWindow<MoveDialog>(true, "Move / Rename Menu Item", true);
        w.fromPath  = from;
        w.toPath    = suggestedTo;
        w.onConfirm = onConfirm;
        w.allPaths  = knownPaths ?? new List<string>();
        w.minSize   = w.maxSize = new Vector2(500, 360);
        w.ShowUtility();
    }

    void OnGUI()
    {
        GUILayout.Space(8);
        EditorGUILayout.LabelField(
            "Adds a VRCFury | Move Menu Item to the ExpressionsManager object.\n" +
            "Paths are automatically flattened to prevent build stack-overflow errors.",
            EditorStyles.wordWrappedMiniLabel);
        GUILayout.Space(6);

        EditorGUILayout.LabelField("From Path (current)", EditorStyles.boldLabel);
        fromPath = PathFieldGUI.Draw(fromPath, allPaths, fromState, "MoveFrom");
        GUILayout.Space(3);

        EditorGUILayout.LabelField("To Path  (blank = delete)", EditorStyles.boldLabel);
        toPath = PathFieldGUI.Draw(toPath, allPaths, toState, "MoveTo");

        string f = (fromPath ?? "").Trim(), t = (toPath ?? "").Trim();
        bool cycle = !string.IsNullOrEmpty(f) && !string.IsNullOrEmpty(t) && EEMPath.IsUnder(t, f);
        if (cycle)
            EditorGUILayout.HelpBox("Destination is inside the item being moved — that would create a loop.",
                                    MessageType.Error);

        GUILayout.Space(8);
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(cycle))
        {
            GUI.backgroundColor = Color.cyan;
            if (GUILayout.Button("Apply", GUILayout.Height(26))) { onConfirm?.Invoke(f, t); Close(); }
            GUI.backgroundColor = Color.white;
        }
        if (GUILayout.Button("Cancel", GUILayout.Height(26))) Close();
        EditorGUILayout.EndHorizontal();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// FOLDER PICKER  (multi-select move destination)
// ═══════════════════════════════════════════════════════════════════════════════

