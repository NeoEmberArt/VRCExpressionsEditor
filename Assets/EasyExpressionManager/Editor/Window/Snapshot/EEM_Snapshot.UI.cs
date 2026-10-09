// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — snapshot menu UI
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{

    void ShowSnapshotMenu()
    {
        var menu = new GenericMenu();

        menu.AddItem(new GUIContent("Take Snapshot…"), false, () =>
        {
            TextPromptDialog.Open("Take Snapshot",
                "Give this snapshot a name so you can recognise it later.",
                "Snapshot " + System.DateTime.Now.ToString("HH:mm"),
                "Save", label =>
                {
                    try
                    {
                        SnapshotExpansionStates(root);
                        string file = null;
                        Undo.IncrementCurrentGroup();
                        Undo.SetCurrentGroupName("Take expression snapshot");
                        LogOp("Snapshot", "", "Snapshot", "", label, () => { file = WriteSnapshot(label, false); });
                        if (file != null)
                        {
                            Debug.Log("[EASY] Snapshot saved to " + file);
                            ShowNotification(new GUIContent("Snapshot saved"));
                        }
                    }
                    catch (System.Exception ex)
                    {
                        EditorUtility.DisplayDialog("Snapshot failed", ex.Message, "OK");
                    }
                });
        });

        var files = ListSnapshotFiles();
        menu.AddSeparator("");
        if (files.Count == 0)
        {
            menu.AddDisabledItem(new GUIContent("No snapshots yet"));
        }
        else
        {
            foreach (var f in files)
            {
                string display = DescribeSnapshotFile(f);
                string captured = f;
                menu.AddItem(new GUIContent("Restore/" + display), false, () => RestoreSnapshotInteractive(captured));
                menu.AddItem(new GUIContent("Restore and remove untracked additions/" + display), false,
                    () => RestoreSnapshotInteractive(captured, true));
            }
            menu.AddSeparator("");
            foreach (var f in files)
            {
                if (Path.GetFileName(f).StartsWith("baseline_", System.StringComparison.Ordinal)) continue;
                string display = DescribeSnapshotFile(f);
                string captured = f;
                menu.AddItem(new GUIContent("Delete/" + display), false, () =>
                {
                    if (!EditorUtility.DisplayDialog("Delete snapshot?", display, "Delete", "Cancel")) return;
                    try
                    {
                        if (!AssetDatabase.DeleteAsset(FileUtil.GetProjectRelativePath(captured)))
                            File.Delete(ProjectToAbsolute(captured));
                        Repaint();
                    }
                    catch (System.Exception ex) { Debug.LogError("[EASY] " + ex.Message); }
                });
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Open Snapshot Folder"), false, () =>
                EditorUtility.RevealInFinder(ProjectToAbsolute(SnapshotFolder() ?? DataFolder())));
        }

        menu.ShowAsContext();
    }

    string DescribeSnapshotFile(string projectPath)
    {
        string name = Path.GetFileNameWithoutExtension(projectPath) ?? "";
        try
        {
            var d = MiniJson.AsDict(MiniJson.Parse(File.ReadAllText(ProjectToAbsolute(projectPath))));
            string label = MiniJson.Str(d, "label", name);
            string when  = MiniJson.Str(d, "createdAt", "");
            bool baseline = MiniJson.Bool(d, "isBaseline");
            return (MiniJson.Str(d, "avatarKey") != AvatarKey() ? "[Legacy / different instance] " : "") + (baseline ? "★ " : "") + label + (string.IsNullOrEmpty(when) ? "" : "   (" + when + ")");
        }
        catch { return name; }
    }
}
