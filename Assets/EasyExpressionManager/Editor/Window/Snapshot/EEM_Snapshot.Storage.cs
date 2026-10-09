// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — snapshot file locations & baseline
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
    static readonly Regex InstanceIdPattern = new Regex("\"instanceID\"\\s*:\\s*(-?\\d+)", RegexOptions.Compiled);
    const string SNAPSHOT_FORMAT = "eem-snapshot-1";

    // ── file locations ────────────────────────────────────────────────────────

    string SnapshotFolder()
    {
        string key = AvatarKey();
        if (string.IsNullOrEmpty(key)) return null;
        string dir = DataFolder() + "/Snapshots";
        EnsureProjectFolder(dir);
        dir += "/" + key;
        EnsureProjectFolder(dir);
        return dir;
    }

    List<string> ListSnapshotFiles()
    {
        var result = new List<string>();
        string dir = SnapshotFolder();
        if (dir == null) return result;
        string abs = ProjectToAbsolute(dir);
        if (!Directory.Exists(abs)) return result;
        try
        {
            result.AddRange(Directory.GetFiles(abs, "*.json").OrderByDescending(f => f));
            var state = AvatarState();
            foreach (string legacy in state.legacyKeys)
            {
                string legacyDir = ProjectToAbsolute(DataFolder() + "/Snapshots/" + legacy);
                if (Directory.Exists(legacyDir)) result.AddRange(Directory.GetFiles(legacyDir, "*.json").OrderByDescending(f => f));
            }
        }
        catch (System.Exception ex) { Debug.LogWarning("[EASY] Could not list snapshots: " + ex.Message); }
        return result;
    }

    // Only automatic safety snapshots rotate; baseline and named saves remain.
    void PruneAutomaticSnapshots(string folder)
    {
        var files = Directory.GetFiles(ProjectToAbsolute(folder), "*.json")
            .Where(path => Path.GetFileName(path).StartsWith("auto_", System.StringComparison.Ordinal) ||
                Path.GetFileName(path).StartsWith("snap_", System.StringComparison.Ordinal) &&
                Path.GetFileName(path).Contains("_Before dissolving "))
            .Select(path => new FileInfo(path)).OrderByDescending(file => file.Name).ToList();
        long bytes = 0;
        for (int i = 0; i < files.Count; i++)
        {
            bytes += files[i].Length;
            if (i == 0 || (i < 10 && bytes <= 8 * 1024 * 1024)) continue;
            string path = folder + "/" + files[i].Name;
            if (!AssetDatabase.DeleteAsset(path)) File.Delete(files[i].FullName);
        }
    }

    bool HasBaselineSnapshot() =>
        ListSnapshotFiles().Any(f => Path.GetDirectoryName(f).Replace("\\", "/").EndsWith("/" + AvatarKey(), System.StringComparison.Ordinal) &&
            Path.GetFileName(f).StartsWith("baseline_", System.StringComparison.Ordinal));

    /// Taken automatically the first time an avatar is selected, so there is
    /// always something to fall back to.
    void EnsureBaselineSnapshot()
    {
        if (!avatar) return;
        if (HasBaselineSnapshot()) return;
        try
        {
            string file = WriteSnapshot("Baseline (before any edits)", true);
            if (file != null) Debug.Log($"[EASY] Baseline snapshot saved to {file}");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[EASY] Could not write the baseline snapshot: " + ex.Message);
        }
    }

}
