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
    const string STRUCTURE_FORMAT = "eem-structure-2";
    const string LEGACY_STRUCTURE_FORMAT = "eem-structure-1";

    static Dictionary<string, object> StructureSegment(string raw, int folderOrdinal)
    {
        return new Dictionary<string, object>
        {
            ["raw"] = raw ?? "",
            ["folderOrdinal"] = folderOrdinal,
        };
    }

    List<object> StructurePathSegments(ExpressionNode node)
    {
        var segments = new List<object>();
        for (var current = node; current != null && current != root; current = current.parent)
        {
            segments.Insert(0, StructureSegment(current.rawName, StructureFolderOrdinalOf(current)));
        }
        return segments;
    }

    static List<object> StructurePathSegmentsFromRaw(IEnumerable<string> rawSegments)
    {
        var result = new List<object>();
        foreach (string raw in rawSegments ?? Enumerable.Empty<string>())
            result.Add(StructureSegment(raw, 0));
        return result;
    }

    static bool TryReadStrictString(Dictionary<string, object> owner, string key,
                                    out string value, bool allowEmpty = true)
    {
        value = null;
        if (owner == null || !owner.TryGetValue(key, out var raw) || !(raw is string text))
            return false;
        if (!allowEmpty && string.IsNullOrEmpty(text)) return false;
        value = text;
        return true;
    }

    static bool TryReadStrictNonNegativeInt(Dictionary<string, object> owner,
                                            string key, out int value)
    {
        value = 0;
        if (owner == null || !owner.TryGetValue(key, out var raw) || raw == null)
            return false;

        if (raw is int i)
        {
            if (i < 0) return false;
            value = i;
            return true;
        }
        if (raw is long l)
        {
            if (l < 0 || l > int.MaxValue) return false;
            value = (int)l;
            return true;
        }
        if (raw is double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d) || d < 0d ||
                d > int.MaxValue || System.Math.Truncate(d) != d) return false;
            value = (int)d;
            return true;
        }
        if (raw is float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f) || f < 0f ||
                f > int.MaxValue || System.Math.Truncate(f) != f) return false;
            value = (int)f;
            return true;
        }
        return false;
    }

    static bool TryReadStrictBool(Dictionary<string, object> owner, string key, out bool value)
    {
        value = false;
        if (owner == null || !owner.TryGetValue(key, out var raw) || !(raw is bool parsed))
            return false;
        value = parsed;
        return true;
    }

    static bool TryReadStructureSegments(Dictionary<string, object> owner,
                                         string key,
                                         out List<string> rawSegments,
                                         out List<int> folderOrdinals)
    {
        rawSegments = new List<string>();
        folderOrdinals = new List<int>();
        if (owner == null || !owner.TryGetValue(key, out var rawList) ||
            !(rawList is List<object> list) || list.Count == 0) return false;
        foreach (var item in list)
        {
            var segment = item as Dictionary<string, object>;
            string raw;
            int folderOrdinal;
            if (segment == null ||
                !TryReadStrictString(segment, "raw", out raw) ||
                !TryReadStrictNonNegativeInt(segment, "folderOrdinal", out folderOrdinal))
            {
                rawSegments.Clear();
                folderOrdinals.Clear();
                return false;
            }
            rawSegments.Add(raw);
            folderOrdinals.Add(folderOrdinal);
        }
        return rawSegments.Count > 0;
    }

    static string StructureDisplayPath(IEnumerable<string> rawSegments)
    {
        return string.Join("/", (rawSegments ?? Enumerable.Empty<string>()).Select(EEMPath.Strip));
    }

    static string StructureInternalPath(IEnumerable<string> rawSegments, IList<int> folderOrdinals)
    {
        var encoded = new List<string>();
        var raw = (rawSegments ?? Enumerable.Empty<string>()).ToList();
        for (int i = 0; i < raw.Count; i++)
        {
            string segment = raw[i] ?? "";
            int ordinal = folderOrdinals != null && i < folderOrdinals.Count
                ? System.Math.Max(0, folderOrdinals[i]) : 0;
            if (ordinal > 0 || string.IsNullOrEmpty(segment))
                segment += ".dup." + ordinal;
            encoded.Add(segment);
        }
        return EEMPath.JoinPath(encoded);
    }

    static bool StructurePathIsVrcfSafe(IList<string> rawSegments,
                                        bool finalNodeIsFolder,
                                        out string reason)
    {
        reason = null;
        if (rawSegments == null || rawSegments.Count == 0) return true;
        if (!EEMPath.TryEncodeVrcfRawPath(rawSegments, out _, out reason)) return false;
        string final = rawSegments[rawSegments.Count - 1] ?? "";
        if (finalNodeIsFolder && EEMPath.IsVrcfReservedAncestorSegment(final))
        {
            reason = "The submenu name '" + final + "' uses VRCFury's reserved .dup. selector syntax.";
            return false;
        }
        return true;
    }

    int StructureLeafOrdinal(ExpressionNode node, string source, string type)
    {
        if (node == null || node.parent == null) return 0;
        var peers = node.parent.children.Where(candidate =>
            string.Equals(candidate.rawName ?? "", node.rawName ?? "", System.StringComparison.Ordinal) &&
            string.Equals(candidate.type.ToString(), type, System.StringComparison.Ordinal) &&
            (string.IsNullOrEmpty(source) ||
             string.Equals(SourceCategoryOf(candidate), source, System.StringComparison.Ordinal)))
            .ToList();
        int ordinal = peers.IndexOf(node);
        return ordinal < 0 ? 0 : ordinal;
    }

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
                    string source = SourceCategoryOf(c);
                    string type = c.type.ToString();
                    items.Add(new Dictionary<string, object>
                    {
                        ["path"]     = DisplayPathOf(c),
                        ["leaf"]     = DisplayOf(c),
                        ["rawLeaf"]  = c.rawName ?? "",
                        ["type"]     = type,
                        ["source"]   = source,
                        ["pathSegments"] = StructurePathSegments(c),
                        ["leafOrdinal"] = StructureLeafOrdinal(c, source, type),
                        ["isVirtual"] = c.isVirtualFolder,
                    });
                }
                Walk(c);
            }
        }
        Walk(root);

        var virtualFolders = new List<object>();
        var virtualFolderSegments = new List<object>();
        foreach (string registered in virtualFolderPaths)
        {
            var node = NodeByFolderPath(root, registered);
            var raw = node != null
                ? StructurePathSegments(node)
                : StructurePathSegmentsFromRaw(EEMPath.SplitPath(registered));
            var rawNames = raw.OfType<Dictionary<string, object>>()
                .Select(segment => MiniJson.Str(segment, "raw", "")).ToList();
            string displayPath = node != null
                ? DisplayPathOf(node)
                : StructureDisplayPath(rawNames);
            virtualFolders.Add(displayPath);
            virtualFolderSegments.Add(new Dictionary<string, object>
            {
                ["displayPath"] = displayPath,
                ["pathSegments"] = raw,
            });
        }

        var payload = new Dictionary<string, object>
        {
            ["format"]         = STRUCTURE_FORMAT,
            ["pluginVersion"]  = ResolveVersion(),
            ["createdAt"]      = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["sourceAvatar"]   = avatar ? avatar.name : "",
            // Keep the old display list for humans/legacy readers, but import
            // v2 from raw segments so literal '/' characters survive.
            ["virtualFolders"] = virtualFolders,
            ["virtualFolderSegments"] = virtualFolderSegments,
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
        public List<string> destSegments = new List<string>();
        public List<int> destFolderOrdinals = new List<int>();
        public List<string> virtualSegments = new List<string>();
        public List<int> virtualFolderOrdinals = new List<int>();
        public bool structured;
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

        string format = data == null ? null : MiniJson.Str(data, "format");
        if (data == null || (format != STRUCTURE_FORMAT && format != LEGACY_STRUCTURE_FORMAT))
        {
            EditorUtility.DisplayDialog("Import failed", "That file is not an Easy Expression Manager structure export.", "OK");
            return;
        }

        var plan = BuildImportPlan(data);
        StructureImportWindow.Open(plan, MiniJson.Str(data, "sourceAvatar", "?"), approved => ApplyImportPlan(approved));
    }

    // Retained as a reference for the pre-v2 display-path planner. New imports
    // use the structured planner below; this fallback is intentionally not
    // called directly.
    List<StructurePlanEntry> BuildImportPlanLegacy(Dictionary<string, object> data)
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

    List<StructurePlanEntry> BuildImportPlan(Dictionary<string, object> data)
    {
        var plan = new List<StructurePlanEntry>();
        bool structured = MiniJson.Str(data, "format") == STRUCTURE_FORMAT;

        if (structured && data.ContainsKey("virtualFolderSegments"))
        {
            foreach (var o in MiniJson.List(data, "virtualFolderSegments"))
            {
                var folder = o as Dictionary<string, object>;
                List<string> raw;
                List<int> ordinals;
                if (folder == null || !TryReadStructureSegments(folder, "pathSegments", out raw, out ordinals))
                    continue;
                string displayPath = MiniJson.Str(folder, "displayPath", StructureDisplayPath(raw));
                if (NodeByStructureFolderPath(root, raw, ordinals) != null) continue;
                string unsafeReason;
                if (!StructurePathIsVrcfSafe(raw, true, out unsafeReason))
                {
                    plan.Add(new StructurePlanEntry
                    {
                        kind = "skip",
                        enabled = false,
                        description = "Skip virtual menu '" + displayPath + "' — " + unsafeReason,
                    });
                    continue;
                }
                plan.Add(new StructurePlanEntry
                {
                    kind = "createVirtual",
                    virtualPath = displayPath,
                    virtualSegments = raw,
                    virtualFolderOrdinals = ordinals,
                    structured = true,
                    description = "Create virtual menu '" + displayPath + "'",
                });
            }
        }
        else
        {
            foreach (var o in MiniJson.List(data, "virtualFolders"))
            {
                string displayPath = o as string;
                if (string.IsNullOrWhiteSpace(displayPath)) continue;
                if (NodeByDisplayPath(root, displayPath) != null) continue;
                plan.Add(new StructurePlanEntry
                {
                    kind = "createVirtual",
                    virtualPath = displayPath,
                    description = "Create virtual menu '" + displayPath + "'",
                });
            }
        }

        var usedStableKeys = new HashSet<string>(System.StringComparer.Ordinal);
        var usedNodes = new HashSet<ExpressionNode>();

        foreach (var o in MiniJson.List(data, "items"))
        {
            var item = o as Dictionary<string, object>;
            if (item == null) continue;
            string wantPath;
            string leaf;
            string rawLeaf;
            string type;
            string source;
            int leafOrdinal;
            bool isVirtual;
            if (structured)
            {
                if (!TryReadStrictString(item, "path", out wantPath, false) ||
                    !TryReadStrictString(item, "leaf", out leaf, false) ||
                    !TryReadStrictString(item, "rawLeaf", out rawLeaf) ||
                    !TryReadStrictString(item, "type", out type, false) ||
                    !TryReadStrictString(item, "source", out source, false) ||
                    !TryReadStrictNonNegativeInt(item, "leafOrdinal", out leafOrdinal) ||
                    !TryReadStrictBool(item, "isVirtual", out isVirtual))
                {
                    plan.Add(new StructurePlanEntry
                    {
                        kind = "skip",
                        enabled = false,
                        description = "Skip malformed v2 item metadata",
                    });
                    continue;
                }
            }
            else
            {
                wantPath = MiniJson.Str(item, "path");
                leaf = MiniJson.Str(item, "leaf");
                rawLeaf = MiniJson.Str(item, "rawLeaf");
                type = MiniJson.Str(item, "type");
                source = MiniJson.Str(item, "source");
                leafOrdinal = MiniJson.Int(item, "leafOrdinal", -1);
                isVirtual = MiniJson.Bool(item, "isVirtual");
            }
            if (string.IsNullOrEmpty(wantPath) || string.IsNullOrEmpty(leaf)) continue;

            List<string> rawSegments = null;
            List<int> folderOrdinals = null;
            bool hasStructuredPath = false;
            if (structured)
            {
                hasStructuredPath = TryReadStructureSegments(item, "pathSegments", out rawSegments, out folderOrdinals);
                if (!hasStructuredPath)
                {
                    plan.Add(new StructurePlanEntry
                    {
                        kind = "skip",
                        enabled = false,
                        description = "Skip '" + leaf + "' — malformed structured path data",
                    });
                    continue;
                }
            }
            if (isVirtual) continue;
            if (hasStructuredPath)
            {
                bool finalIsFolder = type == NodeType.Folder.ToString() ||
                    type == NodeType.SubMenu.ToString() || source == "VirtualFolder" ||
                    source == "FCPrefixFolder" || source == "MoveGeneratedFolder" || source == "Folder";
                string unsafeReason;
                if (!StructurePathIsVrcfSafe(rawSegments, finalIsFolder, out unsafeReason))
                {
                    plan.Add(new StructurePlanEntry
                    {
                        kind = "skip",
                        enabled = false,
                        description = "Skip '" + leaf + "' — " + unsafeReason,
                    });
                    continue;
                }
                if (string.IsNullOrEmpty(rawLeaf)) rawLeaf = rawSegments[rawSegments.Count - 1];
            }

            string how;
            string issue;
            var match = FindImportMatchStructured(wantPath, leaf, rawLeaf, type, source, leafOrdinal,
                                                  rawSegments, folderOrdinals, usedStableKeys, usedNodes,
                                                  out how, out issue);
            if (match == null)
            {
                plan.Add(new StructurePlanEntry
                {
                    kind = "skip",
                    enabled = false,
                    description = "Skip '" + leaf + "' — " + (issue ?? "this avatar has no matching item"),
                });
                continue;
            }

            if (!string.IsNullOrEmpty(match.stableKey)) usedStableKeys.Add(match.stableKey);
            usedNodes.Add(match);

            string currentPath = DisplayPathOf(match);
            bool alreadyThere = hasStructuredPath
                ? StructureLocationMatches(match, rawSegments, folderOrdinals, leafOrdinal, type, source)
                : string.Equals(currentPath, wantPath, System.StringComparison.OrdinalIgnoreCase);
            if (alreadyThere)
            {
                plan.Add(new StructurePlanEntry
                {
                    kind = "alreadyThere",
                    enabled = false,
                    description = "Already in place '" + wantPath + "'",
                });
                continue;
            }

            var destSegments = hasStructuredPath
                ? rawSegments.Take(System.Math.Max(0, rawSegments.Count - 1)).ToList()
                : new List<string>();
            var destOrdinals = hasStructuredPath
                ? folderOrdinals.Take(System.Math.Max(0, folderOrdinals.Count - 1)).ToList()
                : new List<int>();
            plan.Add(new StructurePlanEntry
            {
                kind = "move",
                stableKey = match.stableKey,
                destFolder = ParentDisplayPath(wantPath),
                destSegments = destSegments,
                destFolderOrdinals = destOrdinals,
                structured = hasStructuredPath,
                description = "Move '" + currentPath + "' → '" + wantPath + "'" +
                              (how == "name" ? " (matched by name)" : ""),
            });
        }
        return plan;
    }

    bool StructureCompatible(ExpressionNode node, string type, string source)
    {
        return IsPlaceable(node) &&
            (string.IsNullOrEmpty(type) || node.type.ToString() == type) &&
            (string.IsNullOrEmpty(source) || SourceCategoryOf(node) == source);
    }

    bool StructureLocationMatches(ExpressionNode node, IList<string> rawSegments,
                                  IList<int> folderOrdinals, int leafOrdinal,
                                  string type, string source)
    {
        if (!StructureCompatible(node, type, source)) return false;
        var current = StructurePathSegments(node);
        if (current.Count != rawSegments.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            var segment = current[i] as Dictionary<string, object>;
            if (segment == null || MiniJson.Str(segment, "raw", "") != (rawSegments[i] ?? "")) return false;
            if (i < current.Count - 1)
            {
                int actual = MiniJson.Int(segment, "folderOrdinal", 0);
                int expected = folderOrdinals != null && i < folderOrdinals.Count ? folderOrdinals[i] : 0;
                if (actual != expected) return false;
            }
        }
        return leafOrdinal < 0 || StructureLeafOrdinal(node, source, type) == leafOrdinal;
    }

    ExpressionNode FindImportMatchStructured(string wantPath, string leaf, string rawLeaf,
                                              string type, string source, int leafOrdinal,
                                              IList<string> rawSegments, IList<int> folderOrdinals,
                                              HashSet<string> usedStableKeys,
                                              HashSet<ExpressionNode> usedNodes,
                                              out string how, out string issue)
    {
        how = "path";
        issue = null;
        bool IsUsed(ExpressionNode node)
        {
            return node != null && usedNodes.Contains(node) ||
                node != null && !string.IsNullOrEmpty(node.stableKey) && usedStableKeys.Contains(node.stableKey);
        }

        if (rawSegments != null && rawSegments.Count > 0)
        {
            // A raw path present on the target is authoritative. Do not fall
            // back to a same-name item elsewhere when its typed ordinal is
            // missing or already consumed: that would silently change the
            // exported identity.
            var pathMatches = FindStructurePathMatches(root, rawSegments, folderOrdinals);
            if (pathMatches.Count == 0)
            {
                how = "name";
                var fallback = allNodesFlatList
                    .Where(n => StructureCompatible(n, type, source) &&
                                !IsUsed(n) &&
                                string.Equals(n.rawName, rawLeaf, System.StringComparison.Ordinal))
                    .ToList();
                if (fallback.Count == 1) return fallback[0];
                issue = fallback.Count > 1
                    ? "unique raw-name fallback is ambiguous"
                    : "no matching raw path or unique typed raw-name fallback";
                return null;
            }

            var matches = pathMatches
                .Where(n => StructureCompatible(n, type, source)).ToList();
            if (matches.Count == 0)
            {
                issue = "raw path exists but type/source does not match";
                return null;
            }
            if (leafOrdinal < 0 || leafOrdinal >= matches.Count)
            {
                issue = "duplicate sibling ordinal is not present on this avatar";
                return null;
            }
            if (IsUsed(matches[leafOrdinal]))
            {
                issue = "the typed raw-path target is already assigned to another export row";
                return null;
            }
            how = "structured";
            return matches[leafOrdinal];
        }

        var exact = NodeByDisplayPath(root, wantPath);
        if (exact != null && StructureCompatible(exact, type, source))
        {
            if (IsUsed(exact))
            {
                issue = "the display-path target is already assigned to another export row";
                return null;
            }
            return exact;
        }

        how = "name";
        var byRaw = allNodesFlatList
            .Where(n => StructureCompatible(n, type, source) &&
                        !IsUsed(n) &&
                        string.Equals(n.rawName, rawLeaf, System.StringComparison.Ordinal))
            .ToList();
        if (byRaw.Count == 1) return byRaw[0];
        if (byRaw.Count > 1)
        {
            issue = "raw-name match is ambiguous";
            return null;
        }

        // v2 identity is raw-name based. Display-name fallback is retained
        // only for legacy v1 exports, whose schema did not promise raw identity.
        if (rawSegments != null)
        {
            issue = "no unique typed raw-name fallback";
            return null;
        }

        var byDisplay = allNodesFlatList
            .Where(n => StructureCompatible(n, type, source) &&
                        !IsUsed(n) &&
                        string.Equals(DisplayOf(n), leaf, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byDisplay.Count == 1) return byDisplay[0];
        if (byDisplay.Count > 1) issue = "display-name match is ambiguous";
        how = null;
        return null;
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
                string virtualInternal = p.structured
                    ? StructureInternalPath(p.virtualSegments, p.virtualFolderOrdinals)
                    : EEMPath.JoinPath(p.virtualPath.Split('/').Where(s => !string.IsNullOrEmpty(s)));
                AddVirtualFolder(virtualInternal);
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
                    ExpressionNode destNode;
                    string destInternal = p.structured
                        ? EnsureStructuredDestinationFolder(p.destSegments, p.destFolderOrdinals, out destNode)
                        : EnsureDestinationFolder(p.destFolder, out destNode);
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
    string EnsureDestinationFolder(string destDisplayPath, out ExpressionNode destination)
    {
        destination = root;
        if (string.IsNullOrWhiteSpace(destDisplayPath)) return "";
        var existing = NodeByDisplayPath(root, destDisplayPath);
        if (existing != null)
        {
            destination = existing;
            return PathOf(existing);
        }

        var segments = destDisplayPath.Split('/').Where(s => !string.IsNullOrEmpty(s)).ToArray();
        var builtDisplay = new List<string>();
        string internalPath = "";
        foreach (var seg in segments)
        {
            builtDisplay.Add(seg);
            var node = NodeByDisplayPath(root, string.Join("/", builtDisplay));
            if (node != null) { internalPath = PathOf(node); destination = node; continue; }
            internalPath = EEMPath.Combine(internalPath, seg);
            AddVirtualFolder(internalPath);
            QueueRescan(); FlushRescan();
            destination = NodeByPath(root, internalPath) ?? root;
        }
        return internalPath;
    }

    string EnsureStructuredDestinationFolder(IList<string> rawSegments,
                                             IList<int> folderOrdinals,
                                             out ExpressionNode destination)
    {
        destination = root;
        if (rawSegments == null || rawSegments.Count == 0) return "";

        string reason;
        if (!StructurePathIsVrcfSafe(rawSegments, true, out reason))
            throw new System.InvalidOperationException("Destination rejected: " + reason);

        destination = NodeByStructureFolderPath(root, rawSegments, folderOrdinals);
        if (destination != null) return PathOf(destination, true);

        string internalPath = "";
        for (int i = 0; i < rawSegments.Count; i++)
        {
            var prefixRaw = rawSegments.Take(i + 1).ToList();
            var prefixOrdinals = (folderOrdinals ?? new List<int>()).Take(i + 1).ToList();
            var existing = NodeByStructureFolderPath(root, prefixRaw, prefixOrdinals);
            if (existing != null)
            {
                destination = existing;
                internalPath = PathOf(existing, true);
                continue;
            }

            int ordinal = prefixOrdinals.Count > i ? prefixOrdinals[i] : 0;
            if (ordinal != 0)
                throw new System.InvalidOperationException(
                    "Destination duplicate folder ordinal " + ordinal + " does not exist; refusing to select another folder.");

            internalPath = StructureInternalPath(prefixRaw, prefixOrdinals);
            AddVirtualFolder(internalPath);
            QueueRescan(); FlushRescan();
            destination = NodeByStructureFolderPath(root, prefixRaw, prefixOrdinals);
            if (destination == null)
                throw new System.InvalidOperationException("Created destination folder could not be resolved safely.");
            internalPath = PathOf(destination, true);
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
