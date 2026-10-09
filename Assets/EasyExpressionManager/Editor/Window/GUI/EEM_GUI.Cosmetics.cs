// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — icons & colours
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

    Texture DefaultIcon(NodeType t)
    {
        switch (t)
        {
            case NodeType.Folder:  return EditorGUIUtility.IconContent("Folder Icon")?.image;
            case NodeType.SubMenu: return EditorGUIUtility.IconContent("d_Project")?.image;
            default:
                return EditorGUIUtility.IconContent("AvatarSelector")?.image
                    ?? EditorGUIUtility.IconContent("d_Avatar Icon")?.image;
        }
    }

    Color RowColor(NodeType t)
    {
        switch (t)
        {
            case NodeType.Folder:  return new Color(0.20f, 0.35f, 0.45f);
            case NodeType.SubMenu: return new Color(0.35f, 0.20f, 0.50f);
            case NodeType.Slider:  return new Color(0.45f, 0.38f, 0.10f);
            default:               return new Color(0.18f, 0.32f, 0.18f);
        }
    }

    Color LabelColor(NodeType t)
    {
        switch (t)
        {
            case NodeType.Folder:  return Color.cyan;
            case NodeType.SubMenu: return new Color(0.8f, 0.5f, 1f);
            case NodeType.Toggle:  return new Color(0.6f, 1f, 0.6f);
            case NodeType.Slider:  return new Color(1f, 0.9f, 0.4f);
            default:               return Color.white;
        }
    }
}
