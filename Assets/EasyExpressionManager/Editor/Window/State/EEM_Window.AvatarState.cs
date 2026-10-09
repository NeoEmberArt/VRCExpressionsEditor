using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public partial class EASYExpressionManager
{
    static readonly Dictionary<GameObject, EEMAvatarState> liveAvatarStates = new Dictionary<GameObject, EEMAvatarState>();

    string ComputeAvatarKey() => AvatarState()?.avatarKey;

    static string ValidOwnerId(UnityEngine.Object obj)
    {
        var gid = GlobalObjectId.GetGlobalObjectIdSlow(obj);
        return gid.identifierType != 0 && gid.targetObjectId != 0 && gid.assetGUID.ToString() != new string('0', 32) ? gid.ToString() : "";
    }

    EEMAvatarState AvatarState()
    {
        if (!avatar) return null;
        if (liveAvatarStates.TryGetValue(avatar, out var existing) && existing) return existing;
        string folder = DataFolder() + "/AvatarState";
        EnsureProjectFolder(folder);
        AssetDatabase.ImportAsset(folder, ImportAssetOptions.ForceSynchronousImport);
        string ownerId = ValidOwnerId(avatar);
        string sessionKey = SessionState.GetString("EEM.Avatar." + avatar.GetInstanceID(), "");
        if (!string.IsNullOrEmpty(ownerId) || sessionKey != "")
        {
            foreach (string guid in AssetDatabase.FindAssets("t:EEMAvatarState", new[] { folder }))
            {
                var state = AssetDatabase.LoadAssetAtPath<EEMAvatarState>(AssetDatabase.GUIDToAssetPath(guid));
                if (state && (state.ownerIds.Contains(ownerId) || state.avatarKey == sessionKey))
                {
                    liveAvatarStates[avatar] = state;
                    SessionState.SetString("EEM.Avatar." + avatar.GetInstanceID(), state.avatarKey);
                    return state;
                }
            }
        }
        var created = CreateInstance<EEMAvatarState>();
        created.avatarKey = "av_" + Guid.NewGuid().ToString("N");
        if (!string.IsNullOrEmpty(ownerId)) created.ownerIds.Add(ownerId);
        string legacy = LegacyAvatarKey();
        if (!string.IsNullOrEmpty(legacy)) created.legacyKeys.Add(legacy);
        liveAvatarStates[avatar] = created;
        SessionState.SetString("EEM.Avatar." + avatar.GetInstanceID(), created.avatarKey);
        AssetDatabase.CreateAsset(created, folder + "/" + created.avatarKey + ".asset");
        ImportLegacyVirtualFolders(created, legacy);
        AssetDatabase.SaveAssetIfDirty(created);
        return created;
    }

    void ImportLegacyVirtualFolders(EEMAvatarState state, string legacy)
    {
        if (string.IsNullOrEmpty(legacy)) return;
        string oldFile = DataFolder() + "/VirtualFolders/" + legacy + ".json";
        var paths = new List<string>();
        if (File.Exists(ProjectToAbsolute(oldFile)))
        {
            try
            {
                var data = MiniJson.AsDict(MiniJson.Parse(File.ReadAllText(ProjectToAbsolute(oldFile))));
                paths = MiniJson.List(data, "paths").OfType<string>().Select(EEMPath.Norm).Where(p => p != "").ToList();
            }
            catch (Exception ex) { Debug.LogWarning("[EASY] Legacy virtual menus could not be read: " + ex.Message); }
        }
        else
        {
            string oldPrefs = EditorPrefs.GetString(VF_PREFS_PREFIX + LegacyPrefsSuffix(), "");
            if (!string.IsNullOrEmpty(oldPrefs))
            {
                try { paths = JsonUtility.FromJson<StringListWrapper>(oldPrefs)?.items ?? paths; }
                catch (Exception ex) { Debug.LogWarning("[EASY] Legacy virtual menus could not be read: " + ex.Message); }
            }
        }
        if (paths.Count == 0) return;
        // Prefab-key files were shared by every instance. Assign their contents
        // explicitly, retaining the original file for other instances to recover.
        if (legacy.StartsWith("pfb_", StringComparison.Ordinal) &&
            !EditorUtility.DisplayDialog("Assign legacy virtual menus",
                "Older versions shared these virtual menus between every instance of this prefab. " +
                "Copy them into this avatar's separate history? The old file will be kept.",
                "Use on this avatar", "Start empty")) return;
        state.virtualFolders = paths.Distinct().ToList();
        EditorUtility.SetDirty(state);
    }

    void RefreshAvatarIdentity(Scene scene = default)
    {
        foreach (var pair in liveAvatarStates.ToArray())
        {
            if (!pair.Key || !pair.Value) continue;
            if (scene.IsValid() && pair.Key.scene != scene) continue;
            string owner = ValidOwnerId(pair.Key);
            if (!string.IsNullOrEmpty(owner) && !pair.Value.ownerIds.Contains(owner))
            {
                pair.Value.ownerIds.Add(owner);
                EditorUtility.SetDirty(pair.Value);
            }
            RefreshAddedComponentIds(pair.Value);
            AssetDatabase.SaveAssetIfDirty(pair.Value);
        }
    }

    static string OwnershipSession
    {
        get
        {
            string value = SessionState.GetString("EEM.OwnershipSession", "");
            if (value == "") SessionState.SetString("EEM.OwnershipSession", value = Guid.NewGuid().ToString("N"));
            return value;
        }
    }

    void RegisterPluginAddition(Component component)
    {
        var state = AvatarState();
        if (!state || !component) return;
        Undo.RecordObject(state, "Track expression component");
        state.additions.Add(new EEMAddedComponent
        {
            stableId = ValidOwnerId(component), session = OwnershipSession, instanceId = component.GetInstanceID(),
        });
        EditorUtility.SetDirty(state);
    }

    bool IsPluginAddition(Component component)
    {
        var state = AvatarState();
        string id = ValidOwnerId(component);
        return state && state.additions.Any(a =>
            (a.session == OwnershipSession && a.instanceId == component.GetInstanceID()) ||
            (!string.IsNullOrEmpty(id) && a.stableId == id));
    }

    static void RefreshAddedComponentIds(EEMAvatarState state)
    {
        foreach (var added in state.additions)
        {
            if (added.session != OwnershipSession) continue;
            var obj = EditorUtility.InstanceIDToObject(added.instanceId);
            if (!obj) continue;
            string id = ValidOwnerId(obj);
            if (!string.IsNullOrEmpty(id) && added.stableId != id)
            {
                added.stableId = id;
                EditorUtility.SetDirty(state);
            }
        }
    }
}
