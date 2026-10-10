// ═══════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — Debug / stress-test logging
//
// Mutations always use Undo and error handling. Developer verification and
// JSON capture run only when DebugJSON is enabled in developer.json.
// ???????????????????????????????????????????????????????????????????????????????

using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEditor;

public partial class EASYExpressionManager
{
    /// Short, human-readable notes about side effects a mutation caused —
    /// "superseded existing record", "removed 3 FC entries across 2
    /// components", etc. Cleared at the start of every LogOp and read once it
    /// finishes. Call NoteConsequence(...) from anywhere during a mutation to
    /// add one.
    List<string> opConsequences = new List<string>();
    void NoteConsequence(string note) => opConsequences.Add(note);

    /// Issues already printed to the Console, so repeats can be left out.
    HashSet<string> lastReportedIssues = new HashSet<string>();

    bool LogOp(string opType, string targetPath, string targetType, string oldValue, string newValue,
               System.Action mutate, Dictionary<string, object> extraContext = null)
    {
        if (externalChangesPending)
        {
            LoadVirtualFolders();
            Scan();
            SetStatus("The avatar changed. The list has refreshed; retry the action.", true);
            return false;
        }
        bool debugEnabled = DeveloperFlag("DebugJSON", false);
        if (debugEnabled) EnsureInitialTreeSnapshot();
        opConsequences.Clear();
        var existingRecordIssues = new HashSet<string>(danglingRecordDescriptions.Select(d => "UNRESOLVED RECORD: " + d));

        int mutationGroup = Undo.GetCurrentGroup();
        System.Exception caught = null;
        try
        {
            mutate();
            VerifyMutationOutcomeOrThrow(opType, targetPath, oldValue, newValue, extraContext);
        }
        catch (System.Exception ex)
        {
            caught = ex;
            Undo.FlushUndoRecordObjects();
            Undo.RevertAllDownToGroup(mutationGroup);
            LoadVirtualFolders();
            QueueRescan();
            // Guards (collision, loop, ambiguous path…) and explicit user
            // cancellations are refused actions, not crashes. Keep both as
            // warnings so a Cancel does not look like a failed mutation.
            if (ex is System.InvalidOperationException || ex is System.OperationCanceledException)
                Debug.LogWarning($"[EASY] {opType} not done: {ex.Message}");
            else
                Debug.LogError($"[EASY] '{opType}' on '{targetPath}' threw: {ex}");
        }
        RefreshAvatarIdentity();
        var avatarState = AvatarState();
        if (avatarState != null)
            AssetDatabase.SaveAssetIfDirty(avatarState);
        if (debugEnabled)
            RecordDebugEntry(opType, targetPath, targetType, oldValue, newValue, caught, extraContext, existingRecordIssues);
        if (caught != null)
            SetStatus(caught is System.InvalidOperationException || caught is System.OperationCanceledException
                ? caught.Message
                : $"{opType} failed — see Console.", true);
        return caught == null;
    }

    /// Detailed outcome capture remains optional, but operations whose callers
    /// provide an expected result must never report success after a mutator
    /// silently returned without applying it. This also protects duplicate
    /// and other helpers outside the narrow operation files without changing
    /// their source semantics.
    void VerifyMutationOutcomeOrThrow(string opType, string targetPath, string oldValue,
                                      string newValue, Dictionary<string, object> context)
    {
        switch (opType)
        {
            case "Rename":
            case "Duplicate":
            case "Delete":
            case "BulkDelete":
            case "IconChange":
            case "BulkIcon":
                break;
            default:
                return;
        }

        // Source-object mutations do not update the in-memory tree until the
        // queued scan. Verify the same post-scan view used by the UI.
        Scan();
        RebindIconOutcomeTargetsAfterScan(opType, context);

        // A path can intentionally have several rows before a rename (for
        // example, shared/duplicate native controls). The new path is still a
        // required postcondition, but an old-path survivor is not evidence of
        // failure when the pre-operation path was already ambiguous.
        string verifyOldValue = oldValue;
        if (opType == "Rename" && context != null &&
            context.TryGetValue("previousMatchCount", out var previousValue) &&
            previousValue is int previousMatchCount && previousMatchCount > 1)
            verifyOldValue = null;

        var issues = new List<object>();
        VerifyExpectedOutcome(opType, targetPath, verifyOldValue, newValue, context, issues);
        string failure = issues.Select(i => i?.ToString() ?? "")
            .FirstOrDefault(s => s.StartsWith("OUTCOME MISMATCH") || s.StartsWith("EMPTY DUPLICATE"));
        if (!string.IsNullOrEmpty(failure))
            throw new System.InvalidOperationException(failure);
    }

    /// Clone-on-write can replace a source object between the pre-operation
    /// capture and the verification Scan(), which changes the instance-ID
    /// portion of a node's stableKey. Rebind only when the post-scan path has
    /// an item already carrying the expected icon; otherwise leave the old key
    /// in place so VerifyExpectedOutcome still reports a real disappearance.
    void RebindIconOutcomeTargetsAfterScan(string opType, Dictionary<string, object> context)
    {
        if ((opType != "IconChange" && opType != "BulkIcon") || context == null ||
            !context.TryGetValue("expectedIconId", out var wantedValue) ||
            !(wantedValue is int wantedId) ||
            !context.TryGetValue("expectedIconTargets", out var targetsValue) ||
            !(targetsValue is List<object> iconTargets)) return;

        foreach (var target in iconTargets.OfType<Dictionary<string, object>>())
        {
            string key = target.TryGetValue("stableKey", out var keyValue) ? keyValue as string : null;
            if (!string.IsNullOrEmpty(key) && allNodesFlatList.Any(n => n.stableKey == key)) continue;

            string path = target.TryGetValue("path", out var pathValue) ? pathValue as string : null;
            var matches = FindAllNodesAtPath(root, path ?? "")
                .Where(n => (n.icon ? n.icon.GetInstanceID() : 0) == wantedId)
                .ToList();
            if (matches.Count > 0)
                target["stableKey"] = matches[0].stableKey;
        }
    }

    /// Captured once per avatar, the first time an operation runs — "what the
    /// tree looked like before any of this session's edits". Cleared whenever
    /// the avatar changes (see SetAvatar) so a new avatar gets its own.
    Dictionary<string, object> initialTreeSnapshot;

    void EnsureInitialTreeSnapshot()
    {
        if (initialTreeSnapshot != null || root == null) return;
        initialTreeSnapshot = NodeToDict(root);
    }

    void RecordDebugEntry(string opType, string targetPath, string targetType, string oldValue, string newValue,
                          System.Exception error, Dictionary<string, object> extraContext = null, ISet<string> existingRecordIssues = null)
    {
        debugOpCounter++;
        var issues = new List<object>();

        if (error != null)
        {
            issues.Add($"EXCEPTION: {error.GetType().Name}: {error.Message}");
        }

        int nodeCountAfter = 0;
        try { nodeCountAfter = CaptureRealState(issues); }
        catch (System.Exception scanEx)
        {
            issues.Add($"VERIFY-SCAN-FAILED: {scanEx.GetType().Name}: {scanEx.Message}");
            Debug.LogError($"[EASY Debug] Post-op verification scan itself threw: {scanEx}");
        }

        if (error == null)
        {
            try { VerifyExpectedOutcome(opType, targetPath, oldValue, newValue, extraContext, issues); }
            catch (System.Exception verifyEx)
            {
                issues.Add($"OUTCOME-CHECK-FAILED: {verifyEx.GetType().Name}: {verifyEx.Message}");
            }
        }

        // Only issue classes that specifically mean "this operation's own
        // stated outcome did not happen" flip success to false. Pre-existing
        // tree hygiene findings (duplicate paths elsewhere, unaddressable
        // names, over-capacity menus, etc.) are surfaced but don't fail an
        // unrelated operation just for existing — otherwise every op on an
        // avatar with any existing issue would report failure regardless of
        // what it actually did.
        bool outcomeFailed = issues.Any(i => IsOperationFailure(i?.ToString() ?? "", opType, existingRecordIssues));
        var stateWarnings = issues.Select(i => i.ToString()).Where(s =>
            s.StartsWith("CAPACITY:") || (s.StartsWith("UNRESOLVED RECORD:") && !IsOperationFailure(s, opType, existingRecordIssues))).ToList();

        // Compact by design: one line of identity (from/to/method), one line
        // of outcome (ok/note/issues/error), no embedded tree. 20 operations
        // should read as 20 short entries, not 20 copies of the whole avatar.
        var entry = new Dictionary<string, object>
        {
            ["i"]      = debugOpCounter,
            ["t"]      = System.DateTime.Now.ToString("HH:mm:ss.fff"),
            ["op"]     = opType,
            ["from"]   = oldValue,
            ["to"]     = newValue,
            ["method"] = DeriveMethod(opType, extraContext),
            ["ok"]     = error == null && !outcomeFailed,
            ["nodes"]  = nodeCountAfter,
        };
        if (targetType != "Multiple" && !string.IsNullOrEmpty(targetPath) && targetPath != oldValue)
            entry["target"] = targetPath;
        if (opConsequences.Count > 0) entry["note"] = string.Join("; ", opConsequences);
        if (issues.Count > 0) entry["issues"] = issues;
        if (stateWarnings.Count > 0) entry["stateWarnings"] = stateWarnings.Cast<object>().ToList();
        if (error != null) entry["error"] = $"{error.GetType().Name}: {error.Message}";
        if (extraContext != null && extraContext.Count > 0) entry["context"] = extraContext;

        debugLog.Add(entry);
        while (debugLog.Count > DEBUG_LOG_MAX_ENTRIES) debugLog.RemoveAt(0);

        // The full list always goes into the JSON entry. The Console only gets
        // what's NEW since the previous operation — otherwise every move on an
        // avatar with long-standing issues (duplicate GoGo Loco, '/' in names…)
        // reprinted the same 30 lines and buried the one that mattered.
        var issueTexts = issues.Select(i => i.ToString()).ToList();
        var fresh      = issueTexts.Where(s => !lastReportedIssues.Contains(s)).ToList();
        int known      = issueTexts.Count - fresh.Count;
        lastReportedIssues = new HashSet<string>(issueTexts);
        if (fresh.Count > 0)
            Debug.LogWarning($"[EASY Debug] {fresh.Count} " + (fresh.All(stateWarnings.Contains) ? "state warning(s)" : "new issue(s)") + $" after '{opType}' on '{targetPath}'" +
                (known > 0 ? $" ({known} already reported earlier, see Debug JSON)" : "") + ":\n- " +
                string.Join("\n- ", fresh));

        Repaint();
    }

    bool IsOperationFailure(string issue, string opType, ISet<string> existingRecordIssues)
    {
        // A restored snapshot may contain its original stale or build-only
        // icon records. Restore problems are reported explicitly by its own
        // reconciliation checks; these scan findings remain visible warnings.
        if (issue.StartsWith("UNRESOLVED RECORD"))
            return opType != "Restore" && !(existingRecordIssues?.Contains(issue) ?? false);
        return issue.StartsWith("OUTCOME MISMATCH") || issue.StartsWith("EXCEPTION") ||
            issue.StartsWith("EMPTY DUPLICATE") || issue.StartsWith("RESTORE INCOMPLETE") ||
            issue.StartsWith("IMPORT SKIPPED") || issue.StartsWith("VERIFY-SCAN-FAILED") || issue.StartsWith("OUTCOME-CHECK-FAILED");
    }

    /// Best-effort description of HOW an operation actually wrote its change —
    /// straight to the component/control (Direct), through the
    /// ExpressionsManager object (a MoveMenuItem/OverrideMenuIcon record), or
    /// only in the plugin's own virtual-folder store. Derived from the
    /// sourceCategory context every op already passes, so it doesn't require
    /// separate plumbing at every call site.
    string DeriveMethod(string opType, Dictionary<string, object> context)
    {
        string cat = context != null && context.TryGetValue("sourceCategory", out var c) ? c as string : null;
        switch (opType)
        {
            case "Move":
            case "BulkMove":
                if (cat == "VRCFuryToggle") return "Direct (Fury toggle content.name)";
                if (cat == "VirtualFolder") return "Virtual folder (path rewrite only)";
                return "ExpressionsManager (MoveMenuItem)";
            case "Rename":
                if (cat == "VRCFuryToggle") return "Direct (Fury toggle content.name)";
                if (cat == "VRCNative") return "Direct (native control name)";
                if (cat == "FullController") return "Direct (FC control name, clone-on-write)";
                if (cat == "FCPrefixFolder") return "Direct (FC entry prefix, clone-on-write)";
                if (cat == "VirtualFolder") return "Virtual folder (path rewrite only)";
                return "Direct";
            case "Delete":
            case "BulkDelete":
                if (cat == "VRCFuryToggle") return "Direct (component destroyed)";
                if (cat == "VRCNative") return "Direct (control removed from menu)";
                if (cat == "FullController") return "Direct (FC control removed, clone-on-write)";
                if (cat == "FCPrefixFolder") return "ExpressionsManager cleanup (FC entries removed)";
                if (cat == "VirtualFolder") return "Virtual folder store";
                return "Direct + record/override cleanup sweep";
            case "IconChange":
            case "BulkIcon":
                return "Direct control icon, or ExpressionsManager (OverrideMenuIcon) fallback for folders";
            case "Duplicate":       return "Direct (component/control cloned)";
            case "Create":          return "Virtual folder store";
            case "Snapshot":        return "Full component + menu asset capture";
            case "Restore":         return "Full component + menu asset reconcile";
            case "StructureImport": return "Multiple (move + virtual folder creation)";
            case "CleanRecords":    return "ExpressionsManager (stale MoveMenuItem records removed)";
            default:                return opType;
        }
    }

}
