// Easy Expression Manager: save icons and protect assets shared by other menus or avatars.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    // ── saving ────────────────────────────────────────────────────────────────

    void SnapIcon(ExpressionNode n)
    {
        if (n == null) return;
        EnsureIconTargets(n, force: true);   // re-capture: the pose may have changed
        if (!IconRenderer.HasContent) { ShowNotification(new GUIContent("Pick an object to photograph first")); return; }

        Texture2D shot;
        try { shot = IconRenderer.Render(IG, IG.size, 2); }
        catch (System.Exception ex) { EditorUtility.DisplayDialog("Couldn't make the icon", ex.Message, "OK"); return; }
        if (!shot) { ShowNotification(new GUIContent("Nothing visible to photograph")); return; }

        byte[] png = shot.EncodeToPNG();
        DestroyImmediate(shot);

        bool   overwriting = IG.overwrite && CanOverwriteIcon(n) && !IconUsedElsewhere(n.icon);
        string path;
        if (overwriting) path = AssetDatabase.GetAssetPath(n.icon);
        else
        {
            string folder = IconFolderFor(avatar);
            EnsureAssetFolder(folder);
            path = UniqueIconPath(folder, SafeFileName(DisplayOf(n)));
        }

        File.WriteAllBytes(FullPathOf(path), png);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        ConfigureIconImporter(path, IG.size);

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (tex && n.icon != tex) { pendingIconNode = n; pendingIcon = tex; }
        Debug.Log($"[EASY] {(overwriting ? "Updated" : "Saved")} icon for \"{DisplayOf(n)}\": {path}");
        ShowNotification(new GUIContent(overwriting ? "Icon file updated" : "Icon saved and set"));
        iconPreviewDirty = true;
        Repaint();   // the new icon is applied on the next draw
    }

    bool IconUsedElsewhere(Texture2D icon)
    {
        int references = 0;
        var seen = new HashSet<Component>();
        var loadedScenes = new HashSet<string>();
        foreach (var descriptor in Resources.FindObjectsOfTypeAll<VRCAvatarDescriptor>())
        {
            if (!descriptor || EditorUtility.IsPersistent(descriptor)) continue;
            if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewSceneObject(descriptor)) continue;
            int menuContexts = MenuUsesIcon(descriptor.expressionsMenu, icon) ? 1 : 0;
            loadedScenes.Add(descriptor.gameObject.scene.path);
            foreach (var c in descriptor.GetComponentsInChildren<Component>(true))
            {
                if (!c || !seen.Add(c)) continue;
                string type = c.GetType().FullName ?? "";
                if (!type.StartsWith("VF.") && !type.Contains("VRCFury")) continue;
                var so = new SerializedObject(c);
                var menus = so.FindProperty("content.menus");
                if (menus != null && menus.isArray)
                    for (int i = 0; i < menus.arraySize; i++)
                        if (MenuUsesIcon(menus.GetArrayElementAtIndex(i).FindPropertyRelative("menu.objRef")?.objectReferenceValue as VRCExpressionsMenu, icon))
                            menuContexts++;
                var it = so.GetIterator();
                while (it.Next(true))
                    if (it.propertyType == SerializedPropertyType.ObjectReference && it.objectReferenceValue == icon &&
                        ++references > 1) return true;
            }
            if (menuContexts > 1 || (descriptor.gameObject != avatar && menuContexts > 0)) return true;
        }
        // Menus can be shared across avatars or exist outside the current tree.
        foreach (string guid in AssetDatabase.FindAssets("t:VRCExpressionsMenu"))
            foreach (var menu in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)).OfType<VRCExpressionsMenu>())
                foreach (var c in menu.controls)
                {
                    if (c == null) continue;
                    if (c.icon == icon && ++references > 1) return true;
                    foreach (var label in c.labels ?? new VRCExpressionsMenu.Control.Label[0])
                        if (label.icon == icon && ++references > 1) return true;
                }
        string iconPath = AssetDatabase.GetAssetPath(icon);
        // Protect references in prefab assets and scenes that aren't open.
        if (!string.IsNullOrEmpty(iconPath))
        {
            var owners = AssetDatabase.FindAssets("t:Prefab t:Scene")
                .Select(AssetDatabase.GUIDToAssetPath).Where(path => !loadedScenes.Contains(path)).Distinct().ToArray();
            if (owners.Length > 0 && AssetDatabase.GetDependencies(owners, true).Contains(iconPath)) return true;
        }
        return references > 1;
    }

    static bool MenuUsesIcon(VRCExpressionsMenu rootMenu, Texture2D icon)
    {
        if (!icon) return false;
        var pending = new Stack<VRCExpressionsMenu>();
        var seen = new HashSet<VRCExpressionsMenu>();
        if (rootMenu) pending.Push(rootMenu);
        while (pending.Count > 0)
        {
            var menu = pending.Pop();
            if (!menu || !seen.Add(menu)) continue;
            foreach (var control in menu.controls)
            {
                if (control == null) continue;
                if (control.icon == icon || (control.labels?.Any(label => label.icon == icon) ?? false)) return true;
                if (control.subMenu) pending.Push(control.subMenu);
            }
        }
        return false;
    }

    string IconRoot => (IG.folder ?? IconGenSettings.DefaultFolder).TrimEnd('/');

    string IconFolderFor(GameObject av) => IconRoot + "/" + SafeFileName(av ? av.name : "Avatar");

    bool CanOverwriteIcon(ExpressionNode n)
    {
        if (n == null || !n.icon) return false;
        string p = AssetDatabase.GetAssetPath(n.icon);
        if (string.IsNullOrEmpty(p) || !p.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)) return false;
        return p.StartsWith(IconRoot + "/") || p.StartsWith(IconGenSettings.DefaultFolder + "/");
    }

    static string SafeFileName(string s)
    {
        s = EEMPath.Strip(s ?? "");
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
        s = s.Replace('/', ' ').Replace('\\', ' ').Replace('.', ' ');
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length > 60) s = s.Substring(0, 60).Trim();
        return string.IsNullOrEmpty(s) ? "Icon" : s;
    }

    static string FullPathOf(string assetPath) =>
        Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath);

    static string UniqueIconPath(string folder, string baseName)
    {
        string p = $"{folder}/{baseName}.png";
        for (int i = 2; File.Exists(FullPathOf(p)); i++) p = $"{folder}/{baseName} ({i}).png";
        return p;
    }

    static void EnsureAssetFolder(string folder)
    {
        var parts = folder.Split('/');
        string cur = parts[0];   // "Assets"
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }

    static void ConfigureIconImporter(string path, int size)
    {
        if (!(AssetImporter.GetAtPath(path) is TextureImporter imp)) return;
        imp.textureType         = TextureImporterType.Default;
        imp.alphaSource         = TextureImporterAlphaSource.FromInput;
        imp.alphaIsTransparency = true;
        imp.sRGBTexture         = true;
        imp.mipmapEnabled       = false;
        imp.wrapMode            = TextureWrapMode.Clamp;
        imp.npotScale           = TextureImporterNPOTScale.None;
        imp.maxTextureSize      = Mathf.Max(32, size);
        imp.textureCompression  = TextureImporterCompression.Compressed;
        imp.SaveAndReimport();
    }

    void PickIconFolder()
    {
        string start = Directory.Exists(FullPathOf(IconRoot)) ? FullPathOf(IconRoot) : Application.dataPath;
        string abs = EditorUtility.OpenFolderPanel("Save generated icons in…", start, "");
        if (string.IsNullOrEmpty(abs)) return;
        abs = abs.Replace('\\', '/');
        string data = Application.dataPath.Replace('\\', '/');
        if (!abs.StartsWith(data))
        {
            EditorUtility.DisplayDialog("Pick a folder inside Assets",
                "Icons have to live inside this project's Assets folder so they can be used as menu icons.", "OK");
            return;
        }
        IG.folder = "Assets" + abs.Substring(data.Length);
        IG.Save();
    }

    void ShowIconFolder()
    {
        string folder = IconFolderFor(avatar);
        if (!AssetDatabase.IsValidFolder(folder)) folder = IconRoot;
        var obj = AssetDatabase.LoadAssetAtPath<Object>(folder);
        if (obj) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
        else ShowNotification(new GUIContent("No icons saved yet"));
    }
}
