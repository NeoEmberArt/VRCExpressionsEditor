// Easy Expression Manager: capture menu state and export debug JSON.

using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEditor;

public partial class EASYExpressionManager
{
    // ═══════════════════════════════════════════════════════════════════════════
    // REAL-STATE CAPTURE
    //
    // Runs the same validation sweep as before — orphan/stale/duplicate/
    // multi-component checks, dangling record detection — and still populates
    // `issues` exactly as it always has. The difference is what it hands back:
    // just the node count, not a full serialised copy of the tree and every
    // move record. That full picture now lives in exactly two places per
    // export — the initial and final snapshots — instead of once per operation.
    // ═══════════════════════════════════════════════════════════════════════════

    int CaptureRealState(List<object> issues)
    {
        Scan();

        var seenPaths = new HashSet<string>(System.StringComparer.Ordinal);
        var dupPaths  = new HashSet<string>();
        int nodeCount = 0;

        void Walk(ExpressionNode node)
        {
            if (node == null) return;
            if (node != root)
            {
                nodeCount++;
                string p = PathOf(node);
                if (!string.IsNullOrEmpty(p) && !seenPaths.Add(p)) dupPaths.Add(p);

                if (node.sourceControl != null && node.sourceMenu == null && !node.isFromFullController)
                    issues.Add($"ORPHAN CONTROL: '{DisplayPathOf(node)}' has a sourceControl but no owning sourceMenu");
                if (node.isFromFullController && node.sourceFullControllerObject == null)
                    issues.Add($"ORPHAN FC NODE: '{DisplayPathOf(node)}' is flagged Full-Controller but has no backing component");
                if (node.sourceObject != null && node.sourceObject.targetObject == null)
                    issues.Add($"STALE FURY REF: '{DisplayPathOf(node)}' has a sourceObject whose targetObject is null");
                if (node.duplicateSourceCount > 1)
                    issues.Add($"DUPLICATE SOURCE: '{DisplayPathOf(node)}' is produced by {node.duplicateSourceCount} components at once");
            }
            foreach (var c in node.children) Walk(c);
        }
        Walk(root);

        // Same-name items are allowed (VRChat shows them side by side), so a path
        // that appears more than once is not an issue by itself. The ones that
        // matter — an action VRCFury would apply to all of them — are refused
        // or confirmed at the moment they're attempted.

        foreach (var d in danglingRecordDescriptions)
            issues.Add("UNRESOLVED RECORD: " + d);

        bool hasManager = HasManager();
        if (avatar && hasManager)
        {
            var seenFrom = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var r in ReadMoveRecords())
            {
                if (string.IsNullOrEmpty(r.from))
                    issues.Add("MOVE RECORD: empty 'from' path on a MoveMenuItem component");
                else if (!seenFrom.Add(EEMPath.Norm(r.from)) && !resolvedMoveRecords.Contains(r.comp))
                    issues.Add($"MOVE RECORD: duplicate 'from' path '{EEMPath.ToVrcf(r.from)}' — chains may not be flattened");
                if (!string.IsNullOrEmpty(r.from) && EEMPath.PathEq(r.from, r.to))
                    issues.Add($"MOVE RECORD: no-op move left in place ('{EEMPath.ToVrcf(r.from)}')");
            }
        }

        if (overCapacityMenus.Count > 0)
            issues.Add($"CAPACITY: {overCapacityMenus.Count} menu(s) over the {MENU_CAPACITY}-item limit — " +
                       string.Join(", ", overCapacityMenus.Take(3)) + (overCapacityMenus.Count > 3 ? ", …" : ""));

        return nodeCount;
    }

    Dictionary<string, object> NodeToDict(ExpressionNode n)
    {
        var d = new Dictionary<string, object>
        {
            ["name"]                  = DisplayOf(n),
            ["rawName"]               = n.rawName ?? "",
            ["path"]                  = n == root ? "" : EEMPath.ToVrcf(PathOf(n)),
            ["type"]                  = n.type.ToString(),
            ["source"]                = n == root ? "Folder" : SourceCategoryOf(n),
            ["isVirtualFolder"]       = n.isVirtualFolder,
            ["isFromFullController"]  = n.isFromFullController,
            ["isFCPrefixFolder"]      = n.isFCPrefixFolder,
            ["fcPrefixGeneratorCount"] = n.fcPrefixGenerators.Count,
            ["isMoveGeneratedFolder"] = n.isMoveGeneratedFolder,
            ["hasIcon"]               = n.icon != null,
            ["childCount"]            = n.children.Count,
        };
        if (IsOverCapacity(n)) d["overCapacity"] = true;
        if (n.children.Count > 0)
        {
            var kids = new List<object>();
            foreach (var c in n.children) kids.Add(NodeToDict(c));
            d["children"] = kids;
        }
        return d;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // OUTPUT
    // ═══════════════════════════════════════════════════════════════════════════

    void GenerateDebugJson(bool saveToFile)
    {
        // Final tree is captured fresh right now, not reused from any earlier
        // scan — this is "what the avatar looks like at the moment you asked
        // for the log", the counterpart to initialTreeSnapshot ("what it
        // looked like before the first operation this session").
        if (includeTreesInDebugJson) Scan();
        var finalTree = includeTreesInDebugJson && root != null ? NodeToDict(root) : null;

        var payload = new Dictionary<string, object>
        {
            ["generatedAt"]           = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            ["sessionId"]             = debugSessionId,
            ["pluginVersion"]         = ResolveVersion(),
            ["avatar"]                = avatar ? avatar.name : null,
            ["totalOperationsLogged"] = debugOpCounter,
            ["entriesInThisLog"]      = debugLog.Count,
        };
        if (includeTreesInDebugJson)
        {
            payload["initialTree"] = initialTreeSnapshot;
            payload["finalTree"]   = finalTree;
        }
        payload["operations"] = debugLog.Cast<object>().ToList();

        string json = MiniJson.Pretty(payload);

        if (saveToFile)
        {
            string defaultName = $"EASY_debug_{System.DateTime.Now:yyyyMMdd_HHmmss}.json";
            string savePath = EditorUtility.SaveFilePanel("Save Debug Log", "", defaultName, "json");
            if (string.IsNullOrEmpty(savePath)) return;
            try
            {
                File.WriteAllText(savePath, json);
                EditorUtility.RevealInFinder(savePath);
                Debug.Log($"[EASY Debug] Wrote debug log ({debugLog.Count} operations) to {savePath}");
                ShowNotification(new GUIContent("Debug JSON saved"));
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[EASY Debug] Failed to write debug log: {ex}");
                EditorUtility.DisplayDialog("Save Failed", ex.Message, "OK");
            }
        }
        else
        {
            EditorGUIUtility.systemCopyBuffer = json;
            Debug.Log($"[EASY Debug] Copied debug log ({debugLog.Count} operations, {json.Length} chars) to clipboard.");
            ShowNotification(new GUIContent("Debug JSON copied to clipboard"));
        }
    }
}
