// ═════════════════════════════════════════════════════════════════════════════
// Easy Expression Manager — auto icon: objects behind VRC menu controls
// ═════════════════════════════════════════════════════════════════════════════
//
// A VRC menu control (or a Full Controller menu item) doesn't name the objects
// it shows — it sets a parameter, and the avatar's animator does the rest. To
// find what to photograph, follow that parameter:
//
//   control → its parameter and "on" value
//          → animator transitions whose conditions mean "this control is on"
//            (and blend trees driven by that parameter, at their high end)
//          → the animation clips those states play
//          → GameObjects those clips switch ON, plus material swaps
//
// Searched at most one level deep (one level of sub-state machines, one level
// of blend-tree children) and indexed once per controller, so selecting items
// stays quick even on large FX controllers.
//
// Everything here only reads assets — nothing is modified or unpacked.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public partial class EASYExpressionManager
{
    /// One transition (or blend tree) that reacts to a parameter.
    struct ParamUse
    {
        public AnimatorCondition[] conditions;   // null for a blend tree
        public Motion motion;                    // what plays when it fires
        public BlendTree blendTree;              // set when the parameter drives a blend tree
    }

    [System.NonSerialized] Dictionary<int, Dictionary<string, List<ParamUse>>> animIndexCache =
        new Dictionary<int, Dictionary<string, List<ParamUse>>>();

    /// Candidates for a native VRC control or a Full Controller item: objects its
    /// animation switches on, and meshes it changes.
    void ControlCandidates(ExpressionNode n, Dictionary<Renderer, Dictionary<int, Material>> swaps,
                           List<GameObject> turnedOn, List<GameObject> touched)
    {
        var c = n?.sourceControl;
        if (c == null || !avatar) return;

        bool radial = c.type == VRCExpressionsMenu.Control.ControlType.RadialPuppet;
        string param = radial
            ? (c.subParameters != null && c.subParameters.Length > 0 ? c.subParameters[0]?.name : null)
            : c.parameter?.name;
        if (string.IsNullOrEmpty(param)) return;
        float onValue = radial ? 1f : (Mathf.Approximately(c.value, 0f) ? 1f : c.value);

        foreach (var (controller, bindRoot) in ControllersFor(n))
        {
            var index = AnimIndex(controller);
            if (!index.TryGetValue(param, out var uses)) continue;
            foreach (var use in uses)
            {
                foreach (var clip in ClipsWhenOn(use, param, onValue))
                    ReadClip(clip, bindRoot, turnedOn, touched, swaps);
            }
        }
    }

    /// The animator controllers a control's parameter lives in, each with the
    /// object its animation paths are relative to.
    IEnumerable<(AnimatorController, Transform)> ControllersFor(ExpressionNode n)
    {
        if (n.isFromFullController && n.sourceFullControllerObject?.targetObject is Component fc)
        {
            var so = ReadSO(n.sourceFullControllerObject);
            var rootOverride = so?.FindProperty("content.rootObjOverride")?.objectReferenceValue as GameObject;
            var fcRoot = rootOverride ? rootOverride.transform : fc.transform;
            var list = so?.FindProperty("content.controllers");
            if (list != null && list.isArray)
                for (int i = 0; i < list.arraySize; i++)
                {
                    var ac = list.GetArrayElementAtIndex(i)?.FindPropertyRelative("controller.objRef")?.objectReferenceValue
                             as AnimatorController;
                    if (ac) yield return (ac, fcRoot);
                }
            yield break;
        }

        var desc = avatar ? avatar.GetComponent<VRCAvatarDescriptor>() : null;
        if (!desc) yield break;
        var dso = new SerializedObject(desc);
        var layers = dso.FindProperty("baseAnimationLayers");
        if (layers == null || !layers.isArray) yield break;
        for (int i = 0; i < layers.arraySize; i++)
        {
            var el = layers.GetArrayElementAtIndex(i);
            if (el.FindPropertyRelative("isDefault")?.boolValue == true) continue;
            var ac = el.FindPropertyRelative("animatorController")?.objectReferenceValue as AnimatorController;
            if (ac) yield return (ac, avatar.transform);
        }
    }

    /// parameter → everything in this controller that reacts to it. Built once.
    Dictionary<string, List<ParamUse>> AnimIndex(AnimatorController ac)
    {
        int id = ac.GetInstanceID();
        if (animIndexCache.TryGetValue(id, out var cached)) return cached;
        var index = new Dictionary<string, List<ParamUse>>(System.StringComparer.Ordinal);

        void Add(string p, ParamUse u)
        {
            if (string.IsNullOrEmpty(p)) return;
            if (!index.TryGetValue(p, out var l)) index[p] = l = new List<ParamUse>();
            l.Add(u);
        }
        void AddTransition(AnimatorTransitionBase t)
        {
            if (t == null || t.destinationState == null || t.conditions == null) return;
            foreach (var p in t.conditions.Select(x => x.parameter).Distinct())
                Add(p, new ParamUse { conditions = t.conditions, motion = t.destinationState.motion });
        }
        void Machine(AnimatorStateMachine sm, int depth)
        {
            if (!sm) return;
            foreach (var t in sm.anyStateTransitions) AddTransition(t);
            foreach (var t in sm.entryTransitions)    AddTransition(t);
            foreach (var cs in sm.states)
            {
                var st = cs.state; if (!st) continue;
                foreach (var t in st.transitions) AddTransition(t);
                if (st.motion is BlendTree bt)
                {
                    Add(bt.blendParameter,  new ParamUse { blendTree = bt, motion = bt });
                    Add(bt.blendParameterY, new ParamUse { blendTree = bt, motion = bt });
                }
            }
            if (depth < 1) foreach (var sub in sm.stateMachines) Machine(sub.stateMachine, depth + 1);
        }
        foreach (var layer in ac.layers) Machine(layer.stateMachine, 0);

        animIndexCache[id] = index;
        return index;
    }

    /// The clips that play when the control is on.
    IEnumerable<AnimationClip> ClipsWhenOn(ParamUse use, string param, float onValue)
    {
        if (use.blendTree != null)
        {
            // blend tree driven by the parameter: its "fully on" end
            var kids = use.blendTree.children;
            if (kids == null || kids.Length == 0) yield break;
            var top = kids.OrderByDescending(k => k.threshold).First();
            if (top.motion is AnimationClip tc) yield return tc;
            yield break;
        }

        foreach (var cond in use.conditions)
            if (cond.parameter == param && !ConditionIsOn(cond, onValue)) yield break;

        if (use.motion is AnimationClip clip) yield return clip;
        else if (use.motion is BlendTree bt && bt.children != null)
            foreach (var k in bt.children)   // one level of blend-tree children
                if (k.motion is AnimationClip kc) yield return kc;
    }

    static bool ConditionIsOn(AnimatorCondition c, float onValue)
    {
        switch (c.mode)
        {
            case AnimatorConditionMode.If:       return true;
            case AnimatorConditionMode.IfNot:    return false;
            case AnimatorConditionMode.Equals:   return Mathf.Abs(c.threshold - onValue) < 0.01f;
            case AnimatorConditionMode.NotEqual: return Mathf.Abs(c.threshold) < 0.01f && Mathf.Abs(onValue) > 0.01f;
            case AnimatorConditionMode.Greater:  return onValue > c.threshold;
            case AnimatorConditionMode.Less:     return onValue < c.threshold;
            default:                             return false;
        }
    }

    /// Objects a clip switches on (m_IsActive ending at 1), renderers it changes,
    /// and the materials it swaps in.
    void ReadClip(AnimationClip clip, Transform bindRoot, List<GameObject> turnedOn, List<GameObject> touched,
                  Dictionary<Renderer, Dictionary<int, Material>> swaps)
    {
        if (!clip) return;
        foreach (var b in AnimationUtility.GetCurveBindings(clip))
        {
            if (b.type == typeof(GameObject) && b.propertyName == "m_IsActive")
            {
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                if (curve == null || curve.keys.Length == 0 || curve.keys[curve.keys.Length - 1].value < 0.5f) continue;
                var go = ResolveBinding(bindRoot, b.path);
                if (go && !turnedOn.Contains(go)) turnedOn.Add(go);
            }
            else if (typeof(Renderer).IsAssignableFrom(b.type))
            {
                var go = ResolveBinding(bindRoot, b.path);
                if (go && go.GetComponent<Renderer>() && !touched.Contains(go)) touched.Add(go);
            }
        }
        foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
        {
            const string prefix = "m_Materials.Array.data[";
            if (!b.propertyName.StartsWith(prefix)) continue;
            if (!int.TryParse(b.propertyName.Substring(prefix.Length).TrimEnd(']'), out int slot)) continue;
            var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
            if (keys == null || keys.Length == 0) continue;
            var mat = keys[keys.Length - 1].value as Material;
            var go  = ResolveBinding(bindRoot, b.path);
            var r   = go ? go.GetComponent<Renderer>() : null;
            if (!r || !mat) continue;
            if (!swaps.TryGetValue(r, out var d)) swaps[r] = d = new Dictionary<int, Material>();
            d[slot] = mat;
            if (!touched.Contains(go)) touched.Add(go);
        }
    }

    /// Animation paths are relative to the controller's root; Full Controller
    /// clips fall back to the avatar root, as VRCFury does.
    GameObject ResolveBinding(Transform bindRoot, string path)
    {
        if (!bindRoot) return null;
        var t = string.IsNullOrEmpty(path) ? bindRoot : bindRoot.Find(path);
        if (!t && avatar && bindRoot != avatar.transform)
            t = string.IsNullOrEmpty(path) ? null : avatar.transform.Find(path);
        return t ? t.gameObject : null;
    }
}
