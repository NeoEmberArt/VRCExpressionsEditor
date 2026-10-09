// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — auto icon: settings
// ═════════════════════════════════════════════════════════════════════════════
//
// One set of settings for every avatar, saved per user (EditorPrefs).

using UnityEngine;
using UnityEditor;

public enum IconStyle      { FullTexture, Silhouette, Shaded, OutlineOnly }
public enum IconBackground { Transparent, Colour }
public enum IconOutline    { None, Thin, Medium, Thick, Custom }

[System.Serializable]
public class IconGenSettings
{
    public const string DefaultFolder = "Assets/EasyExpressionManager_Generated/Icons";

    public float yaw   = 0f;     // around the avatar's up axis; 0 = facing the avatar's front
    public float pitch = 8f;     // looking down onto the item

    public IconStyle      style        = IconStyle.FullTexture;
    public Color          tint         = new Color(0.56f, 0.82f, 1f);     // light blue
    public IconBackground background   = IconBackground.Transparent;
    public Color          bgColor      = new Color(0.13f, 0.14f, 0.17f);
    public IconOutline    outline      = IconOutline.None;
    public Color          outlineColor = Color.white;
    public float          customOutlinePercent = 2.2f;

    public int    size    = 256;
    public float  padding = 0.08f;          // fraction of the icon left empty on each side
    public string folder  = DefaultFolder;  // a folder inside Assets; "<avatar>" is added below it
    public bool   overwrite = true;         // replace the icon this generator made for the item (new file if none)
    public bool   open;                     // generator panel expanded — closed until the user opens it

    // v2: defaults changed in 2.2.0 (overwrite on, panel closed); a fresh key so
    // settings saved by a pre-release build don't keep the old defaults
    const string PREF = "EASY_IconGenSettings_v2";

    public static IconGenSettings Load()
    {
        var s = new IconGenSettings();
        string json = EditorPrefs.GetString(PREF, "");
        if (!string.IsNullOrEmpty(json))
        {
            try { JsonUtility.FromJsonOverwrite(json, s); } catch { s = new IconGenSettings(); }
        }
        if (s.size != 128 && s.size != 256 && s.size != 512) s.size = 256;
        s.padding = Mathf.Clamp(s.padding, 0f, 0.3f);
        s.customOutlinePercent = Mathf.Clamp(s.customOutlinePercent, 0f, 20f);
        if (string.IsNullOrEmpty(s.folder) || !s.folder.StartsWith("Assets")) s.folder = DefaultFolder;
        return s;
    }

    public void Save() => EditorPrefs.SetString(PREF, JsonUtility.ToJson(this));

    /// Outline width as a fraction of the icon size.
    public float OutlineFraction =>
        outline == IconOutline.Thin   ? 0.012f :
        outline == IconOutline.Medium ? 0.022f :
        outline == IconOutline.Thick  ? 0.036f :
        outline == IconOutline.Custom ? Mathf.Clamp(customOutlinePercent, 0f, 20f) / 100f : 0f;
}
