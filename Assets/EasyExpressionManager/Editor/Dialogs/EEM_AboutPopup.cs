// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — about popup
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public class AboutPopup : EditorWindow
{
    string version;

    public static void Open(string version)
    {
        var w = GetWindow<AboutPopup>(true, "About Easy Expression Manager", true);
        w.version = version;
        w.minSize = w.maxSize = new Vector2(340, 250);
        w.ShowUtility();
    }

    void OnGUI()
    {
        GUILayout.Space(10);
        EditorGUILayout.LabelField("Easy Expression Manager",
            new GUIStyle(EditorStyles.whiteLargeLabel) { fontStyle = FontStyle.Bold });
        EditorGUILayout.LabelField("Version " + version, EditorStyles.miniLabel);
        GUILayout.Space(8);
        EditorGUILayout.LabelField("Need help? Contact NeoEmberArts:", EditorStyles.wordWrappedMiniLabel);
        GUILayout.Space(6);
        GUI.backgroundColor = new Color(0.28f, 0.40f, 0.85f);
        if (GUILayout.Button("Discord",  GUILayout.Height(24))) Application.OpenURL("https://discord.com/users/616724478419795977");
        GUI.backgroundColor = new Color(0.17f, 0.60f, 0.85f);
        if (GUILayout.Button("Telegram", GUILayout.Height(24))) Application.OpenURL("https://t.me/NeoEmberArts");
        GUI.backgroundColor = new Color(0.70f, 0.25f, 0.20f);
        if (GUILayout.Button("Email",    GUILayout.Height(24))) Application.OpenURL("mailto:authoranimation@gmail.com");
        GUI.backgroundColor = Color.white;
        GUILayout.Space(10);
        if (GUILayout.Button("Close", GUILayout.Height(24))) Close();
        GUILayout.Space(6);
    }
}
