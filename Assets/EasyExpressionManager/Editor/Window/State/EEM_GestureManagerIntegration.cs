using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

[InitializeOnLoad]
internal static class EEMGestureManagerIntegration
{
    const string ComponentName = "BlackStartX.GestureManager.GestureManager";
    const string ButtonName = "EEM.GestureManagerLauncher";
    static readonly Type ManagerType = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType(ComponentName, false)).FirstOrDefault(t => t != null && typeof(Component).IsAssignableFrom(t));
    static readonly Type ManagerEditorType = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("BlackStartX.GestureManager.Editor.GestureManagerEditor", false)).FirstOrDefault(t => t != null);
    static readonly FieldInfo InspectorRoot = ManagerEditorType?.GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance);
    // Harmony is bundled by VRCFury, but EEM must still compile and run for
    // projects that only have the VRChat SDK. Resolve it dynamically so the
    // optional Gesture Manager integration disappears cleanly when neither
    // dependency is installed.
    static readonly Type HarmonyType = FindLoadedType("HarmonyLib.Harmony");
    static readonly Type HarmonyMethodType = FindLoadedType("HarmonyLib.HarmonyMethod");
    static readonly object InspectorPatch;

    static Type FindLoadedType(string fullName) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);

    static object MakeHarmonyMethod(MethodInfo method)
    {
        if (HarmonyMethodType == null || method == null) return null;
        var ctor = HarmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) });
        if (ctor != null) return ctor.Invoke(new object[] { method });
        return null;
    }

    static void Patch(MethodInfo original, MethodInfo prefix = null,
                      MethodInfo postfix = null, MethodInfo finalizer = null)
    {
        if (InspectorPatch == null || original == null || HarmonyMethodType == null) return;
        var patch = HarmonyType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Patch")
            .FirstOrDefault(m => m.GetParameters().Length == 5 &&
                                 typeof(System.Reflection.MethodBase).IsAssignableFrom(m.GetParameters()[0].ParameterType));
        if (patch == null) return;
        patch.Invoke(InspectorPatch, new object[]
        {
            original,
            MakeHarmonyMethod(prefix),
            MakeHarmonyMethod(postfix),
            null,
            MakeHarmonyMethod(finalizer)
        });
    }

    static EEMGestureManagerIntegration()
    {
        if (ManagerType == null || ManagerEditorType == null || HarmonyType == null || HarmonyMethodType == null) return;
        var createInspector = ManagerEditorType.GetMethod("CreateInspectorGUI", BindingFlags.Public | BindingFlags.Instance);
        if (createInspector == null || createInspector.DeclaringType != ManagerEditorType) return;
        try
        {
            InspectorPatch = Activator.CreateInstance(HarmonyType, "Neo.EEM.GestureManagerLauncher");
            Patch(createInspector, postfix: typeof(EEMGestureManagerIntegration)
                .GetMethod(nameof(InspectorCreated), BindingFlags.Static | BindingFlags.NonPublic));
            AssemblyReloadEvents.beforeAssemblyReload += Unpatch;
            // Also repair inspectors that were already constructed during reload.
            // This runs once; newly opened and locked inspectors use the postfix.
            EditorApplication.delayCall += AttachExistingInspectors;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[EASY] Could not attach the Gesture Manager inspector launcher: " + ex.Message);
        }
        if (InspectorPatch != null) AttachInitializationScope();
    }

    static void AttachInitializationScope()
    {
        // Evaluating Gesture Manager's avatar graph invokes Unity's native
        // material drawers. Use Fury's existing suppression scope only while
        // initializing the avatar; ordinary material inspectors stay active.
        var module = ManagerEditorType.Assembly.GetType("BlackStartX.GestureManager.Editor.Modules.Vrc3.ModuleVrc3", false);
        var initialize = module?.GetMethod("InitForAvatar", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (initialize == null || initialize.DeclaringType != module) return;
        try
        {
            Patch(initialize,
                prefix: typeof(EEMGestureManagerIntegration).GetMethod(nameof(BeginAvatarInitialization), BindingFlags.Static | BindingFlags.NonPublic),
                finalizer: typeof(EEMGestureManagerIntegration).GetMethod(nameof(EndAvatarInitialization), BindingFlags.Static | BindingFlags.NonPublic));
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[EASY] Could not attach the Gesture Manager material initialization scope: " + ex.Message);
        }
    }

    static void Unpatch()
    {
        if (InspectorPatch == null || HarmonyType == null) return;
        var id = HarmonyType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(InspectorPatch, null) as string;
        var unpatch = HarmonyType.GetMethod("UnpatchAll", BindingFlags.Public | BindingFlags.Instance,
                                            null, new[] { typeof(string) }, null);
        if (unpatch != null && id != null) unpatch.Invoke(InspectorPatch, new object[] { id });
    }

    static void BeginAvatarInitialization(out IDisposable __state)
    {
        __state = EEMIconRenderer.SuppressPreviewMaterialDrawers();
    }

    static void EndAvatarInitialization(IDisposable __state)
    {
        __state?.Dispose();
    }

    internal static bool Available => ManagerType != null;

    internal static void AddInspectorButton(Editor editor)
    {
        if (!editor || ManagerEditorType == null || !ManagerEditorType.IsInstanceOfType(editor)) return;
        InspectorCreated(editor, InspectorRoot?.GetValue(editor) as VisualElement);
    }

    static void AttachExistingInspectors()
    {
        foreach (var editor in Resources.FindObjectsOfTypeAll(ManagerEditorType).OfType<Editor>()) AddInspectorButton(editor);
    }

    static void InspectorCreated(Editor __instance, VisualElement __result)
    {
        if (!__instance || !__instance.target || ManagerType == null || !ManagerType.IsInstanceOfType(__instance.target)) return;
        var root = __result;
        if (root == null || root.Q(ButtonName) != null) return;
        var button = new Button(EASYExpressionManager.Open)
        { name = ButtonName, text = "Easy Expression Manager [by Neo]" };
        button.style.alignSelf = Align.Center;
        button.style.width = 250;
        button.style.height = 25;
        button.style.marginTop = 5;
        button.style.marginBottom = 5;
        root.Add(button);
    }

    internal static Component FindOrCreateManager()
    {
        if (!Available) return null;
        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            var scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) continue;
            foreach (var gameObject in scene.GetRootGameObjects())
            {
                var first = gameObject.GetComponentsInChildren(ManagerType, true).FirstOrDefault();
                if (first) return first;
            }
        }
        // Prefer the supplied prefab, including its serialized default settings.
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/vrchat.blackstartx.gesture-manager/GestureManager.prefab");
        if (!prefab || !prefab.GetComponent(ManagerType))
            prefab = AssetDatabase.FindAssets("t:Prefab GestureManager")
                .Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .FirstOrDefault(p => p && p.GetComponent(ManagerType));
        var created = prefab ? PrefabUtility.InstantiatePrefab(prefab) as GameObject : new GameObject("GestureManager");
        if (!created) throw new InvalidOperationException("Gesture Manager could not be created.");
        Undo.RegisterCreatedObjectUndo(created, "Create Gesture Manager");
        return created.GetComponent(ManagerType) ?? Undo.AddComponent(created, ManagerType);
    }

    internal static void EnterPlayMode()
    {
        var manager = FindOrCreateManager();
        if (!manager) return;
        Selection.activeGameObject = manager.gameObject;
        EditorGUIUtility.PingObject(manager.gameObject);
        if (!EditorApplication.isPlayingOrWillChangePlaymode)
            EditorApplication.delayCall += EditorApplication.EnterPlaymode;
    }
}

public partial class EASYExpressionManager
{
    void DrawGestureManagerPlayButton()
    {
        if (!EEMGestureManagerIntegration.Available) return;
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            if (GUILayout.Button("Enter Play-Mode", GUILayout.Height(25)))
                Defer(EEMGestureManagerIntegration.EnterPlayMode);
    }
}
