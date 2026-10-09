using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

// SDK preprocessors receive the avatar being built. Source packages are untouched.
public sealed class EEMMoveBuildOrder : IVRCSDKPreprocessAvatarCallback
{
    // The installed VRCFury avatar preprocessor runs at -10000.
    public int callbackOrder => -10001;
    public bool OnPreprocessAvatar(GameObject avatar)
    {
        if (!avatar || !avatar.transform.Find("ExpressionsManager")) return true;
        try { return EASYExpressionManager.PrepareMenuMoveOrder(avatar); }
        catch (Exception ex)
        {
            Debug.LogWarning("[EASY] Menu moves could not be ordered; continuing with VRCFury's normal order: " + ex.Message);
            return true;
        }
    }
}

public partial class EASYExpressionManager
{
    internal static bool PrepareMenuMoveOrder(GameObject builtAvatar)
    {
        var helper = CreateInstance<EASYExpressionManager>();
        // Avoid opening a window, capturing snapshots or creating storage.
        helper.avatar = builtAvatar;
        try
        {
            var records = helper.ReadMoveRecords();
            var ordered = helper.OrderMovesByDependency(records, helper.BuildMoveSourceTree());
            if (records.Select(r => r.comp).SequenceEqual(ordered.Select(r => r.comp))) return true;
            var manager = builtAvatar.transform.Find(MANAGER_OBJECT_NAME).gameObject;
            var copies = ordered.Select(r => new
            {
                type = r.comp.GetType(),
                feature = new SerializedObject(r.comp).FindProperty("content").managedReferenceValue,
                enabled = !(r.comp is Behaviour behaviour) || behaviour.enabled,
            }).ToList();
            // Keep original records until all replacements have serialized successfully.
            // A failed creation leaves the normal VRCFury order intact.
            var replacements = new List<Component>();
            try
            {
                foreach (var copy in copies)
                {
                    var component = manager.AddComponent(copy.type);
                    if (!component) throw new InvalidOperationException("Could not copy a menu move component.");
                    replacements.Add(component);
                    var so = new SerializedObject(component);
                    var content = so.FindProperty("content");
                    if (content == null) throw new InvalidOperationException("Menu move content was not found.");
                    content.managedReferenceValue = copy.feature;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    if (component is Behaviour behaviour) behaviour.enabled = copy.enabled;
                }
            }
            catch
            {
                foreach (var component in replacements) if (component) DestroyImmediate(component);
                throw;
            }
            foreach (var record in records) DestroyImmediate(record.comp);
            return true;
        }
        finally { DestroyImmediate(helper); }
    }
}
