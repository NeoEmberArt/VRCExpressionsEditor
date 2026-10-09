// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — window state & lifecycle
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager : EditorWindow
{
    // ── state ──────────────────────────────────────────────────────────────────
    private GameObject     avatar;
    private Vector2        scroll;
    private ExpressionNode root;
    private ExpressionNode selected;
    private string         searchFilter       = "";
    private string         cachedSearchFilter;
    private HashSet<ExpressionNode>  searchVisibleNodes = new HashSet<ExpressionNode>();
    private Dictionary<string, bool> expansionStates    = new Dictionary<string, bool>();

    // inline rename
    private ExpressionNode inlineEditingNode;
    private string         inlineRenameBuffer = "";

    // multi-select
    private bool                    selectionMode = false;
    private HashSet<ExpressionNode> selectedNodes = new HashSet<ExpressionNode>();
    /// Explicit anchor for shift-range selection. A HashSet has no order, so the
    /// old `selectedNodes.LastOrDefault()` anchor was arbitrary.
    private ExpressionNode          selectionAnchor;
    private List<ExpressionNode>    allNodesFlatList = new List<ExpressionNode>();
    private HashSet<string> bulkMoveSourceKeys = new HashSet<string>();
    private Texture2D               bulkIconPending;

    // drag
    private ExpressionNode draggedNode, dragCandidate;
    private Vector2        dragStartMouse;
    private double         dragStartTime;
    private const double   DRAG_HOLD_SECONDS = 0.14d;
    private const float    DRAG_START_PIXELS = 6f;

    // pending icon auto-apply (queued because ObjectField fires mid-layout)
    private ExpressionNode pendingIconNode;
    private Texture2D      pendingIcon;

    // deferred rescan — one scan per frame instead of one per mutation
    private bool rescanQueued;
    private bool externalChangesPending;

    private List<string> virtualFolderPaths = new List<string>();

    private System.Type cachedVrcfCompType;
    private Dictionary<string, System.Type> featureTypeCache = new Dictionary<string, System.Type>();
    private string cachedIconMenuPathProp;
    private string cachedIconTextureProp;

    private HashSet<VRCExpressionsMenu> visitedMenus = new HashSet<VRCExpressionsMenu>();

    /// Original package menu asset → the project-owned copy we made of it.
    /// Cleared on every scan; within one operation it stops a second edit to the
    /// same menu from cloning the untouched original all over again.
    private Dictionary<(Object owner, int entry, VRCExpressionsMenu menu), VRCExpressionsMenu> fcCloneMap =
        new Dictionary<(Object, int, VRCExpressionsMenu), VRCExpressionsMenu>();
    private Dictionary<(Object owner, int entry, VRCExpressionsMenu.Control control), VRCExpressionsMenu.Control> fcControlCloneMap =
        new Dictionary<(Object, int, VRCExpressionsMenu.Control), VRCExpressionsMenu.Control>();

    /// Records read during the last scan that point at a path nothing resolves to.
    /// v2.2.0 no longer destroys these behind the user's back — they are surfaced.
    private List<string> danglingRecordDescriptions = new List<string>();
    private HashSet<Component> resolvedMoveRecords = new HashSet<Component>();
    private HashSet<Component> unresolvedMoveRecords = new HashSet<Component>();
    private HashSet<Component> resolvedIconRecords = new HashSet<Component>();
    /// Folders whose direct child count exceeds what VRChat shows on one page.
    private List<string> overCapacityMenus = new List<string>();

    private const string MANAGER_OBJECT_NAME = "ExpressionsManager";
    private const string VF_PREFS_PREFIX     = "EASY_VirtualFolders_";
    private const string CLONE_ROOT          = "Assets/EasyExpressionManager_Generated";
    /// VRChat renders 8 controls per menu page.
    private const int    MENU_CAPACITY       = 8;
    /// Used when developer.json is missing, has no VersionCode key, or the key
    /// is empty. Bump this alongside a real release; day-to-day version bumps
    /// for testing can just go in developer.json instead, no recompile needed.
    public  const string PLUGIN_VERSION_FALLBACK = "2.3.0";

    /// developer.json's VersionCode, when present, overrides the compiled-in
    /// fallback — same override pattern as DebugJSON, and re-checked the same
    /// way (file's own last-write time), so editing it takes effect immediately.
    string ResolveVersion() => DeveloperString("VersionCode", PLUGIN_VERSION_FALLBACK);

    // ── debug / stress-test logging ──────────────────────────────────────────
    private List<Dictionary<string, object>> debugLog = new List<Dictionary<string, object>>();
    private int    debugOpCounter = 0;
    private string debugSessionId;
    private bool   includeTreesInDebugJson = true;
    private const int DEBUG_LOG_MAX_ENTRIES = 2000;

    [MenuItem("Tools/Easy Expression Editor")]
    public static void Open() => GetWindow<EASYExpressionManager>("Easy Expression Editor");

    // ── lifecycle ──────────────────────────────────────────────────────────────

    void OnEnable()
    {
        if (string.IsNullOrEmpty(debugSessionId))
            debugSessionId = System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" +
                             System.Guid.NewGuid().ToString("N").Substring(0, 8);
        Undo.undoRedoPerformed += OnUndoRedo;
        ObjectChangeEvents.changesPublished += OnObjectChanges;
        EditorApplication.hierarchyChanged += OnExternalChange;
        EditorApplication.projectChanged += OnExternalChange;
        UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += OnSceneSaved;
        EditorApplication.delayCall += TryAutoSelect;
    }

    void OnDisable()
    {
        DisposeIconGen();   // closes the private preview scene used for auto icons
        EditorApplication.update -= StatusTick;
        Undo.undoRedoPerformed -= OnUndoRedo;
        ObjectChangeEvents.changesPublished -= OnObjectChanges;
        EditorApplication.hierarchyChanged -= OnExternalChange;
        EditorApplication.projectChanged -= OnExternalChange;
        UnityEditor.SceneManagement.EditorSceneManager.sceneSaved -= OnSceneSaved;
        EditorApplication.delayCall -= TryAutoSelect;
    }

    void OnObjectChanges(ref ObjectChangeEventStream stream)
    {
        if (!avatar || externalChangesPending) return;
        for (int i = 0; i < stream.length; i++)
        {
            int id;
            switch (stream.GetEventType(i))
            {
                case ObjectChangeKind.ChangeGameObjectStructure:
                    stream.GetChangeGameObjectStructureEvent(i, out var structure);
                    id = structure.instanceId;
                    break;
                case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties);
                    id = properties.instanceId;
                    break;
                case ObjectChangeKind.ChangeAssetObjectProperties:
                    stream.GetChangeAssetObjectPropertiesEvent(i, out var assetProperties);
                    id = assetProperties.instanceId;
                    break;
                default:
                    // Structural edits are also coalesced by hierarchyChanged.
                    continue;
            }
            var obj = EditorUtility.InstanceIDToObject(id);
            var transform = obj is Component component ? component.transform : (obj is GameObject go ? go.transform : null);
            bool relevant = transform && transform.IsChildOf(avatar.transform);
            if (obj is VRCExpressionsMenu menu)
                relevant |= visitedMenus.Contains(menu) || allNodesFlatList.Any(row => row.sourceFullControllerMenu == menu);
            if (obj is EEMAvatarState state && liveAvatarStates.TryGetValue(avatar, out var current))
                relevant |= state == current;
            if (relevant) { OnExternalChange(); return; }
        }
    }

    void OnExternalChange()
    {
        if (!avatar) return;
        externalChangesPending = true;
        Repaint();
    }

    void OnSceneSaved(UnityEngine.SceneManagement.Scene scene) => RefreshAvatarIdentity(scene);

    void OnUndoRedo() { if (avatar) { LoadVirtualFolders(); AssetDatabase.SaveAssetIfDirty(AvatarState()); Scan(); } Repaint(); }

    void TryAutoSelect() => TryAutoSelectAvatar(true);

    void TryAutoSelectAvatar(bool scanAfter)
    {
        if (avatar) return;
        VRCAvatarDescriptor found = null;
        foreach (var d in Resources.FindObjectsOfTypeAll<VRCAvatarDescriptor>())
        {
            if (!d || !d.enabled || !d.gameObject.activeInHierarchy) continue;
            if (!d.gameObject.scene.IsValid() || !d.gameObject.scene.isLoaded) continue;
            if (EditorUtility.IsPersistent(d)) continue;
            if (found != null) return;
            found = d;
        }
        if (!found) return;
        SetAvatar(found.gameObject, scanAfter);
    }

    void SetAvatar(GameObject go, bool scanAfter)
    {
        avatar = go;
        // caches are avatar-scoped — a stale VRCFury component type from a previous
        // avatar would silently break every reflection call
        cachedVrcfCompType     = null;
        featureTypeCache.Clear();
        cachedIconMenuPathProp = null;
        cachedIconTextureProp  = null;
        fcCloneMap.Clear();
        fcControlCloneMap.Clear();
        initialTreeSnapshot = null;
        cachedAvatarKey = null;   // recomputed fresh below, then held stable for this selection
        selected = null;
        ExitSelectionMode();
        LoadVirtualFolders();
        if (avatar)
        {
            EnsureBaselineSnapshot();
            if (scanAfter) Scan();
        }
        Repaint();
    }

}
