// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — design picker, window sizing, shared UI helpers
// ═════════════════════════════════════════════════════════════════════════════
//
// Two designs draw the SAME window state (tree, selection, search, dialogs):
//   • Split View — list on the left, details on the right (default)
//   • Classic    — the original single-column v2 layout
// Switching is instant and remembered per user through EditorPrefs.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public enum EEMDesign { Split, Classic }

public partial class EASYExpressionManager
{
    // v2 key: earlier test builds stored other design ids under the old key,
    // so a fresh key makes sure everyone starts on Split View.
    const string DESIGN_PREF = "EASY_DesignStyle_v2";
    const string SPLIT_PREF  = "EASY_SplitRatio";

    static readonly EEMDesign[] DesignOrder = { EEMDesign.Split, EEMDesign.Classic };

    static string DesignName(EEMDesign d) => d == EEMDesign.Classic ? "Classic" : "Split View";

    // ── persisted choice ──────────────────────────────────────────────────────
    [System.NonSerialized] bool      designLoaded;
    [System.NonSerialized] EEMDesign designValue;

    EEMDesign Design
    {
        get
        {
            if (!designLoaded)
            {
                int v = EditorPrefs.GetInt(DESIGN_PREF, (int)EEMDesign.Split);
                designValue  = System.Enum.IsDefined(typeof(EEMDesign), v) ? (EEMDesign)v : EEMDesign.Split;
                designLoaded = true;
            }
            return designValue;
        }
        set
        {
            designValue  = value;
            designLoaded = true;
            EditorPrefs.SetInt(DESIGN_PREF, (int)value);
        }
    }

    void SetDesign(EEMDesign d)
    {
        if (d == Design) return;
        Design       = d;
        skin         = null;
        widthChecked = false;   // the other design may need more room
        GUI.FocusControl(null);
        Repaint();
    }

    void DrawDesignPicker(GUIStyle style)
    {
        var content = new GUIContent("Design: " + DesignName(Design), "Change how this window looks. Applies instantly.");
        float w = style.CalcSize(content).x + 4f;
        if (EditorGUILayout.DropdownButton(content, FocusType.Passive, style, GUILayout.Width(w)))
        {
            var m = new GenericMenu();
            foreach (var d in DesignOrder)
            {
                var cap = d;
                m.AddItem(new GUIContent(DesignName(d)), Design == d, () => SetDesign(cap));
            }
            m.AddSeparator("");
            m.AddItem(new GUIContent("Show Name Formatting"), RichNames, () => RichNames = !RichNames);
            m.ShowAsContext();
        }
    }

    // ── window width ──────────────────────────────────────────────────────────
    // Each design has a width below which buttons start getting clipped. That
    // width becomes the window's minimum, and a floating window that opens
    // narrower than it is widened once so nothing starts out cut off.
    const float MIN_WINDOW_HEIGHT = 360f;
    [System.NonSerialized] bool widthChecked;

    float RequiredWidth()
    {
        // Classic: fixed-width toolbar + "Exit Select" and the multi-select bar.
        // Split:   230 list + 5 divider + 285 details (fits the toolbar too).
        float w = Design == EEMDesign.Classic ? 660f : 520f;
        if (DebugEnabled) w += Design == EEMDesign.Classic ? 145f : 90f;
        return w;
    }

    void EnforceWindowWidth()
    {
        if (Event.current.type != EventType.Layout) return;

        float req = RequiredWidth();
        if (!Mathf.Approximately(minSize.x, req) || minSize.y < MIN_WINDOW_HEIGHT)
            minSize = new Vector2(req, MIN_WINDOW_HEIGHT);

        if (widthChecked) return;
        widthChecked = true;
        if (docked || position.width >= req - 0.5f) return;

        // Resizing from inside OnGUI fights the layout pass — do it right after.
        EditorApplication.delayCall += () =>
        {
            if (this == null || docked) return;
            var p = position;
            if (p.width >= req) return;
            p.width = req;
            if (p.height < MIN_WINDOW_HEIGHT) p.height = 560f;
            position = p;
        };
    }

    // ── frame-stable UI state ─────────────────────────────────────────────────
    // IMGUI lays the window out once (Layout) and then replays it for every other
    // event. Anything that adds or removes controls mid-replay — expanding a
    // folder, the details pane switching items, rescanning — has to wait until
    // the pass is over, or Unity throws "Getting control N's position in a group
    // with only N controls". These snapshots and the deferred queue prevent that.
    [System.NonSerialized] ExpressionNode uiSel;
    [System.NonSerialized] bool           uiMulti;
    [System.NonSerialized] List<System.Action> deferredUi = new List<System.Action>();

    void Defer(System.Action a) { if (a != null) deferredUi.Add(a); }

    void RunDeferredUi()
    {
        if (deferredUi.Count == 0) return;
        var run = deferredUi.ToArray();
        deferredUi.Clear();
        foreach (var a in run) a();
        Repaint();
    }

}
