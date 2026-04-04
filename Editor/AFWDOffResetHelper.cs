#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.Components;

public class AFWDOffResetHelper : EditorWindow
{
    public const string Name = "WD Off Reset Helper";

    private VRCAvatarDescriptor avatarDescriptor;
    private enum AnimLayerType { Base, Additive, Gesture, Action, FX, Sitting, TPose, IKPose }
    private AnimLayerType selectedLayerType = AnimLayerType.FX;

    private AnimatorController selectedController;
    private int selectedLayerIndex = 0;
    private AnimatorState selectedState;

    private System.Action pendingGroupAction;
    private Dictionary<string, bool> foldoutStates = new Dictionary<string, bool>();
    private Dictionary<EditorCurveBinding, bool> displayBindings = new Dictionary<EditorCurveBinding, bool>();
    private Vector2 scrollPos;
    private string searchFilter = "";
    private bool hasSearched = false;
    private bool isUniqueMode = true;

    [MenuItem("TohruTheDragon/Avatar Fork/" + Name)]
    public static void ShowWindow() => GetWindow<AFWDOffResetHelper>($"AF {Name}");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("If your avatar is Write Defaults: On, then this tool is not helpful for you!", MessageType.Warning);
        EditorGUILayout.HelpBox("This tool helps you find all blendshapes, that are only animated in the Reset animtion, and those, that are animated elsewhere, but not in the Reset animation.", MessageType.Info);

        avatarDescriptor = (VRCAvatarDescriptor)EditorGUILayout.ObjectField("Avatar Descriptor", avatarDescriptor, typeof(VRCAvatarDescriptor), true);

        if (avatarDescriptor == null)
        {
            EditorGUILayout.HelpBox("Please assign a VRCAvatarDescriptor.", MessageType.Info);
            return;
        }

        EditorGUI.BeginChangeCheck();
        selectedLayerType = (AnimLayerType)EditorGUILayout.EnumPopup("Layer Category", selectedLayerType);
        FetchController();
        if (EditorGUI.EndChangeCheck()) { ResetState(); }

        if (selectedController == null)
        {
            EditorGUILayout.HelpBox("No Animator Controller found in this slot.", MessageType.Warning);
            return;
        }

        string[] layerNames = selectedController.layers.Select(l => l.name).ToArray();
        selectedLayerIndex = EditorGUILayout.Popup("Layer to Audit From", Mathf.Clamp(selectedLayerIndex, 0, layerNames.Length - 1), layerNames);

        var statesInSelectedLayer = GetAllStates(selectedController.layers[selectedLayerIndex].stateMachine);
        string[] stateNames = statesInSelectedLayer.Select(s => s.name).ToArray();

        int stateIndex = statesInSelectedLayer.IndexOf(selectedState);
        stateIndex = EditorGUILayout.Popup("State to Audit", Mathf.Max(0, stateIndex), stateNames);
        if (statesInSelectedLayer.Count > 0) selectedState = statesInSelectedLayer[stateIndex];

        EditorGUILayout.Space();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Find Unique Properties\n(Cleanup Mode)", GUILayout.Height(40)))
        {
            isUniqueMode = true;
            AnalyzeProperties();
        }
        if (GUILayout.Button("Find Missing Blendshapes\n(Sync Mode)", GUILayout.Height(40)))
        {
            isUniqueMode = false;
            AnalyzeProperties();
        }
        EditorGUILayout.EndHorizontal();

        if (hasSearched && displayBindings.Count == 0)
        {
            EditorGUILayout.HelpBox(isUniqueMode ? "No unique properties found." : "No missing blendshapes found.", MessageType.Info);
        }

        if (displayBindings.Count > 0)
        {
            DrawResults();
        }
    }

    private void FetchController()
    {
        var layers = avatarDescriptor.baseAnimationLayers;
        var specialLayers = avatarDescriptor.specialAnimationLayers;
        selectedController = selectedLayerType switch
        {
            AnimLayerType.Base => layers[0].animatorController as AnimatorController,
            AnimLayerType.Additive => layers[1].animatorController as AnimatorController,
            AnimLayerType.Gesture => layers[2].animatorController as AnimatorController,
            AnimLayerType.Action => layers[3].animatorController as AnimatorController,
            AnimLayerType.FX => layers[4].animatorController as AnimatorController,
            AnimLayerType.Sitting => specialLayers[0].animatorController as AnimatorController,
            AnimLayerType.TPose => specialLayers[1].animatorController as AnimatorController,
            AnimLayerType.IKPose => specialLayers[2].animatorController as AnimatorController,
            _ => null
        };
    }

    private void AnalyzeProperties()
    {
        displayBindings.Clear();
        searchFilter = "";
        hasSearched = true;
        AnimationClip targetClip = GetClipFromState(selectedState);
        if (targetClip == null) return;

        var targetBindings = GetClipBindings(targetClip);
        var targetKeys = new HashSet<string>(targetBindings.Select(GetBindingKey));

        HashSet<string> globalOtherKeys = new HashSet<string>();
        List<EditorCurveBinding> globalOtherList = new List<EditorCurveBinding>();

        foreach (var layer in selectedController.layers)
        {
            foreach (var state in GetAllStates(layer.stateMachine))
            {
                if (state == selectedState) continue;
                foreach (var clip in GetClipsFromState(state))
                {
                    if (clip == null) continue;
                    foreach (var b in GetClipBindings(clip))
                    {
                        if (globalOtherKeys.Add(GetBindingKey(b))) globalOtherList.Add(b);
                    }
                }
            }
        }

        if (isUniqueMode)
        {
            foreach (var b in targetBindings)
                if (!globalOtherKeys.Contains(GetBindingKey(b))) displayBindings.Add(b, true);
        }
        else
        {
            foreach (var b in globalOtherList)
            {
                if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.") && !targetKeys.Contains(GetBindingKey(b)))
                    displayBindings.Add(b, true);
            }
        }
    }

    private void DrawResults()
    {
        EditorGUILayout.Space();

        // Counter UI
        int totalItems = displayBindings.Count;
        int selectedItems = displayBindings.Values.Count(v => v);

        Rect rect = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(isUniqueMode ? "CLEANUP: Properties ONLY in this clip" : "SYNC: Missing Blendshapes", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"Total Items: {totalItems}  |  Selected: {selectedItems}", EditorStyles.miniLabel);
        EditorGUILayout.EndVertical();

        // Search Bar
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Filter:", GUILayout.Width(40));
        searchFilter = EditorGUILayout.TextField(searchFilter);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Select All")) SetAll(true);
        if (GUILayout.Button("Select None")) SetAll(false);
        EditorGUILayout.EndHorizontal();

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, "box", GUILayout.MaxHeight(300));

        var keys = displayBindings.Keys.ToList();

        var filtered = displayBindings
            .Where(kvp =>
            {
                if (string.IsNullOrEmpty(searchFilter))
                    return true;

                string label = $"{kvp.Key.path} : {kvp.Key.propertyName}".ToLower();
                return label.Contains(searchFilter.ToLower());
            });

        var grouped = filtered
            .GroupBy(kvp => kvp.Key.path)
            .OrderBy(g => g.Key)
            .ToList();

        if (!grouped.Any())
        {
            EditorGUILayout.HelpBox("No results match your search.", MessageType.Info);
        }

        foreach (var group in grouped)
        {
            string groupKey = group.Key;
            string groupName = string.IsNullOrEmpty(groupKey) ? "<Root>" : groupKey;
            int totalInGroup = group.Count();
            int selectedInGroup = group.Count(k => k.Value);

            // Initialize foldout state
            if (!foldoutStates.ContainsKey(groupKey))
                foldoutStates[groupKey] = string.IsNullOrEmpty(searchFilter) ? false : true;

            string groupLabel = $"{groupName} ({selectedInGroup}/{totalInGroup})";

            EditorGUILayout.BeginHorizontal();

            foldoutStates[groupKey] = EditorGUILayout.Foldout(
                foldoutStates[groupKey],
                groupLabel,
                true
            );

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("All", GUILayout.Width(40)))
            {
                var localGroup = group.ToList();
                pendingGroupAction = () =>
                {
                    foreach (var kvp in localGroup)
                        displayBindings[kvp.Key] = true;
                };
            }

            if (GUILayout.Button("None", GUILayout.Width(50)))
            {
                var localGroup = group.ToList();
                pendingGroupAction = () =>
                {
                    foreach (var kvp in localGroup)
                        displayBindings[kvp.Key] = false;
                };
            }

            GUILayout.EndHorizontal();

            if (!foldoutStates[groupKey])
                continue;

            EditorGUI.indentLevel++;

            // Sort inside group
            foreach (var kvp in group.OrderBy(k => k.Key.propertyName))
            {
                string label = $"{kvp.Key.propertyName}";

                displayBindings[kvp.Key] = EditorGUILayout.ToggleLeft(label, kvp.Value);
            }

            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndScrollView();

        pendingGroupAction?.Invoke();
        pendingGroupAction = null;

        EditorGUILayout.Space();

        if (isUniqueMode)
        {
            GUI.color = new Color(1f, 0.4f, 0.4f);

            if (GUILayout.Button($"Remove {selectedItems} Selected Properties", GUILayout.Height(30)))
            {
                RemoveProperties();
            }
        }
        else
        {
            GUI.color = new Color(0.4f, 1f, 0.4f);

            if (GUILayout.Button($"Sync {selectedItems} Selected Blendshapes", GUILayout.Height(30)))
            {
                AddProperties();
            }
        }

        GUI.color = Color.white;
    }

    private void RemoveProperties()
    {
        AnimationClip clip = GetClipFromState(selectedState);
        Undo.RecordObject(clip, "AF WD Cleanup: Remove");

        foreach (var kvp in displayBindings.Where(k => k.Value))
        {
            AnimationUtility.SetEditorCurve(clip, kvp.Key, null);
            AnimationUtility.SetObjectReferenceCurve(clip, kvp.Key, null);
        }

        FinishAction(clip);
    }

    private void AddProperties()
    {
        AnimationClip clip = GetClipFromState(selectedState);
        Undo.RecordObject(clip, "AF WD Sync: Add Blendshapes");

        float[] existingTimes = AnimationUtility.GetCurveBindings(clip)
            .Select(b => AnimationUtility.GetEditorCurve(clip, b))
            .Where(c => c != null && c.keys.Length > 0)
            .SelectMany(c => c.keys.Select(k => k.time))
            .Distinct()
            .OrderBy(t => t)
            .ToArray();

        if (existingTimes.Length == 0) existingTimes = new float[] { 0f };

        foreach (var kvp in displayBindings.Where(k => k.Value))
        {
            float val = 0;
            Transform t = avatarDescriptor.transform.Find(kvp.Key.path);
            if (t != null && t.TryGetComponent<SkinnedMeshRenderer>(out var smr))
            {
                int idx = smr.sharedMesh.GetBlendShapeIndex(kvp.Key.propertyName.Replace("blendShape.", ""));
                if (idx != -1) val = smr.GetBlendShapeWeight(idx);
            }

            AnimationCurve newCurve = new AnimationCurve();
            foreach (float time in existingTimes)
                newCurve.AddKey(new Keyframe(time, val, 0, 0));

            AnimationUtility.SetEditorCurve(clip, kvp.Key, newCurve);
        }

        FinishAction(clip);
    }

    private void FinishAction(AnimationClip clip)
    {
        EditorUtility.SetDirty(clip);
        AssetDatabase.SaveAssets();
        AnalyzeProperties();
    }

    private void ResetState()
    {
        displayBindings.Clear();
        hasSearched = false;
        searchFilter = "";
    }

    private void SetAll(bool val)
    {
        List<EditorCurveBinding> keys = displayBindings.Keys.ToList();

        foreach (var key in keys)
        {
            displayBindings[key] = val;
        }
    }

    private List<EditorCurveBinding> GetClipBindings(AnimationClip clip)
    {
        List<EditorCurveBinding> bindings = AnimationUtility.GetCurveBindings(clip).ToList();
        bindings.AddRange(AnimationUtility.GetObjectReferenceCurveBindings(clip));

        return bindings;
    }

    private string GetBindingKey(EditorCurveBinding binding)
    {
        return $"{binding.path}:{binding.type}:{binding.propertyName}";
    }

    private List<AnimatorState> GetAllStates(AnimatorStateMachine stateMachine)
    {
        List<AnimatorState> states = stateMachine.states.Select(x => x.state).ToList();

        foreach (var sub in stateMachine.stateMachines)
        {
            states.AddRange(GetAllStates(sub.stateMachine));
        }

        return states;
    }

    private AnimationClip GetClipFromState(AnimatorState state)
    {
        if (state?.motion is AnimationClip clip)
        {
            return clip;
        }
        else if (state?.motion is BlendTree tree)
        {
            return GetClipsFromBlendTree(tree).FirstOrDefault();
        }
        
        return null;
    }

    private List<AnimationClip> GetClipsFromState(AnimatorState state)
    {
        List<AnimationClip> clipList = new List<AnimationClip>();

        if (state?.motion is AnimationClip clip)
        {
            clipList.Add(clip);
        }
        else if (state?.motion is BlendTree tree)
        {
            clipList.AddRange(GetClipsFromBlendTree(tree));
        }

        return clipList;
    }

    private List<AnimationClip> GetClipsFromBlendTree(BlendTree tree)
    {
        List<AnimationClip> clipList = new List<AnimationClip>();

        if (tree == null)
        {
            return clipList;
        }
        
        foreach (var child in tree.children)
        {
            if (child.motion is AnimationClip clip)
            {
                clipList.Add(clip);
            }
            else if (child.motion is BlendTree subtree)
            {
                clipList.AddRange(GetClipsFromBlendTree(subtree));
            }
        }
        
        return clipList;
    }
}

#endif