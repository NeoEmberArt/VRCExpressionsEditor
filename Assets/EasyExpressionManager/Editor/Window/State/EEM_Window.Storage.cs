// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — plugin data folder & avatar-scoped storage
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
    // ═══════════════════════════════════════════════════════════════════════════
    // DEVELOPER SETTINGS  (Editor/developer.json)
    //
    // Parsed with MiniJson — the same parser already used for snapshots,
    // structure exports, and the debug log — instead of a hand-rolled
    // `Key = Value` line reader. Lets a maintainer flip dev-only tooling on or
    // off (currently `DebugJSON`) and override the displayed version
    // (`VersionCode`) without touching code. Re-checked against the file's own
    // last-write time on every read — it isn't a .cs file, so editing it never
    // triggers Unity's domain reload, and an already-open window would
    // otherwise keep serving a stale cached value from before the edit.
    //
    // Each flag picks its own default for "missing file / missing key /
    // unparseable value" via DeveloperFlag's defaultValue parameter — DebugJSON
    // defaults to OFF, so a build shipped without this file (or a malformed
    // one) shows no developer tooling rather than silently exposing it.
    // ═══════════════════════════════════════════════════════════════════════════

    Dictionary<string, object> cachedDeveloperSettings;
    System.DateTime cachedDeveloperSettingsWriteTime;
    string cachedDeveloperSettingsPath;

    bool developerSettingsMissingWarned;

    Dictionary<string, object> LoadDeveloperSettings()
    {
        string editorFolder = FindEditorFolder();
        string path = string.IsNullOrEmpty(editorFolder) ? null : ProjectToAbsolute(editorFolder + "/developer.json");

        // Not a .cs file, so editing it never triggers Unity's domain reload —
        // an already-open window's cached copy would otherwise never notice
        // the file changed underneath it. Cheap to check the write time on
        // every call; only actually re-read the file when it's changed.
        bool fileExists = !string.IsNullOrEmpty(path) && File.Exists(path);
        var writeTime = fileExists ? File.GetLastWriteTimeUtc(path) : System.DateTime.MinValue;

        if (!fileExists && !developerSettingsMissingWarned)
        {
            developerSettingsMissingWarned = true;
            Debug.LogWarning(string.IsNullOrEmpty(editorFolder)
                ? "[EASY] Could not locate the plugin's own Editor folder, so developer.json can't be read. " +
                  "Developer settings are using their defaults."
                : $"[EASY] No developer.json found at '{path}'. Developer settings are using their defaults.");
        }

        bool stale = cachedDeveloperSettings == null ||
                     cachedDeveloperSettingsPath != path ||
                     cachedDeveloperSettingsWriteTime != writeTime;
        if (!stale) return cachedDeveloperSettings;

        cachedDeveloperSettings = new Dictionary<string, object>(System.StringComparer.OrdinalIgnoreCase);
        cachedDeveloperSettingsPath = path;
        cachedDeveloperSettingsWriteTime = writeTime;

        if (!fileExists) return cachedDeveloperSettings;

        try
        {
            var parsed = MiniJson.AsDict(MiniJson.Parse(File.ReadAllText(path)));
            if (parsed != null)
                foreach (var kv in parsed) cachedDeveloperSettings[kv.Key] = kv.Value;
            else
                Debug.LogWarning("[EASY] developer.json did not parse as a JSON object — check for a stray comma " +
                                 "or missing brace. Developer settings are using their defaults.");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[EASY] Could not parse developer.json, defaulting all dev settings: " + ex.Message);
        }
        return cachedDeveloperSettings;
    }

    bool DeveloperFlag(string key, bool defaultValue = true)
    {
        var settings = LoadDeveloperSettings();
        if (!settings.TryGetValue(key, out object val) || val == null) return defaultValue;
        if (val is bool b) return b;
        // tolerate a quoted "true"/"false"/"1"/"0" too, since it's an easy typo to make
        string s = val.ToString().Trim();
        if (bool.TryParse(s, out bool parsed)) return parsed;
        if (s == "1") return true;
        if (s == "0") return false;
        return defaultValue;
    }

    string DeveloperString(string key, string defaultValue)
    {
        var settings = LoadDeveloperSettings();
        if (!settings.TryGetValue(key, out object val) || val == null) return defaultValue;
        string s = val.ToString().Trim();
        return s.Length > 0 ? s : defaultValue;
    }

    /// The plugin's own Editor folder, resolved once and cached for the whole
    /// session. Previously computed independently in two places (here and
    /// DataFolder()) via MonoScript reflection alone — but DataFolder() has
    /// ALWAYS had a second line of defense that this never did: if reflection
    /// comes back empty, it falls back to the literal path
    /// "Assets/EasyExpressionManager". That fallback is very likely the ONLY
    /// reason DataFolder() (and therefore snapshots/virtual folders) ever
    /// worked reliably — it would keep working even if MonoScript resolution
    /// silently failed on every single call, which is exactly the kind of
    /// failure that's easy to never notice. FindEditorFolder() had no
    /// equivalent, so if reflection wasn't resolving, developer.json had
    /// nothing to fall back to at all. Given the same fallback here.
    string cachedEditorFolder;

    string FindEditorFolder()
    {
        if (!string.IsNullOrEmpty(cachedEditorFolder)) return cachedEditorFolder;

        try
        {
            var script = MonoScript.FromScriptableObject(this);
            string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
            if (!string.IsNullOrEmpty(scriptPath))
            {
                var dir = Path.GetDirectoryName(scriptPath)?.Replace("\\", "/");
                while (!string.IsNullOrEmpty(dir) &&
                       !dir.EndsWith("/Editor", System.StringComparison.Ordinal) &&
                       dir.Contains("/"))
                    dir = Path.GetDirectoryName(dir)?.Replace("\\", "/");
                if (!string.IsNullOrEmpty(dir) && dir.EndsWith("/Editor", System.StringComparison.Ordinal))
                    cachedEditorFolder = dir;
            }
        }
        catch { /* fall through to the hardcoded fallback below */ }

        // Reflection came back empty (or threw) — check the conventional
        // install path directly, the same one DataFolder() has always fallen
        // back to. Verified against disk rather than assumed, since it's only
        // correct if the plugin is actually installed there.
        if (string.IsNullOrEmpty(cachedEditorFolder))
        {
            const string conventional = "Assets/EasyExpressionManager/Editor";
            if (Directory.Exists(ProjectToAbsolute(conventional)))
                cachedEditorFolder = conventional;
        }

        // Only a successful resolution gets cached — a failure (both
        // reflection AND the conventional path missing) tries again on the
        // next call instead of being stuck on whatever the very first
        // attempt happened to return.
        return cachedEditorFolder;
    }

    string cachedDataFolder;

    string DataFolder()
    {
        if (!string.IsNullOrEmpty(cachedDataFolder)) return cachedDataFolder;

        string editorFolder = FindEditorFolder();
        string baseFolder = !string.IsNullOrEmpty(editorFolder)
            ? editorFolder.Substring(0, editorFolder.Length - "/Editor".Length)
            : null;

        // Packages/ is immutable — fall back into Assets so writes actually land.
        if (string.IsNullOrEmpty(baseFolder) ||
            baseFolder.StartsWith("Packages/", System.StringComparison.Ordinal))
            baseFolder = "Assets/EasyExpressionManager";

        string data = baseFolder + "/Data";
        EnsureProjectFolder(data);
        cachedDataFolder = data;
        return data;
    }

    static void EnsureProjectFolder(string projectRelativePath)
    {
        if (string.IsNullOrEmpty(projectRelativePath)) return;
        string full = Path.Combine(Directory.GetCurrentDirectory(), projectRelativePath);
        try { if (!Directory.Exists(full)) Directory.CreateDirectory(full); }
        catch (System.Exception ex) { Debug.LogWarning("[EASY] Could not create folder '" + projectRelativePath + "': " + ex.Message); }
    }

    static string ProjectToAbsolute(string projectRelativePath) =>
        Path.Combine(Directory.GetCurrentDirectory(), projectRelativePath);


    /// Cache the selected avatar's identity for the session.
    string cachedAvatarKey;

    string AvatarKey()
    {
        if (!string.IsNullOrEmpty(cachedAvatarKey)) return cachedAvatarKey;
        return cachedAvatarKey = ComputeAvatarKey();
    }

    string LegacyAvatarKey()
    {
        if (!avatar) return null;

        string assetPath = AssetDatabase.GetAssetPath(avatar);
        if (!string.IsNullOrEmpty(assetPath))
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (!string.IsNullOrEmpty(guid)) return "guid_" + guid;
        }

        // A scene instance of a prefab keeps its identity even if renamed or moved.
        string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatar);
        if (!string.IsNullOrEmpty(prefabPath))
        {
            string guid = AssetDatabase.AssetPathToGUID(prefabPath);
            if (!string.IsNullOrEmpty(guid)) return "pfb_" + guid;
        }

        var scene = avatar.scene;
        if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
            return "scn_" + StableHash(scene.path + "|" + GetHierarchyPath(avatar.transform));

        return "tmp_" + StableHash(GetHierarchyPath(avatar.transform));
    }

    /// GetHashCode() is not guaranteed stable across runtimes — this is.
    static string StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619u; }
            return h.ToString("X8");
        }
    }

    static string GetHierarchyPath(Transform t)
    {
        var parts = new List<string>();
        while (t != null) { parts.Insert(0, t.name); t = t.parent; }
        return string.Join("/", parts);
    }

    string VirtualFolderFilePath()
    {
        string key = AvatarKey();
        if (string.IsNullOrEmpty(key)) return null;
        string dir = DataFolder() + "/VirtualFolders";
        EnsureProjectFolder(dir);
        return dir + "/" + key + ".json";
    }

    void LoadVirtualFolders()
    {
        var state = AvatarState();
        virtualFolderPaths = state ? new List<string>(state.virtualFolders) : new List<string>();
    }

    string LegacyPrefsSuffix()
    {
        if (!avatar) return "";
        string assetPath = AssetDatabase.GetAssetPath(avatar);
        if (!string.IsNullOrEmpty(assetPath))
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (!string.IsNullOrEmpty(guid)) return guid;
        }
        var scene = avatar.scene;
        if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
            return (scene.path + "|" + GetHierarchyPath(avatar.transform)).GetHashCode().ToString("X8");
        return "unsaved_" + GetHierarchyPath(avatar.transform).GetHashCode().ToString("X8");
    }

    void SaveVirtualFolders()
    {
        var state = AvatarState();
        if (!state || state.virtualFolders.SequenceEqual(virtualFolderPaths)) return;
        Undo.RecordObject(state, "Change virtual menus");
        state.virtualFolders = new List<string>(virtualFolderPaths);
        EditorUtility.SetDirty(state);
        AssetDatabase.SaveAssetIfDirty(state);
    }

    void AddVirtualFolder(string internalPath)
    {
        internalPath = EEMPath.Norm(internalPath);
        if (string.IsNullOrEmpty(internalPath)) return;
        if (!virtualFolderPaths.Any(p => EEMPath.PathEq(p, internalPath)))
        {
            virtualFolderPaths.Add(internalPath);
            SaveVirtualFolders();
        }
    }

    void RemoveVirtualFolder(string internalPath)
    {
        internalPath = EEMPath.Norm(internalPath);
        int removed = virtualFolderPaths.RemoveAll(p =>
            EEMPath.PathEq(p, internalPath) || EEMPath.IsUnder(p, internalPath));
        if (removed > 0) SaveVirtualFolders();
    }

    /// Where this virtual menu is registered (see ExpressionNode.virtualPath).
    string VirtualPathOf(ExpressionNode n) =>
        n != null && !string.IsNullOrEmpty(n.virtualPath) ? n.virtualPath : PathOf(n);

    /// Moves the registration of a virtual menu that has been relocated in the
    /// tree. Uses the node's registered path, so it also works for a virtual menu
    /// sitting inside a folder that a MoveMenuItem record has already moved.
    void MoveVirtualFolderRegistration(ExpressionNode n, string newInternalPath)
    {
        if (n == null || !n.isVirtualFolder) return;
        string reg = VirtualPathOf(n);
        if (!EEMPath.PathEq(reg, newInternalPath)) RenameVirtualFolderPath(reg, newInternalPath);
    }

    bool IsRegisteredVirtualPath(string internalPath) =>
        virtualFolderPaths.Any(p => EEMPath.PathEq(p, internalPath));

    void RenameVirtualFolderPath(string oldPath, string newPath)
    {
        oldPath = EEMPath.Norm(oldPath);
        newPath = EEMPath.Norm(newPath);
        bool changed = false;
        for (int i = 0; i < virtualFolderPaths.Count; i++)
        {
            string p = EEMPath.Norm(virtualFolderPaths[i]);
            string rewritten = EEMPath.RewriteSeg(p, oldPath, newPath);
            if (!EEMPath.PathEq(rewritten, p)) { virtualFolderPaths[i] = rewritten; changed = true; }
        }
        if (changed) SaveVirtualFolders();
    }

    [System.Serializable]
    class StringListWrapper { public List<string> items = new List<string>(); }

}
