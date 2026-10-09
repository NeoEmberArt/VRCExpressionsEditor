// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — prefab handling & VRCFury discovery
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

    // SerializedObject edits and Undo component operations create prefab overrides.
    // Keep the connection to both the avatar prefab and nested clothing prefabs.

    /// A fresh read-only view of a component.
    SerializedObject ReadSO(SerializedObject so)
    {
        if (so?.targetObject == null) return so;
        var fresh = new SerializedObject(so.targetObject);
        fresh.Update();
        return fresh;
    }

    SerializedObject FreshSO(SerializedObject so)
    {
        if (so?.targetObject == null) return so;
        var fresh = new SerializedObject(so.targetObject);
        fresh.Update();
        return fresh;
    }


    System.Type ConcreteVrcfType()
    {
        if (cachedVrcfCompType != null) return cachedVrcfCompType;
        if (avatar)
            foreach (var comp in avatar.GetComponentsInChildren<Component>(true))
            {
                if (!comp || comp is Transform) continue;
                var t = comp.GetType(); if (t.IsAbstract) continue;
                string fn = t.FullName ?? "";
                if (!fn.Contains("VRCFury") && !fn.Contains("VF.")) continue;
                var so = new SerializedObject(comp);
                if (so.FindProperty("content") != null) return cachedVrcfCompType = t;
            }
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            foreach (var t in SafeTypes(asm))
                if (!t.IsAbstract && t.Name == "VRCFury" && typeof(Component).IsAssignableFrom(t))
                    return cachedVrcfCompType = t;
        return null;
    }

    System.Type FindFeatureType(string name)
    {
        if (featureTypeCache.TryGetValue(name, out var cached)) return cached;
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            foreach (var t in SafeTypes(asm))
                if (t.Name == name && IsVrcfFeatureType(t)) return featureTypeCache[name] = t;
        return null;
    }

    static bool IsVrcfFeatureType(System.Type type)
    {
        if (type == null || type.IsAbstract || type.IsInterface) return false;
        for (var parent = type.BaseType; parent != null; parent = parent.BaseType)
            if (parent.Name == "FeatureModel" &&
                ((parent.Namespace ?? "").StartsWith("VF.") || (parent.Namespace ?? "").Contains("VRCFury")))
                return true;
        return false;
    }

    System.Type FindOverrideMenuIconType()
    {
        const string key = "__OverrideMenuIcon";
        if (featureTypeCache.TryGetValue(key, out var cached)) return cached;
        foreach (var name in new[] { "SetIcon", "OverrideMenuIcon", "OverrideMenuItemIcon", "SetMenuIcon", "MenuIconOverride" })
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                foreach (var t in SafeTypes(asm))
                    if (t.Name == name && IsVrcfFeatureType(t)) return featureTypeCache[key] = t;

        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            foreach (var t in SafeTypes(asm))
            {
                if (!IsVrcfFeatureType(t)) continue;
                string fn = t.FullName ?? "";
                if (!fn.Contains("VRCFury") && !fn.Contains("VF.")) continue;
                var fields = t.GetFields(System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                bool hasPath = false, hasIcon = false;
                foreach (var f in fields)
                {
                    string fl = f.Name.ToLowerInvariant();
                    if (f.FieldType == typeof(string) && (fl.Contains("path") || fl.Contains("menu"))) hasPath = true;
                    if ((fl.Contains("icon") || fl.Contains("texture")) && !f.FieldType.IsPrimitive && f.FieldType != typeof(string)) hasIcon = true;
                }
                if (hasPath && hasIcon) return featureTypeCache[key] = t;
            }
        return null;
    }

    Component AddFeatureComponent(GameObject go, System.Type featureType)
    {
        var vrcfType = ConcreteVrcfType(); if (vrcfType == null) return null;
        var comp = Undo.AddComponent(go, vrcfType);
        if (!comp) return null;
        RegisterPluginAddition(comp);
        var so = new SerializedObject(comp); so.Update();
        var contentProp = so.FindProperty("content");
        if (contentProp?.propertyType == SerializedPropertyType.ManagedReference)
        {
            contentProp.managedReferenceValue = System.Activator.CreateInstance(featureType);
            so.ApplyModifiedProperties(); return comp;
        }
        var featuresProp = so.FindProperty("config.features");
        if (featuresProp != null && featuresProp.isArray)
        {
            featuresProp.InsertArrayElementAtIndex(featuresProp.arraySize);
            var elem = featuresProp.GetArrayElementAtIndex(featuresProp.arraySize - 1);
            if (elem.propertyType == SerializedPropertyType.ManagedReference)
                elem.managedReferenceValue = System.Activator.CreateInstance(featureType);
            so.ApplyModifiedProperties(); return comp;
        }
        Object.DestroyImmediate(comp); return null;
    }

    static System.Type[] SafeTypes(System.Reflection.Assembly asm)
    {
        try { return asm.GetTypes(); } catch { return new System.Type[0]; }
    }


    /// Resolve the VRCFury types BEFORE creating anything, so a project without
    /// VRCFury no longer ends up with a stray empty "ExpressionsManager" object.
    GameObject GetOrCreateManager()
    {
        if (!avatar) return null;
        var t = avatar.transform.Find(MANAGER_OBJECT_NAME);
        if (t != null) return t.gameObject;
        if (ConcreteVrcfType() == null)
        {
            Debug.LogError("[EASY] VRCFury was not found in this project — cannot create the ExpressionsManager object.");
            return null;
        }
        var go = new GameObject(MANAGER_OBJECT_NAME);
        Undo.RegisterCreatedObjectUndo(go, "Create ExpressionsManager");
        go.transform.SetParent(avatar.transform, false);
        return go;
    }

    bool HasManager() => avatar && avatar.transform.Find(MANAGER_OBJECT_NAME) != null;

    GameObject ManagerObjectOrNull()
    {
        var t = avatar ? avatar.transform.Find(MANAGER_OBJECT_NAME) : null;
        return t != null ? t.gameObject : null;
    }


    bool IsDirectToggle(ExpressionNode n) => n?.sourceObject != null && n.type == NodeType.Toggle;

    NodeType NodeTypeOf(VRCExpressionsMenu.Control c) =>
        c.type == VRCExpressionsMenu.Control.ControlType.SubMenu      ? NodeType.SubMenu :
        c.type == VRCExpressionsMenu.Control.ControlType.RadialPuppet ? NodeType.Slider  : NodeType.Toggle;

    void QueueRescan() => rescanQueued = true;

    void FlushRescan()
    {
        if (!rescanQueued) return;
        rescanQueued = false;
        Scan();
    }
}
