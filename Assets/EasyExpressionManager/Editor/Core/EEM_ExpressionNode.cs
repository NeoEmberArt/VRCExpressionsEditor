// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — data model — NodeType & ExpressionNode
// ═════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.ScriptableObjects;

public enum NodeType { Folder, Toggle, Slider, SubMenu }

/// One VRCFury Full Controller entry that contributes to materialising a
/// prefix folder — the component it lives on, plus the exact prefix string
/// stored on that entry (matched by value, since array indexes shift as
/// entries get removed).
public struct FCPrefixGenerator
{
    public SerializedObject owner;
    public string prefix;
    public FCPrefixGenerator(SerializedObject owner, string prefix) { this.owner = owner; this.prefix = prefix; }
}

/// <summary>
/// A single row in the tree. A node may be produced by several different sources
/// at once (a VRC control that a Fury toggle also nests under, for example), so
/// the "source" fields are not mutually exclusive.
///
/// v2.2.0: identity is no longer derived from the display name. `rawName` is the
/// literal string stored in the asset (rich-text tags and all) and `stableKey` is
/// a collision-free identifier used for expansion state, selection and re-binding
/// after a rescan. Strip()/Display() are for drawing only.
/// </summary>
public class ExpressionNode
{
    // ── identity ──────────────────────────────────────────────────────────────
    public string rawName = "";        // exactly what the asset stores
    public string sourceKey = "";      // backing identity, independent of display path
    public List<ExpressionNode> mergedSources = new List<ExpressionNode>();
    public string stableKey = "";      // unique within a scan; survives rescans
    public string fullPath;            // Fury content.name for toggles (raw, as authored)
    public string nativePath;          // exact source path captured before move merges

    public bool     isExpanded;
    public NodeType type;
    public Texture2D icon;
    public List<ExpressionNode> children = new List<ExpressionNode>();
    public ExpressionNode parent;

    // ── VRC native ────────────────────────────────────────────────────────────
    public VRCExpressionsMenu         sourceMenu;
    public VRCExpressionsMenu.Control sourceControl;
    public int sourceControlCount = -1; // source array size when this row was scanned
    public SerializedObject           sourceObject;   // VRCFury toggle component

    // ── VRCFury Full Controller ───────────────────────────────────────────────
    public bool                       isFromFullController;
    public VRCExpressionsMenu         sourceFullControllerMenu;
    public SerializedObject           sourceFullControllerObject;
    public int                        fcEntryIndex = -1;
    /// Menu assets from the Full Controller entry root down to (and including)
    /// sourceFullControllerMenu. Used for clone-on-write so we never edit a
    /// package asset in place.
    public List<VRCExpressionsMenu>   fcChain = new List<VRCExpressionsMenu>();

    // ── generated-folder bookkeeping (what would re-create this if deleted) ───
    public bool isVirtualFolder;        // registered in the virtual folder store
    /// The path this virtual menu is REGISTERED under. Virtual menus are placed
    /// before move records run, so once a parent folder is moved by a record the
    /// node's tree path (PathOf) no longer matches its registration. Anything
    /// that edits the registration must use this, not PathOf.
    public string virtualPath;
    /// Folder that exists only as a parent on the way to a virtual menu
    /// (e.g. "A" for a virtual menu registered at "A/B").
    public bool isVirtualIntermediate;
    public bool isFCPrefixFolder;       // materialised by a Full Controller entry prefix
    /// Every distinct (owning component, prefix string) pair that contributes
    /// to this folder. A single folder path can be produced by entries on
    /// SEVERAL DIFFERENT VRCFury components at once — e.g. one component owns
    /// "Setting/autoSet", another owns "Setting/soundFX", a third owns bare
    /// "Setting" — so a single owner reference isn't enough to find and remove
    /// everything that would regenerate this folder. Matched by prefix VALUE,
    /// not array index, since deleting one entry shifts the indices of the rest.
    public List<FCPrefixGenerator> fcPrefixGenerators = new List<FCPrefixGenerator>();
    public bool isMoveGeneratedFolder;  // materialised by a MoveMenuItem toPath
    public List<Component> moveGenerators = new List<Component>();
    public List<Component> appliedMoveGenerators = new List<Component>(); // records applied to this control itself

    // ── override badges ───────────────────────────────────────────────────────
    public bool hasManagedMove, hasManagedIcon;

    /// More than one component resolves to this exact path (duplicate Fury paths).
    public int duplicateSourceCount;

    /// The raw name contains a '/', which is escaped in VRCFury paths.
    public bool nameContainsSeparator;
    /// This node itself was created by a native source, before any move merge.
    /// Merged sources retain their own marker and managed-move coverage.
    public bool hasNativeFolderSource;
    /// How many items in the same folder show this name (this one included).
    /// 1 = unique. Same-name items are allowed; this only drives a quiet marker.
    public int sameNameCount = 1;

    public bool IsFolderLike =>
        type == NodeType.Folder || type == NodeType.SubMenu || isVirtualFolder;
}
