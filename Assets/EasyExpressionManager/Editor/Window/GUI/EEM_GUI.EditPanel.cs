// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — selected-item edit panel
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

    void DrawEditPanel()
    {
        bool hasSel = selected != null && selected != root;
        if (!hasSel) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        // v2.1.0 used selected.name.ToUpper() on the RAW name, so a rich-text
        // name rendered as "<B>GOGO LOCO".
        EditorGUILayout.LabelField($"EDITING: {DisplayOf(selected).ToUpper()}", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();

        // Icon field is drawn for EVERY node, virtual menus included. Virtual
        // menus have no sourceControl/sourceObject, so ApplyIconChange falls
        // through to WriteOverrideMenuIcon(path) — the same OverrideMenuIcon
        // route FC-prefix folders use. Previously the virtual-menu branch
        // returned before this field was drawn, so there was no way to set it.
        EditorGUILayout.BeginVertical(GUILayout.Width(60));
        var newIco = (Texture2D)EditorGUILayout.ObjectField(GUIContent.none, selected.icon, typeof(Texture2D), false,
                         GUILayout.Width(50), GUILayout.Height(50));
        if (newIco != selected.icon) { pendingIconNode = selected; pendingIcon = newIco; }
        DrawAutoIconButton(50);
        EditorGUILayout.EndVertical();

        if (selected.isVirtualFolder)
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.HelpBox("Virtual menu — set its icon here; rename / delete via right-click.", MessageType.None);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            DrawIconGenerator(selected, wide: true);
            EditorGUILayout.EndVertical();
            GUILayout.Space(8);
            return;
        }

        EditorGUILayout.BeginVertical();
        EditorGUILayout.LabelField("Display Name", EditorStyles.miniLabel);
        GUI.SetNextControlName("EditPanelName");
        string prev = selected.rawName ?? "";
        string next2 = EditorGUILayout.DelayedTextField(prev);
        if (!string.IsNullOrEmpty(next2) && !string.Equals(next2, prev, System.StringComparison.Ordinal))
        {
            var cap = selected;
            Defer(() => DoRename(cap, next2));
        }

        GUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();

        GUI.backgroundColor = new Color(1f, 0.85f, 0.3f);
        if (GUILayout.Button(new GUIContent("Move/Rename via ExpressionsManager", "Move or rename to any path."),
                GUILayout.Height(22)))
            OpenMoveDialog(selected);
        GUI.backgroundColor = Color.white;

        bool canDup = CanDuplicate(selected);
        GUI.backgroundColor = canDup ? new Color(0.4f, 0.85f, 0.55f) : Color.white;
        using (new EditorGUI.DisabledScope(!canDup))
            if (GUILayout.Button(new GUIContent("Duplicate", "Duplicate this item."), GUILayout.Height(22), GUILayout.Width(90)))
                OpenDuplicateDialog(selected);
        GUI.backgroundColor = Color.white;

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();
        DrawIconGenerator(selected, wide: true);
        EditorGUILayout.EndVertical();
        GUILayout.Space(8);
    }

}
