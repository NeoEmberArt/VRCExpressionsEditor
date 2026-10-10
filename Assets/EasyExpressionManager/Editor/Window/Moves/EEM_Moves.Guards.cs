// Easy Expression Manager: validate move paths, destinations and folder cycles.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    // ═══════════════════════════════════════════════════════════════════════════
    // GUARDS
    // ═══════════════════════════════════════════════════════════════════════════

    /// Literal slashes are escaped for VRCFury. Refuse the remaining raw-name
    /// cases its grammar cannot represent, rather than writing another target.
    void GuardAddressable(string internalPath, string opName)
    {
        if (string.IsNullOrEmpty(internalPath)) return;

        var encodedParts = EEMPath.SplitPath(internalPath);
        if (encodedParts.Length == 0) return;

        // Internal paths use .dup.N only for an ancestor folder selector. Do
        // not feed that generated syntax to the raw-name encoder: resolve it
        // to the actual folder name first. The final segment is always a
        // literal VRCFury control name, so a final name such as "A.dup.1" is
        // intentionally allowed.
        var rawParts = new List<string>();
        var parent = root;
        for (int i = 0; i < encodedParts.Length; i++)
        {
            string part = encodedParts[i] ?? "";
            bool ancestor = i < encodedParts.Length - 1;
            string rawPart = part;

            if (ancestor && parent != null)
            {
                int selector = part.IndexOf(".dup.", System.StringComparison.Ordinal);
                if (selector >= 0)
                {
                    string folderName = part.Substring(0, selector);
                    int ordinal;
                    if (!int.TryParse(part.Substring(selector + 5), out ordinal) || ordinal < 0)
                        RejectAddressable(internalPath, opName, "invalid duplicate-folder selector '" + part + "'");

                    var folders = MatchVrcControls(parent.children, folderName, true);
                    if (ordinal >= folders.Count)
                        RejectAddressable(internalPath, opName,
                            "duplicate-folder selector '" + part + "' does not resolve in the current menu");

                    var selectedFolder = folders[ordinal];
                    rawPart = selectedFolder.rawName ?? folderName;
                    parent = selectedFolder;
                }
                else
                {
                    var folders = MatchVrcControls(parent.children, part, true);
                    if (folders.Count == 1)
                    {
                        rawPart = folders[0].rawName ?? part;
                        parent = folders[0];
                    }
                    else
                    {
                        // A missing/new or already-ambiguous parent cannot
                        // provide a reliable raw identity. Preserve the
                        // spelling and let the encoder reject only the
                        // representations VRCFury truly cannot express.
                        parent = null;
                    }
                }
            }

            rawParts.Add(rawPart);
        }

        string ignoredVrcfPath;
        string reason;
        if (!EEMPath.TryEncodeVrcfRawPath(rawParts, out ignoredVrcfPath, out reason))
            RejectAddressable(internalPath, opName, reason);
    }

    void RejectAddressable(string internalPath, string opName, string reason)
    {
        string message = $"{opName} blocked: '{EEMPath.ToVrcf(internalPath)}' cannot be represented safely by VRCFury ({reason}).";
        NoteConsequence("rejected unrepresentable VRCFury path: " + reason);
        throw new System.InvalidOperationException(message);
    }

    /// Refuses to run any operation whose targeting or cleanup works off a path
    /// STRING rather than a live object reference — MoveMenuItem records,
    /// OverrideMenuIcon records, and the path-prefix sweep that renames and
    /// deletes use to find everything that referenced the old path. A path
    /// string can't distinguish two same-named siblings, so writing or sweeping
    /// against an ambiguous path silently targets, or later re-resolves to,
    /// whichever one a plain lookup happens to hit first — not necessarily the
    /// one the user meant, and not necessarily the same one twice in a row.
    /// Refuses to run any operation whose targeting or cleanup works off a path
    /// STRING rather than a live object reference — MoveMenuItem records,
    /// OverrideMenuIcon records, and the path-prefix sweep that renames and
    /// deletes use to find everything that referenced the old path. A path
    /// string can't distinguish two same-named siblings, so writing or sweeping
    /// against an ambiguous path silently targets, or later re-resolves to,
    /// whichever one a plain lookup happens to hit first — not necessarily the
    /// one the user meant, and not necessarily the same one twice in a row.
    ///
    /// This checks only the FULL combined path, not every ancestor segment:
    /// 'ShapeModify/TailSize' is safe to target even though 'ShapeModify' alone
    /// has several duplicates, as long as the full path fans out to exactly one
    /// real node (NodeByPath/FindAllNodesAtPath already resolve it correctly
    /// either way). Use GuardDestinationChainUnambiguous instead for a path
    /// something is about to be ATTACHED under — that case needs every
    /// ancestor folder to be unambiguous too, since attachment has to commit to
    /// one specific parent object.
    void GuardUnambiguousPath(string internalPath, string opName)
    {
        if (string.IsNullOrEmpty(internalPath)) return;
        var matches = FindAllNodesAtPath(root, internalPath);
        if (matches.Count <= 1) return;
        // A block move may intentionally select the complete same-name group.
        // VRCFury moves that group together; an unselected neighbour still blocks.
        if (bulkMoveSourceKeys.Count > 0 && matches.All(n => bulkMoveSourceKeys.Contains(n.stableKey))) return;
        var parentNode = matches[0].parent;
        string where = parentNode == null || parentNode == root ? "the top level" : $"'{DisplayOf(parentNode)}'";
        throw new System.InvalidOperationException(
            $"{matches.Count} items in {where} are called '{DisplayOf(matches[0])}'. VRCFury finds this kind of " +
            $"item by its name, so this {opName} would apply to all of them. Rename one first.");
    }

    /// Same idea as GuardUnambiguousPath, but for a DESTINATION path something
    /// is about to be attached under. Every ancestor folder that already exists
    /// must resolve unambiguously, because attaching has to pick one specific
    /// parent object at each level — 'Setting' → 'ShapeModify/Setting' is
    /// exactly as unsafe as moving an ambiguous node itself, even though the
    /// full destination path 'ShapeModify/Setting' is brand new (and therefore
    /// trivially "unambiguous" by GuardUnambiguousPath's own-path-only check):
    /// the segment that actually matters here is the parent folder,
    /// 'ShapeModify'. Stops checking past the first segment that doesn't exist
    /// yet — a genuinely new folder being created isn't ambiguous, it's just new.
    void GuardDestinationChainUnambiguous(string internalPath, string opName)
    {
        if (string.IsNullOrEmpty(internalPath)) return;
        string prefix = "";
        var parent = root;
        foreach (var part in EEMPath.SplitPath(internalPath).Take(System.Math.Max(0, EEMPath.Depth(internalPath) - 1)))
        {
            prefix = EEMPath.Combine(prefix, part);
            // Ancestors are submenu routes, not final-control lookups. A
            // same-name toggle cannot be a parent. Explicit .dup.N selectors
            // already identify the intended submenu among duplicate folders.
            int selector = part.IndexOf(".dup.", System.StringComparison.Ordinal);
            string name = selector < 0 ? part : part.Substring(0, selector);
            var matches = MatchVrcControls(parent.children, name, true);
            var folder = DestinationFolder(parent, part, false);
            if (selector >= 0)
            {
                if (folder == null) return;
                parent = folder;
                continue;
            }
            if (matches.Count == 0) return;   // nothing here yet — new, not ambiguous
            if (matches.Count == 1) { parent = folder; continue; }
            string sources = string.Join(", ", matches.Select(DescribeSource));
            throw new System.InvalidOperationException(
                $"{matches.Count} nodes resolve to '{EEMPath.ToVrcf(prefix)}' (sources: {sources}) — " +
                $"rename one of them before performing this {opName}.");
        }
    }

    /// Same-name items are allowed (they show up side by side in VRChat, and may
    /// well be intentional), so a move never fails just because the name is
    /// taken. The one thing that changes the result is a FOLDER landing on a
    /// same-name folder: VRCFury always combines those into one
    /// (MenuManager.Move → GetSubmenu finds the existing folder and merges into
    /// it). This returns that folder so the caller can say so first.
    ExpressionNode FolderMergeTarget(ExpressionNode moving, string toInternalPath)
    {
        if (moving == null || !moving.IsFolderLike) return null;
        toInternalPath = EEMPath.Norm(toInternalPath);
        if (EEMPath.PathEq(PathOf(moving), toInternalPath)) return null;
        var parentPath = EEMPath.ParentPath(toInternalPath);
        var parentNode = string.IsNullOrEmpty(parentPath) ? root : NodeByFolderPath(root, parentPath);
        if (parentNode == null) return null;
        string key = EEMPath.MatchKey(EEMPath.LeafOf(toInternalPath));
        return parentNode.children.FirstOrDefault(c =>
            c != moving && c.IsFolderLike && EEMPath.MatchKey(c.rawName) == key);
    }

    void GuardNoCycle(ExpressionNode moving, ExpressionNode target)
    {
        if (moving == null || target == null) return;
        if (target == moving)
            throw new System.InvalidOperationException("Cannot move an item into itself.");
        if (IsAncestorOf(moving, target))
            throw new System.InvalidOperationException(
                $"Cannot move '{DisplayOf(moving)}' into '{DisplayOf(target)}' — the destination is inside it.");
    }
}
