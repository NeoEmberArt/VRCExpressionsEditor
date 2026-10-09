// ═══════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — Structure Export / Import
//
// Exports where things ended up, so the same layout can be re-applied to another
// avatar that has similar content. Matching is by NAME, not by asset reference,
// and anything the target avatar doesn't have is reported and skipped rather
// than invented.
// ═══════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEditor;

public partial class EASYExpressionManager
{
    const string STRUCTURE_FORMAT = "eem-structure-1";

    // ═══════════════════════════════════════════════════════════════════════════
    // EXPORT
    // ═══════════════════════════════════════════════════════════════════════════

    /// Items whose position the user actually controls. The insides of a Full
    /// Controller's own menus are excluded — nobody wants "GoGo Loco's third
    /// dance button" replicated onto another avatar, they want GoGo Loco itself
    /// placed somewhere.
    bool IsPlaceable(ExpressionNode n)
    {
        if (n == null || n == root) return false;
        if (n.parent != null && n.parent.isFromFullController) return false;
        string display = DisplayOf(n);
        if (string.IsNullOrWhiteSpace(display) || display.StartsWith("(unnamed ")) return false;
        return true;
    }

    void ExportStructure()
    {
        if (root == null) { EditorUtility.DisplayDialog("Nothing to export", "Scan an avatar first.", "OK"); return; }

        var items = new List<object>();
        void Walk(ExpressionNode n)
        {
            foreach (var c in n.children)
            {
                if (IsPlaceable(c))
                {
                    items.Add(new Dictionary<string, object>
                    {
                        ["path"]     = DisplayPathOf(c),
                        ["leaf"]     = DisplayOf(c),
                        ["rawLeaf"]  = c.rawName ?? "",
                        ["type"]     = c.type.ToString(),
                        ["source"]   = SourceCategoryOf(c),
                        ["isVirtual"] = c.isVirtualFolder,
                    });
                }
                Walk(c);
            }
        }
        Walk(root);

        var payload = new Dictionary<string, object>
        {
            ["format"]         = STRUCTURE_FORMAT,
            ["pluginVersion"]  = ResolveVersion(),
            ["createdAt"]      = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["sourceAvatar"]   = avatar ? avatar.name : "",
            ["virtualFolders"] = virtualFolderPaths
                                    .Select(p => (object)string.Join("/", EEMPath.SplitPath(p).Select(EEMPath.Strip)))
                                    .ToList(),
            ["items"]          = items,
        };

        string defaultName = $"EEM_structure_{(avatar ? avatar.name : "avatar")}.json";
        string savePath = EditorUtility.SaveFilePanel("Export Menu Structure", "", defaultName, "json");
        if (string.IsNullOrEmpty(savePath)) return;
        try
        {
            File.WriteAllText(savePath, MiniJson.Pretty(payload));
            Debug.Log($"[EASY] Exported {items.Count} placement(s) to {savePath}");
            ShowNotification(new GUIContent("Structure exported"));
        }
        catch (System.Exception ex)
        {
            EditorUtility.DisplayDialog("Export failed", ex.Message, "OK");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // IMPORT
    // ═══════════════════════════════════════════════════════════════════════════

    internal class StructurePlanEntry
    {
        public string kind;          // "move" | "createVirtual" | "skip" | "alreadyThere"
        public string description;
        public string stableKey;     // item to move
        public string destFolder;    // display path of the destination folder
        public string virtualPath;   // for createVirtual
        public bool   enabled = true;
    }

    void ImportStructure()
    {
        if (!avatar || root == null)
        {
            EditorUtility.DisplayDialog("No avatar", "Select and scan an avatar first.", "OK");
            return;
        }
        string openPath = EditorUtility.OpenFilePanel("Import Menu Structure", "", "json");
        if (string.IsNullOrEmpty(openPath)) return;

        Dictionary<string, object> data;
        try { data = MiniJson.AsDict(MiniJson.Parse(File.ReadAllText(openPath))); }
        catch (System.Exception ex) { EditorUtility.DisplayDialog("Import failed", ex.Message, "OK"); return; }

        if (data == null || MiniJson.Str(data, "format") != STRUCTURE_FORMAT)
        {
            EditorUtility.DisplayDialog("Import failed", "That file is not an Easy Expression Manager structure export.", "OK");
            return;
        }

        var plan = BuildImportPlan(data);
        StructureImportWindow.Open(plan, MiniJson.Str(data, "sourceAvatar", "?"), approved => ApplyImportPlan(approved));
    }

    List<StructurePlanEntry> BuildImportPlan(Dictionary<string, object> data)
    {
        var plan = new List<StructurePlanEntry>();

        // 1 — virtual menus the layout depends on
        foreach (var o in MiniJson.List(data, "virtualFolders"))
        {
            string displayPath = o as string;
            if (string.IsNullOrWhiteSpace(displayPath)) continue;
            if (NodeByDisplayPath(root, displayPath) != null) continue;
            plan.Add(new StructurePlanEntry
            {
                kind = "createVirtual",
                virtualPath = displayPath,
                description = $"Create virtual menu  '{displayPath}'",
            });
        }

        // 2 — placements
        foreach (var o in MiniJson.List(data, "items"))
        {
            var item = o as Dictionary<string, object>;
            if (item == null) continue;
            string wantPath = MiniJson.Str(item, "path");
            string leaf     = MiniJson.Str(item, "leaf");
            string rawLeaf  = MiniJson.Str(item, "rawLeaf");
            if (string.IsNullOrEmpty(wantPath) || string.IsNullOrEmpty(leaf)) continue;
            bool isVirtual = MiniJson.Bool(item, "isVirtual");
            if (isVirtual) continue;   // handled by the virtual-menu pass

            var match = FindImportMatch(wantPath, leaf, rawLeaf, out string how);
            if (match == null)
            {
                plan.Add(new StructurePlanEntry
                {
                    kind = "skip",
                    enabled = false,
                    description = $"Skip  '{leaf}'  — this avatar has no matching item",
                });
                continue;
            }

            string currentPath = DisplayPathOf(match);
            if (string.Equals(currentPath, wantPath, System.StringComparison.OrdinalIgnoreCase))
            {
                plan.Add(new StructurePlanEntry
                {
                    kind = "alreadyThere",
                    enabled = false,
                    description = $"Already in place  '{wantPath}'",
                });
                continue;
            }

            string destFolder = ParentDisplayPath(wantPath);
            plan.Add(new StructurePlanEntry
            {
                kind = "move",
                stableKey = match.stableKey,
                destFolder = destFolder,
                description = $"Move  '{currentPath}'  →  '{wantPath}'" + (how == "name" ? "   (matched by name)" : ""),
            });
        }

        return plan;
    }

    static string ParentDisplayPath(string displayPath)
    {
        if (string.IsNullOrEmpty(displayPath)) return "";
        int i = displayPath.LastIndexOf('/');
        return i < 0 ? "" : displayPath.Substring(0, i);
    }

    /// Exact path first, then a unique name match anywhere in the tree.
    ExpressionNode FindImportMatch(string wantPath, string leaf, string rawLeaf, out string how)
    {
        how = "path";
        var exact = NodeByDisplayPath(root, wantPath);
        if (exact != null) return exact;

        how = "name";
        var byRaw = allNodesFlatList
            .Where(n => IsPlaceable(n) && string.Equals(n.rawName, rawLeaf, System.StringComparison.Ordinal))
            .ToList();
        if (byRaw.Count == 1) return byRaw[0];

        var byDisplay = allNodesFlatList
            .Where(n => IsPlaceable(n) && string.Equals(DisplayOf(n), leaf, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byDisplay.Count == 1) return byDisplay[0];

        how = null;
        return null;
    }

    void ApplyImportPlan(List<StructurePlanEntry> plan)
    {
        if (plan == null) return;
        var todo = plan.Where(p => p.enabled && (p.kind == "move" || p.kind == "createVirtual")).ToList();
        if (todo.Count == 0) return;

        SnapshotExpansionStates(root);
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Import menu structure");
        int grp = Undo.GetCurrentGroup();

        var problems = new List<string>();
        var problemEntries = new List<object>();
        int applied = 0;

        LogOp("StructureImport", "", "Multiple", "", $"{todo.Count} action(s)", () =>
        {
            // virtual menus first — moves may depend on them existing
            foreach (var p in todo.Where(p => p.kind == "createVirtual"))
            {
                AddVirtualFolder(EEMPath.JoinPath(p.virtualPath.Split('/').Where(s => !string.IsNullOrEmpty(s))));
                applied++;
            }
            QueueRescan(); FlushRescan();

            foreach (var p in todo.Where(p => p.kind == "move"))
            {
                var node = allNodesFlatList.FirstOrDefault(n => n.stableKey == p.stableKey);
                if (node == null) { problems.Add("item vanished before it could be moved: " + p.description); continue; }
                Undo.IncrementCurrentGroup();
                int rowGroup = Undo.GetCurrentGroup();
                try
                {
                    string destInternal = EnsureDestinationFolder(p.destFolder);
                    var destNode = string.IsNullOrEmpty(destInternal) ? root : NodeByPath(root, destInternal);
                    PerformMove(node, destInternal, destNode);
                    FlushRescan();
                    applied++;
                }
                catch (System.Exception ex)
                {
                    Undo.FlushUndoRecordObjects();
                    Undo.RevertAllDownToGroup(rowGroup);
                    LoadVirtualFolders();
                    QueueRescan(); FlushRescan();
                    problems.Add(p.description + " — " + ex.Message);
                }
            }
            problemEntries.AddRange(problems.Cast<object>());
        }, new Dictionary<string, object>
        {
            ["plannedActions"] = todo.Count,
            ["problems"] = problemEntries,
        });

        Undo.CollapseUndoOperations(grp);
        FlushRescan();

        string summary = $"Applied {applied} of {todo.Count} action(s).";
        if (problems.Count > 0)
        {
            Debug.LogWarning("[EASY] Structure import issues:\n- " + string.Join("\n- ", problems));
            summary += $"\n\n{problems.Count} could not be applied — see the Console.";
        }
        EditorUtility.DisplayDialog("Structure Import", summary, "OK");
    }

    /// Resolves a display-path folder, creating virtual menus for any missing
    /// segment so the destination genuinely exists before we move into it.
    string EnsureDestinationFolder(string destDisplayPath)
    {
        if (string.IsNullOrWhiteSpace(destDisplayPath)) return "";
        var existing = NodeByDisplayPath(root, destDisplayPath);
        if (existing != null) return PathOf(existing);

        var segments = destDisplayPath.Split('/').Where(s => !string.IsNullOrEmpty(s)).ToArray();
        var builtDisplay = new List<string>();
        string internalPath = "";
        foreach (var seg in segments)
        {
            builtDisplay.Add(seg);
            var node = NodeByDisplayPath(root, string.Join("/", builtDisplay));
            if (node != null) { internalPath = PathOf(node); continue; }
            internalPath = EEMPath.Combine(internalPath, seg);
            AddVirtualFolder(internalPath);
            QueueRescan(); FlushRescan();
        }
        return internalPath;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// IMPORT PREVIEW WINDOW — nothing is written until Apply is pressed
// ═══════════════════════════════════════════════════════════════════════════════

public class StructureImportWindow : EditorWindow
{
    private List<EASYExpressionManager.StructurePlanEntry> plan;
    private string sourceAvatar;
    private System.Action<List<EASYExpressionManager.StructurePlanEntry>> onApply;
    private Vector2 scroll;

    internal static void Open(List<EASYExpressionManager.StructurePlanEntry> plan, string sourceAvatar,
                              System.Action<List<EASYExpressionManager.StructurePlanEntry>> onApply)
    {
        var w = GetWindow<StructureImportWindow>(true, "Import Menu Structure", true);
        w.plan = plan;
        w.sourceAvatar = sourceAvatar;
        w.onApply = onApply;
        w.minSize = new Vector2(560, 420);
        w.ShowUtility();
    }

    void OnGUI()
    {
        if (plan == null) { Close(); return; }

        GUILayout.Space(8);
        EditorGUILayout.LabelField($"Structure exported from \"{sourceAvatar}\"", EditorStyles.boldLabel);
        int moves = plan.Count(p => p.kind == "move");
        int creates = plan.Count(p => p.kind == "createVirtual");
        int skips = plan.Count(p => p.kind == "skip");
        int already = plan.Count(p => p.kind == "alreadyThere");
        EditorGUILayout.LabelField(
            $"{moves} move(s) · {creates} virtual menu(s) to create · {already} already in place · {skips} not found here",
            EditorStyles.miniLabel);
        GUILayout.Space(6);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Enable All", GUILayout.Width(90)))
            foreach (var p in plan) if (p.kind == "move" || p.kind == "createVirtual") p.enabled = true;
        if (GUILayout.Button("Disable All", GUILayout.Width(90)))
            foreach (var p in plan) p.enabled = false;
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(4);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
        foreach (var p in plan)
        {
            bool actionable = p.kind == "move" || p.kind == "createVirtual";
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!actionable))
                p.enabled = EditorGUILayout.Toggle(p.enabled, GUILayout.Width(18));

            var style = new GUIStyle(EditorStyles.label) { wordWrap = true };
            style.normal.textColor =
                p.kind == "move"          ? new Color(0.6f, 1f, 0.7f) :
                p.kind == "createVirtual" ? new Color(0.7f, 0.8f, 1f) :
                p.kind == "alreadyThere"  ? new Color(0.6f, 0.6f, 0.6f) :
                                            new Color(1f, 0.75f, 0.45f);
            EditorGUILayout.LabelField(p.description, style);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        GUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor = new Color(0.5f, 0.9f, 0.6f);
        if (GUILayout.Button("Apply Checked", GUILayout.Height(28)))
        {
            var copy = plan.ToList();
            var cb = onApply;
            Close();
            cb?.Invoke(copy);
        }
        GUI.backgroundColor = Color.white;
        if (GUILayout.Button("Cancel", GUILayout.Height(28), GUILayout.Width(100))) Close();
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(4);
    }
}
