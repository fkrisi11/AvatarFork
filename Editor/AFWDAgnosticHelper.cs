#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Reflection;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

public struct AnimProperty : IEquatable<AnimProperty>, IComparable<AnimProperty>
{
    public EditorCurveBinding binding;
    public bool isFloat;
    public string path;
    public string displayString;

    private const int HashSeed = 17;
    private const int HashMultiplier = 31;

    public bool Equals(AnimProperty other)
    {
        return binding.path == other.binding.path &&
        binding.type == other.binding.type &&
        binding.propertyName == other.binding.propertyName;
    }

    public override int GetHashCode()
    {
        int hash = HashSeed;

        hash = hash * HashMultiplier + (binding.path?.GetHashCode() ?? 0);
        hash = hash * HashMultiplier + (binding.type?.GetHashCode() ?? 0);
        hash = hash * HashMultiplier + (binding.propertyName?.GetHashCode() ?? 0);

        return hash;
    }

    public int CompareTo(AnimProperty other)
    {
        return string.Compare(displayString, other.displayString, StringComparison.Ordinal);
    }
}

public class AnimPropertyGroup : IComparable<AnimPropertyGroup>
{
    public string basePath;
    public string basePropertyName;
    public string displayString;
    public List<AnimProperty> properties = new List<AnimProperty>();

    public int CompareTo(AnimPropertyGroup other)
    {
        return string.Compare(displayString, other.displayString, StringComparison.Ordinal);
    }
}

public enum WDStatus { Off, On, Mixed, Empty }

public class AFWDAgnosticHelper : EditorWindow
{
    public const string Name = "WD Agnostic Helper";

    private VRCAvatarDescriptor avatarDescriptor;
    private VRCAvatarDescriptor.AnimLayerType selectedLayerType = VRCAvatarDescriptor.AnimLayerType.FX;

    private AnimatorController currentController;
    private List<LayerReport> layerReports = new List<LayerReport>();
    private List<MultiStateReport> multiStateReports = new List<MultiStateReport>();
    private Vector2 scrollPosition;

    private string globalSearchQuery = "";
    private bool groupTransformProperties = true;
    private HashSet<string> expandedMultiGroups = new HashSet<string>();

    private int selectedTab = 0;
    private readonly string[] tabNames = { "Layer Analysis", "Animated on Multiple Layers" };

    private GUIStyle errorLabelStyle;
    private GUIStyle warningLinkStyle;
    private GUIStyle normalLinkStyle;
    private GUIStyle valueLabelStyle;
    private GUIStyle richFoldoutStyle;
    private GUIStyle oddRowStyle;
    private GUIStyle evenRowStyle;

    private GUIStyle wdOnStyle;
    private GUIStyle wdMixedStyle;

    [MenuItem("TohruTheDragon/Avatar Fork/" + Name)]
    public static void ShowWindow()
    {
        GetWindow<AFWDAgnosticHelper>($"AF {Name}");
    }

    private void OnEnable()
    {
        wantsMouseMove = true;
        Undo.undoRedoPerformed += OnUndoRedoPerformed;
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
    }

    private void OnUndoRedoPerformed()
    {
        if (avatarDescriptor != null)
        {
            AnalyzeAvatarLayer();
            Repaint();
        }
    }

    private void OnGUI()
    {
        if (Event.current.type == EventType.MouseMove) Repaint();

        InitializeStyles();

        EditorGUILayout.Space();
        avatarDescriptor = (VRCAvatarDescriptor)EditorGUILayout.ObjectField("Avatar Descriptor", avatarDescriptor, typeof(VRCAvatarDescriptor), true);

        if (avatarDescriptor == null)
        {
            EditorGUILayout.HelpBox("Please assign a VRCAvatarDescriptor to begin.", MessageType.Info);
            return;
        }

        EditorGUILayout.Space();
        selectedLayerType = (VRCAvatarDescriptor.AnimLayerType)EditorGUILayout.EnumPopup("Playable Layer", selectedLayerType);

        if (GUILayout.Button("Analyze Selected Layer", GUILayout.Height(30)))
        {
            AnalyzeAvatarLayer();
        }

        EditorGUILayout.Space();

        if (layerReports.Count > 0)
        {
            EditorGUILayout.BeginHorizontal();
            globalSearchQuery = EditorGUILayout.TextField("Search", globalSearchQuery);
            groupTransformProperties = EditorGUILayout.ToggleLeft("Group X/Y/Z/W", groupTransformProperties, GUILayout.Width(130));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();

            selectedTab = GUILayout.Toolbar(selectedTab, tabNames, GUILayout.Height(25));
            EditorGUILayout.Space();
        }

        if (layerReports.Count > 0)
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            if (selectedTab == 0)
            {
                EditorGUILayout.HelpBox("Right click properties to see their priorities.", MessageType.Info);
                DrawLayerReportsTab();
            }
            else
            {
                EditorGUILayout.HelpBox("Right click properties to see their priorities.", MessageType.Info);
                DrawMultiStateTab();
            }

            EditorGUILayout.EndScrollView();
        }
    }

    private void InitializeStyles()
    {
        if (errorLabelStyle == null)
        {
            errorLabelStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(1f, 0.3f, 0.3f) }, fontStyle = FontStyle.Bold };
            normalLinkStyle = new GUIStyle(EditorStyles.linkLabel) { alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
            warningLinkStyle = new GUIStyle(EditorStyles.linkLabel) { normal = { textColor = new Color(1f, 0.8f, 0.2f) }, alignment = TextAnchor.MiddleLeft };

            valueLabelStyle = new GUIStyle(EditorStyles.label)
            {
                clipping = TextClipping.Clip,
                wordWrap = false,
                alignment = TextAnchor.MiddleRight
            };

            richFoldoutStyle = new GUIStyle(EditorStyles.foldoutHeader) { richText = true };

            evenRowStyle = new GUIStyle();
            evenRowStyle.padding = new RectOffset(2, 2, 2, 2);

            oddRowStyle = new GUIStyle();
            oddRowStyle.padding = new RectOffset(2, 2, 2, 2);
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 0.1f));
            tex.Apply();
            oddRowStyle.normal.background = tex;

            wdOnStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
            wdOnStyle.normal.textColor = Color.white;

            wdMixedStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
            wdMixedStyle.normal.textColor = Color.red;
        }
    }

    private GUIStyle GetRowStyle(int index)
    {
        return index % 2 == 1 ? oddRowStyle : evenRowStyle;
    }

    private void AnalyzeAvatarLayer()
    {
        List<LayerReport> oldLayerReports = new List<LayerReport>(layerReports);

        layerReports.Clear();
        multiStateReports.Clear();
        currentController = GetAnimatorController(selectedLayerType);

        if (currentController == null)
        {
            Debug.LogWarning($"{Name}: No custom Animator Controller found for the {selectedLayerType} layer.");
            return;
        }

        for (int i = 0; i < currentController.layers.Length; i++)
        {
            layerReports.Add(AnalyzeAnimatorLayer(currentController.layers[i], i));
        }

        Dictionary<AnimProperty, MultiStateReport> propDict = new Dictionary<AnimProperty, MultiStateReport>();

        foreach (LayerReport layer in layerReports)
        {
            foreach (StateReport state in layer.states)
            {
                foreach (AnimProperty prop in state.animatedProperties)
                {
                    if (!propDict.ContainsKey(prop))
                    {
                        propDict[prop] = new MultiStateReport { property = prop };
                    }

                    propDict[prop].states.Add(state);
                }
            }
        }

        multiStateReports = propDict.Values.Where(r =>
        {
            int uniqueLayers = r.states.Select(s => s.layerObject).Distinct().Count();

            if (uniqueLayers > 1)
            {
                return true;
            }

            if (r.states.Any(s => s.multiAnimatedInState.Contains(r.property)))
            {
                return true;
            }

            return false;
        }).ToList();

        multiStateReports.Sort((a, b) => a.property.CompareTo(b.property));

        foreach (LayerReport nLayer in layerReports)
        {
            LayerReport oLayer = oldLayerReports.FirstOrDefault(l => l.layerName == nLayer.layerName);

            if (oLayer != null)
            {
                nLayer.isExpanded = oLayer.isExpanded;
                nLayer.isAccumulatedExpanded = oLayer.isAccumulatedExpanded;
                nLayer.layerSearchQuery = oLayer.layerSearchQuery;

                foreach (StateReport nState in nLayer.states)
                {
                    StateReport oState = oLayer.states.FirstOrDefault(s => s.statePath == nState.statePath);

                    if (oState != null)
                    {
                        nState.isMissingExpanded = oState.isMissingExpanded;
                    }
                }
            }
        }
    }

    private AnimatorController GetAnimatorController(VRCAvatarDescriptor.AnimLayerType layerType)
    {
        if (avatarDescriptor.baseAnimationLayers != null)
        {
            foreach (VRCAvatarDescriptor.CustomAnimLayer layer in avatarDescriptor.baseAnimationLayers)
            {
                if (layer.type == layerType && !layer.isDefault && layer.animatorController != null)
                {
                    return layer.animatorController as AnimatorController;
                }
            }
        }

        if (avatarDescriptor.specialAnimationLayers != null)
        {
            foreach (VRCAvatarDescriptor.CustomAnimLayer layer in avatarDescriptor.specialAnimationLayers)
            {
                if (layer.type == layerType && !layer.isDefault && layer.animatorController != null)
                {
                    return layer.animatorController as AnimatorController;
                }
            }
        }

        return null;
    }

    private LayerReport AnalyzeAnimatorLayer(AnimatorControllerLayer layer, int layerIndex)
    {
        LayerReport report = new LayerReport { layerName = layer.name, layerObject = layer, layerIndex = layerIndex };

        report.mask = layer.avatarMask;

        TraverseStateMachine(layer.stateMachine, layer.stateMachine.name, report.states, layer, layerIndex);

        if (report.states.Count == 0)
        {
            report.wdStatus = WDStatus.Empty;
        }
        else
        {
            bool hasWDOn = report.states.Any(s => s.writeDefaults);
            bool hasWDOff = report.states.Any(s => !s.writeDefaults);

            if (hasWDOn && hasWDOff)
            {
                report.wdStatus = WDStatus.Mixed;
            }
            else if (hasWDOn)
            {
                report.wdStatus = WDStatus.On;
            }
            else
            {
                report.wdStatus = WDStatus.Off;
            }
        }

        foreach (StateReport state in report.states)
        {
            report.allAccumulatedProperties.UnionWith(state.animatedProperties);
        }

        foreach (StateReport state in report.states)
        {
            state.missingProperties = new HashSet<AnimProperty>(report.allAccumulatedProperties);
            state.missingProperties.ExceptWith(state.animatedProperties);
        }

        return report;
    }

    private void TraverseStateMachine(AnimatorStateMachine sm, string currentPath, List<StateReport> states, AnimatorControllerLayer layer, int layerIndex)
    {
        foreach (ChildAnimatorState childState in sm.states)
        {
            StateReport stateReport = new StateReport
            {
                statePath = $"{currentPath}/{childState.state.name}",
                stateObject = childState.state,
                layerObject = layer,
                layerIndex = layerIndex,
                motion = childState.state.motion,
                writeDefaults = childState.state.writeDefaultValues
            };

            if (childState.state.motion == null)
            {
                stateReport.hasNoAnimation = true;
            }
            else
            {
                AnalyzeMotion(childState.state.motion, stateReport);
            }

            states.Add(stateReport);
        }

        foreach (ChildAnimatorStateMachine childSM in sm.stateMachines)
        {
            string nextPath = $"{currentPath}/{childSM.stateMachine.name}";
            TraverseStateMachine(childSM.stateMachine, nextPath, states, layer, layerIndex);
        }
    }

    private void AnalyzeMotion(Motion motion, StateReport stateReport)
    {
        if (motion == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(motion.name))
        {
            stateReport.motionNames.Add(motion.name);
        }

        if (motion is AnimationClip clip)
        {
            ExtractPropertiesFromClip(clip, stateReport.animatedProperties, stateReport.multiAnimatedInState);
        }
        else if (motion is BlendTree blendTree)
        {
            foreach (ChildMotion child in blendTree.children)
            {
                AnalyzeMotion(child.motion, stateReport);
            }
        }
    }

    private void ExtractPropertiesFromClip(AnimationClip clip, HashSet<AnimProperty> properties, HashSet<AnimProperty> multiProperties)
    {
        EditorCurveBinding[] floatBindings = AnimationUtility.GetCurveBindings(clip);

        foreach (EditorCurveBinding binding in floatBindings)
        {
            AnimProperty prop = CreateAnimProperty(binding, true);

            if (!properties.Add(prop))
            {
                multiProperties.Add(prop);
            }
        }

        EditorCurveBinding[] objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
        foreach (EditorCurveBinding binding in objBindings)
        {
            AnimProperty prop = CreateAnimProperty(binding, false);

            if (!properties.Add(prop))
            {
                multiProperties.Add(prop);
            }
        }
    }

    private AnimProperty CreateAnimProperty(EditorCurveBinding binding, bool isFloat)
    {
        string p = string.IsNullOrEmpty(binding.path) ? "" : binding.path;
        string displayPath = string.IsNullOrEmpty(binding.path) ? "Root" : binding.path;

        return new AnimProperty
        {
            binding = binding,
            isFloat = isFloat,
            path = p,
            displayString = $"[{binding.type.Name}] {displayPath} : {binding.propertyName}"
        };
    }

    // Grouping Logic
    private bool IsVectorComponent(string pName)
    {
        return pName.EndsWith(".x") || pName.EndsWith(".y") || pName.EndsWith(".z") || pName.EndsWith(".w");
    }

    private string GetPropertyComponent(string pName)
    {
        if (pName.EndsWith(".x"))
        {
            return "x";
        }

        if (pName.EndsWith(".y"))
        {
            return "y";
        }

        if (pName.EndsWith(".z"))
        {
            return "z";
        }

        if (pName.EndsWith(".w"))
        {
            return "w";
        }

        return "";
    }

    private List<AnimPropertyGroup> GroupProperties(IEnumerable<AnimProperty> props)
    {
        Dictionary<string, AnimPropertyGroup> groups = new Dictionary<string, AnimPropertyGroup>();

        foreach (AnimProperty prop in props)
        {
            string pName = prop.binding.propertyName;
            bool isVector = groupTransformProperties && IsVectorComponent(pName);

            string baseName = isVector ? pName.Substring(0, pName.Length - 2) : pName;
            string key = $"{prop.binding.path}::{prop.binding.type.Name}::{baseName}";

            if (!groups.ContainsKey(key))
            {
                groups[key] = new AnimPropertyGroup { basePath = prop.path, basePropertyName = baseName };
            }

            groups[key].properties.Add(prop);
        }

        List<AnimPropertyGroup> result = new List<AnimPropertyGroup>();

        foreach (AnimPropertyGroup g in groups.Values)
        {
            g.properties.Sort();

            if (g.properties.Count == 1 && !IsVectorComponent(g.properties[0].binding.propertyName))
            {
                g.displayString = g.properties[0].displayString;
            }
            else
            {
                AnimProperty first = g.properties[0];
                string displayPath = string.IsNullOrEmpty(first.path) ? "Root" : first.path;
                IOrderedEnumerable<string> comps = g.properties.Select(p => GetPropertyComponent(p.binding.propertyName)).OrderBy(c => c);
                string compStr = string.Join(", ", comps);
                g.displayString = $"[{first.binding.type.Name}] {displayPath} : {g.basePropertyName} ({compStr})";
            }

            result.Add(g);
        }

        result.Sort();

        return result;
    }

    // GUI Drawing
    private void DrawLayerReportsTab()
    {
        bool isSearchingGlobally = !string.IsNullOrEmpty(globalSearchQuery);

        foreach (LayerReport layer in layerReports)
        {
            bool layerNameMatch = false;

            if (isSearchingGlobally)
            {
                layerNameMatch = layer.layerName.IndexOf(globalSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                bool layerHasMatches = layerNameMatch ||
                                       layer.allAccumulatedProperties.Any(p => p.displayString.IndexOf(globalSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                       layer.states.Any(s => StateMatchesText(s, globalSearchQuery));

                if (!layerHasMatches)
                {
                    continue;
                }
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            int missingProps = layer.states.Sum(s => s.missingProperties.Count);
            int noMotionCount = layer.states.Count(s => s.hasNoAnimation);
            string noMotionStr = noMotionCount > 0 ? $", <color=#FF4D4D><b>{noMotionCount} No Motion</b></color>" : "";

            string headerTitle = $"[{layer.layerIndex}] {layer.layerName} ({layer.allAccumulatedProperties.Count} Props, {missingProps} Missing, {layer.states.Count} States{noMotionStr})";

            Rect headerRect = EditorGUILayout.GetControlRect();
            float currentRight = headerRect.xMax;

            // Measure icon widths
            bool showWD = layer.wdStatus == WDStatus.On || layer.wdStatus == WDStatus.Mixed;
            string wdText = showWD ? (layer.wdStatus == WDStatus.Mixed ? "Mixed WD" : "WD") : "";
            GUIStyle targetWdStyle = showWD ? (layer.wdStatus == WDStatus.Mixed ? wdMixedStyle : wdOnStyle) : null;
            float wdWidth = showWD ? targetWdStyle.CalcSize(new GUIContent(wdText)).x : 0f;

            bool showMask = layer.mask != null;
            Texture maskIcon = showMask ? EditorGUIUtility.ObjectContent(null, typeof(AvatarMask)).image : null;
            float maskWidth = (showMask && maskIcon != null) ? 20f : 0f;

            float padding = (showWD && showMask) ? 5f : 0f;
            float totalExtraWidth = wdWidth + padding + maskWidth;

            // Shrink Foldout Rect to prevent overlapping click zones
            Rect foldoutRect = new Rect(headerRect.x, headerRect.y, headerRect.width - totalExtraWidth - 5f, headerRect.height);
            layer.isExpanded = EditorGUI.Foldout(foldoutRect, layer.isExpanded, headerTitle, true, richFoldoutStyle);

            // Draw WD Text
            if (showWD)
            {
                currentRight -= wdWidth;
                Rect wdRect = new Rect(currentRight, headerRect.y, wdWidth, headerRect.height);
                EditorGUI.LabelField(wdRect, wdText, targetWdStyle);
            }

            // Draw Mask Icon (Before WD)
            if (showMask && maskIcon != null)
            {
                if (showWD)
                {
                    currentRight -= padding;
                }

                currentRight -= maskWidth;
                Rect maskRect = new Rect(currentRight, headerRect.y, maskWidth, headerRect.height);
                GUIContent maskContent = new GUIContent(maskIcon, layer.mask.name);

                if (Event.current.type == EventType.MouseDown && maskRect.Contains(Event.current.mousePosition))
                {
                    Selection.activeObject = layer.mask;
                    EditorGUIUtility.PingObject(layer.mask);
                    Event.current.Use();
                }

                GUI.Label(maskRect, maskContent);
            }

            if (layer.isExpanded)
            {
                EditorGUI.indentLevel++;
                layer.layerSearchQuery = EditorGUILayout.TextField("Layer Property Search", layer.layerSearchQuery);
                EditorGUILayout.Space();

                bool hasLayerSearch = !string.IsNullOrEmpty(layer.layerSearchQuery);
                bool isSearching = isSearchingGlobally || hasLayerSearch;

                layer.isAccumulatedExpanded = EditorGUILayout.Foldout(layer.isAccumulatedExpanded, "All Accumulated Properties");

                if (layer.isAccumulatedExpanded)
                {
                    EditorGUI.indentLevel++;
                    List<AnimProperty> matchingAccumulated = layer.allAccumulatedProperties.Where(p => MatchesSearch(p.displayString, globalSearchQuery, layer.layerSearchQuery, layerNameMatch)).ToList();
                    List<AnimPropertyGroup> accGroups = GroupProperties(matchingAccumulated);

                    if (accGroups.Count == 0)
                    {
                        EditorGUILayout.LabelField(isSearching ? "No matching properties found." : "No animated properties found.", EditorStyles.miniLabel);
                    }
                    else
                    {
                        for (int i = 0; i < accGroups.Count; i++) DrawClickablePropertyGroup(accGroups[i], false, i);
                    }

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("States Analysis", EditorStyles.boldLabel);

                foreach (StateReport state in layer.states)
                {
                    bool stateNameMatch = isSearchingGlobally && (layerNameMatch || StateMatchesText(state, globalSearchQuery));
                    List<AnimProperty> matchingPresent = state.animatedProperties.Where(p => MatchesSearch(p.displayString, globalSearchQuery, layer.layerSearchQuery, stateNameMatch)).ToList();
                    List<AnimProperty> matchingMissing = state.missingProperties.Where(p => MatchesSearch(p.displayString, globalSearchQuery, layer.layerSearchQuery, stateNameMatch)).ToList();

                    List<AnimPropertyGroup> presentGroups = GroupProperties(matchingPresent);
                    List<AnimPropertyGroup> missingGroups = GroupProperties(matchingMissing);

                    if (isSearching)
                    {
                        bool hasPropertyMatches = presentGroups.Count > 0 || missingGroups.Count > 0;

                        if (hasLayerSearch && !hasPropertyMatches)
                        {
                            continue;
                        }

                        if (isSearchingGlobally && !hasLayerSearch && !stateNameMatch && !hasPropertyMatches)
                        {
                            continue;
                        }
                    }

                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField(state.statePath, EditorStyles.boldLabel);
                    EditorGUILayout.BeginHorizontal();

                    GUIContent btnContent = state.motion != null ? EditorGUIUtility.ObjectContent(state.motion, typeof(Motion)) : new GUIContent("None (Motion)");

                    if (GUILayout.Button(btnContent, EditorStyles.objectField, GUILayout.Height(20)))
                    {
                        if (state.motion != null)
                        {
                            Selection.activeObject = state.motion;
                            EditorGUIUtility.PingObject(state.motion);
                            EditorApplication.delayCall += () => EditorApplication.ExecuteMenuItem("Window/Animation/Animation");
                        }
                    }

                    if (GUILayout.Button("Focus", GUILayout.Width(130)))
                    {
                        ViewState(currentController, layer.layerObject, state.stateObject);
                    }

                    EditorGUILayout.EndHorizontal();

                    if (state.hasNoAnimation)
                    {
                        EditorGUILayout.LabelField("-> NO ANIMATION (Cannot assign properties without a clip)", errorLabelStyle);
                    }

                    if (isSearching && presentGroups.Count > 0)
                    {
                        EditorGUILayout.LabelField("Matching Present Properties:", EditorStyles.miniBoldLabel);
                        EditorGUI.indentLevel++;

                        for (int i = 0; i < presentGroups.Count; i++)
                        {
                            DrawClickablePropertyGroup(presentGroups[i], false, i);
                        }

                        EditorGUI.indentLevel--;
                    }

                    if (missingGroups.Count > 0)
                    {
                        EditorGUILayout.BeginHorizontal();
                        string foldoutTitle = isSearching ? $"Matching Missing Properties ({matchingMissing.Count})" : $"Missing Properties ({state.missingProperties.Count})";
                        state.isMissingExpanded = EditorGUILayout.Foldout(state.isMissingExpanded, foldoutTitle);

                        EditorGUI.BeginDisabledGroup(state.motion == null);

                        if (GUILayout.Button(isSearching ? "Add Matching Missing" : "Add All Missing", GUILayout.Width(150)))
                        {
                            StateReport stateRef = state;
                            List<AnimProperty> propsToAdd = matchingMissing;
                            EditorApplication.delayCall += () => AddPropertiesToState(stateRef, propsToAdd);
                        }

                        EditorGUI.EndDisabledGroup();
                        EditorGUILayout.EndHorizontal();

                        if (state.isMissingExpanded)
                        {
                            EditorGUI.indentLevel++;

                            for (int i = 0; i < missingGroups.Count; i++)
                            {
                                DrawMissingPropertyGroup(state, missingGroups[i], i);
                            }

                            EditorGUI.indentLevel--;
                        }
                    }
                    else if (!state.hasNoAnimation && !isSearching)
                    {
                        EditorGUILayout.LabelField("All layer properties are present.", normalLinkStyle);
                    }

                    EditorGUILayout.EndVertical();
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }
    }

    private class MultiStateGroup
    {
        public string displayString;
        public AnimProperty baseProperty;
        public List<StateReport> uniqueStates = new List<StateReport>();
    }

    private void DrawMultiStateTab()
    {
        bool isSearchingGlobally = !string.IsNullOrEmpty(globalSearchQuery);
        bool hasVisibleItems = false;

        Dictionary<string, MultiStateGroup> groupedMulti = new Dictionary<string, MultiStateGroup>();

        foreach (MultiStateReport report in multiStateReports)
        {
            string pName = report.property.binding.propertyName;
            bool isVector = groupTransformProperties && IsVectorComponent(pName);
            string baseName = isVector ? pName.Substring(0, pName.Length - 2) : pName;
            string key = $"{report.property.binding.path}::{report.property.binding.type.Name}::{baseName}";

            if (!groupedMulti.ContainsKey(key))
            {
                groupedMulti[key] = new MultiStateGroup { baseProperty = report.property };
            }

            foreach (StateReport s in report.states)
            {
                if (!groupedMulti[key].uniqueStates.Any(us => us.stateObject == s.stateObject && us.layerObject == s.layerObject))
                {
                    groupedMulti[key].uniqueStates.Add(s);
                }
            }
        }

        List<MultiStateGroup> groupsList = groupedMulti.Values.ToList();
        foreach (MultiStateGroup g in groupsList)
        {
            AnimProperty first = g.baseProperty;
            List<MultiStateReport> matchedReports = multiStateReports.Where(r =>
                r.property.binding.path == first.binding.path &&
                r.property.binding.type == first.binding.type &&
                (r.property.binding.propertyName == first.binding.propertyName ||
                (groupTransformProperties && IsVectorComponent(r.property.binding.propertyName) && r.property.binding.propertyName.StartsWith(first.binding.propertyName.Substring(0, first.binding.propertyName.Length - 2))))
            ).ToList();

            if (matchedReports.Count == 1 && !IsVectorComponent(first.binding.propertyName))
            {
                g.displayString = first.displayString;
            }
            else
            {
                string displayPath = string.IsNullOrEmpty(first.path) ? "Root" : first.path;
                string bName = (groupTransformProperties && IsVectorComponent(first.binding.propertyName)) ? first.binding.propertyName.Substring(0, first.binding.propertyName.Length - 2) : first.binding.propertyName;
                IOrderedEnumerable<string> comps = matchedReports.Select(r => GetPropertyComponent(r.property.binding.propertyName)).OrderBy(c => c);
                g.displayString = $"[{first.binding.type.Name}] {displayPath} : {bName} ({string.Join(", ", comps)})";
            }
        }

        groupsList.Sort((a, b) => string.Compare(a.displayString, b.displayString, StringComparison.Ordinal));

        int groupRowIndex = 0;

        foreach (MultiStateGroup group in groupsList)
        {
            bool propMatches = false;

            if (isSearchingGlobally)
            {
                propMatches = group.displayString.IndexOf(globalSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                bool stateMatches = group.uniqueStates.Any(s => StateMatchesText(s, globalSearchQuery));

                if (!propMatches && !stateMatches)
                {
                    continue;
                }
            }

            hasVisibleItems = true;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal(GetRowStyle(groupRowIndex++));

            Rect foldoutRect = GUILayoutUtility.GetRect(15f, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(false));
            bool isExpanded = expandedMultiGroups.Contains(group.displayString);
            bool newExpanded = EditorGUI.Foldout(foldoutRect, isExpanded, "");

            if (newExpanded != isExpanded)
            {
                if (newExpanded)
                {
                    expandedMultiGroups.Add(group.displayString);
                }
                else
                {
                    expandedMultiGroups.Remove(group.displayString);
                }
            }

            GUIContent content = new GUIContent(group.displayString);
            Rect rect = GUILayoutUtility.GetRect(content, normalLinkStyle, GUILayout.ExpandWidth(false));

            if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && rect.Contains(Event.current.mousePosition))
            {
                PropertyPriorityWindow.ShowWindow(group.baseProperty, currentController, avatarDescriptor.transform);
                Event.current.Use();
            }

            if (GUI.Button(rect, content, normalLinkStyle))
            {
                PingProperty(group.baseProperty);
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (newExpanded)
            {
                EditorGUI.indentLevel++;

                List<StateReport> sortedStates = group.uniqueStates.OrderByDescending(s => s.layerIndex).ToList();
                List<int> uniqueLayerIndices = sortedStates.Select(s => s.layerIndex).Distinct().ToList();

                int innerRowIndex = 0;

                foreach (StateReport state in sortedStates)
                {
                    if (isSearchingGlobally && !propMatches && !StateMatchesText(state, globalSearchQuery))
                    {
                        continue;
                    }

                    int rank = uniqueLayerIndices.IndexOf(state.layerIndex);

                    EditorGUILayout.BeginHorizontal(GetRowStyle(innerRowIndex++));

                    EditorGUILayout.LabelField($"#{rank}", EditorStyles.boldLabel, GUILayout.Width(45));
                    EditorGUILayout.LabelField($"[{state.layerObject.name}] {state.statePath}");

                    GUIContent btnContent = state.motion != null ? EditorGUIUtility.ObjectContent(state.motion, typeof(Motion)) : new GUIContent("None (Motion)");

                    if (GUILayout.Button(btnContent, EditorStyles.objectField, GUILayout.Height(18), GUILayout.Width(160)))
                    {
                        if (state.motion != null)
                        {
                            Selection.activeObject = state.motion;
                            EditorGUIUtility.PingObject(state.motion);
                            EditorApplication.delayCall += () => EditorApplication.ExecuteMenuItem("Window/Animation/Animation");
                        }
                    }

                    if (GUILayout.Button("Focus", EditorStyles.miniButton, GUILayout.Width(60)))
                    {
                        ViewState(currentController, state.layerObject, state.stateObject);
                    }

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        if (!hasVisibleItems)
        {
            EditorGUILayout.HelpBox(isSearchingGlobally ? "No properties match your search." : "No properties are animated simultaneously in a way that overrides (multiple layers or internal blend trees).", MessageType.Info);
        }
    }

    private bool StateMatchesText(StateReport state, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        if (state.statePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (state.motionNames.Any(m => m.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }

        if (state.motion == null && "none (motion)".IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private bool MatchesSearch(string propText, string globalSearch, string layerSearch, bool bypassGlobal)
    {
        bool matches = true;

        if (!string.IsNullOrEmpty(globalSearch) && !bypassGlobal)
        {
            matches &= propText.IndexOf(globalSearch, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        if (!string.IsNullOrEmpty(layerSearch))
        {
            matches &= propText.IndexOf(layerSearch, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        return matches;
    }

    private void DrawClickablePropertyGroup(AnimPropertyGroup group, bool isMissing, int rowIndex)
    {
        EditorGUILayout.BeginHorizontal(GetRowStyle(rowIndex));

        GUIStyle styleToUse = isMissing ? warningLinkStyle : normalLinkStyle;
        GUIContent content = new GUIContent(group.displayString);

        Rect rect = GUILayoutUtility.GetRect(content, styleToUse, GUILayout.ExpandWidth(false));

        if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && rect.Contains(Event.current.mousePosition))
        {
            PropertyPriorityWindow.ShowWindow(group.properties[0], currentController, avatarDescriptor.transform);
            Event.current.Use();
        }

        if (GUI.Button(rect, content, styleToUse))
        {
            PingProperty(group.properties[0]);
        }

        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    private void DrawMissingPropertyGroup(StateReport state, AnimPropertyGroup group, int rowIndex)
    {
        EditorGUILayout.BeginHorizontal(GetRowStyle(rowIndex));

        GUIContent content = new GUIContent(group.displayString);
        Rect rect = GUILayoutUtility.GetRect(content, warningLinkStyle, GUILayout.ExpandWidth(false));

        if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && rect.Contains(Event.current.mousePosition))
        {
            PropertyPriorityWindow.ShowWindow(group.properties[0], currentController, avatarDescriptor.transform);
            Event.current.Use();
        }

        if (GUI.Button(rect, content, warningLinkStyle))
        {
            PingProperty(group.properties[0]);
        }

        GUILayout.FlexibleSpace();

        string valStr = "0";

        if (group.properties[0].isFloat)
        {
            if (avatarDescriptor != null)
            {
                List<string> vals = new List<string>();

                foreach (AnimProperty p in group.properties)
                {
                    if (AnimationUtility.GetFloatValue(avatarDescriptor.gameObject, p.binding, out float v))
                    {
                        vals.Add(v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        vals.Add("0");
                    }
                }

                valStr = vals.Count > 1 ? $"({string.Join(", ", vals)})" : vals[0];
            }
        }
        else
        {
            valStr = "None";

            if (avatarDescriptor != null && AnimationUtility.GetObjectReferenceValue(avatarDescriptor.gameObject, group.properties[0].binding, out Object o))
            {
                valStr = o != null ? o.name : "None";
            }
        }

        GUIContent valContent = new GUIContent($"Val: {valStr}", $"Current Avatar Value:\n{valStr}");
        EditorGUILayout.LabelField(valContent, valueLabelStyle, GUILayout.Width(180));

        EditorGUI.BeginDisabledGroup(state.motion == null);

        if (GUILayout.Button("+", GUILayout.Width(25)))
        {
            StateReport stateRef = state;
            AnimPropertyGroup groupRef = group;
            EditorApplication.delayCall += () => AddPropertiesToState(stateRef, groupRef.properties);
        }

        EditorGUI.EndDisabledGroup();

        EditorGUILayout.EndHorizontal();
    }

    private void PingProperty(AnimProperty prop)
    {
        if (avatarDescriptor == null)
        {
            return;
        }

        Transform targetObj = string.IsNullOrEmpty(prop.path) ? avatarDescriptor.transform : avatarDescriptor.transform.Find(prop.path);

        if (targetObj != null)
        {
            Selection.activeGameObject = targetObj.gameObject;
            EditorGUIUtility.PingObject(targetObj.gameObject);
        }
        else
        {
            Debug.LogWarning($"{Name}: Could not find animated object path on avatar: {(string.IsNullOrEmpty(prop.path) ? "Root" : prop.path)}");
        }
    }

    private void AddPropertiesToState(StateReport state, List<AnimProperty> props)
    {
        if (state.motion == null)
        {
            return;
        }

        bool modified = false;

        foreach (AnimProperty prop in props)
        {
            modified |= AddPropertyToMotion(state.motion, prop);
        }

        if (modified)
        {
            AssetDatabase.SaveAssets();
            AnalyzeAvatarLayer();
        }
    }

    private bool AddPropertyToMotion(Motion motion, AnimProperty prop)
    {
        if (motion == null)
        {
            return false;
        }

        bool modified = false;

        if (motion is AnimationClip clip)
        {
            modified |= AddPropertyToClip(clip, prop);
        }
        else if (motion is BlendTree tree)
        {
            List<AnimationClip> clips = GetClipsFromTree(tree);

            foreach (AnimationClip c in clips)
            {
                modified |= AddPropertyToClip(c, prop);
            }
        }

        return modified;
    }

    private List<AnimationClip> GetClipsFromTree(BlendTree tree)
    {
        List<AnimationClip> clips = new List<AnimationClip>();

        if (tree == null)
        {
            return clips;
        }

        foreach (ChildMotion child in tree.children)
        {
            if (child.motion is AnimationClip c)
            {
                clips.Add(c);
            }
            else if (child.motion is BlendTree b)
            {
                clips.AddRange(GetClipsFromTree(b));
            }
        }

        return clips;
    }

    private bool AddPropertyToClip(AnimationClip clip, AnimProperty prop)
    {
        if (clip == null)
        {
            return false;
        }

        Undo.RecordObject(clip, "Add Missing Animation Property");

        float floatVal = 0f;
        Object objVal = null;
        bool hasAvatar = avatarDescriptor != null;

        if (prop.isFloat)
        {
            if (hasAvatar)
            {
                AnimationUtility.GetFloatValue(avatarDescriptor.gameObject, prop.binding, out floatVal);
            }
        }
        else
        {
            if (hasAvatar)
            {
                AnimationUtility.GetObjectReferenceValue(avatarDescriptor.gameObject, prop.binding, out objVal);
            }
        }

        int maxKeys = 0;

        foreach (EditorCurveBinding b in AnimationUtility.GetCurveBindings(clip))
        {
            AnimationCurve c = AnimationUtility.GetEditorCurve(clip, b);

            if (c != null)
            {
                maxKeys = Mathf.Max(maxKeys, c.keys.Length);
            }
        }

        foreach (EditorCurveBinding b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
        {
            ObjectReferenceKeyframe[] c = AnimationUtility.GetObjectReferenceCurve(clip, b);

            if (c != null)
            {
                maxKeys = Mathf.Max(maxKeys, c.Length);
            }
        }

        if (prop.isFloat)
        {
            AnimationCurve curve = new AnimationCurve();

            if (maxKeys <= 1)
            {
                curve.AddKey(new Keyframe(0f, floatVal));
            }
            else
            {
                curve.AddKey(new Keyframe(0f, floatVal));
                curve.AddKey(new Keyframe(clip.length, floatVal));
            }

            AnimationUtility.SetEditorCurve(clip, prop.binding, curve);
        }
        else
        {
            if (maxKeys <= 1)
            {
                ObjectReferenceKeyframe[] keys = { new ObjectReferenceKeyframe { time = 0f, value = objVal } };
                AnimationUtility.SetObjectReferenceCurve(clip, prop.binding, keys);
            }
            else
            {
                ObjectReferenceKeyframe[] keys = { new ObjectReferenceKeyframe { time = 0f, value = objVal }, new ObjectReferenceKeyframe { time = clip.length, value = objVal } };
                AnimationUtility.SetObjectReferenceCurve(clip, prop.binding, keys);
            }
        }

        EditorUtility.SetDirty(clip);

        return true;
    }

    public static void ViewState(AnimatorController controller, AnimatorControllerLayer layer, AnimatorState targetState)
    {
        if (controller == null || layer == null || layer.stateMachine == null || targetState == null)
        {
            return;
        }

        List<Object> FindStateBreadcrumbs(List<Object> currentPath, AnimatorStateMachine stateMachine, AnimatorState target)
        {
            foreach (ChildAnimatorState animatorState in stateMachine.states)
            {
                if (animatorState.state == target)
                {
                    return currentPath;
                }
            }

            foreach (ChildAnimatorStateMachine child in stateMachine.stateMachines)
            {
                if (child.stateMachine == null)
                {
                    continue;
                }

                currentPath.Add(child.stateMachine);
                List<Object> found = FindStateBreadcrumbs(currentPath, child.stateMachine, target);

                if (found != null)
                {
                    return found;
                }

                currentPath.RemoveAt(currentPath.Count - 1);
            }

            return null;
        }

        List<Object> stateBreadCrumbs = FindStateBreadcrumbs(new List<Object> { layer.stateMachine }, layer.stateMachine, targetState);

        if (stateBreadCrumbs == null)
        {
            return;
        }

        Assembly graphsAssembly = Assembly.Load("UnityEditor.Graphs");
        if (graphsAssembly == null)
        {
            return;
        }

        Type actType = graphsAssembly.GetType("UnityEditor.Graphs.AnimatorControllerTool");
        Type nodeType = graphsAssembly.GetType("UnityEditor.Graphs.Node");

        if (actType == null || nodeType == null)
        {
            return;
        }

        MethodInfo hasOpenInstances = typeof(EditorWindow).GetMethod("HasOpenInstances")?.MakeGenericMethod(actType);

        if (hasOpenInstances == null)
        {
            return;
        }

        if ((bool)hasOpenInstances.Invoke(null, null))
        {
            BindingFlags BF_ALL = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            EditorWindow act = EditorWindow.GetWindow(actType, false, "Animator", false);
            act.GetType().GetProperty("animatorController", BF_ALL)?.SetValue(act, controller);

            int layerIndex = -1;
            for (int i = 0; i < controller.layers.Length; i++)
            {
                if (controller.layers[i].name == layer.name)
                {
                    layerIndex = i; break;
                }
            }

            if (layerIndex != -1)
            {
                act.GetType().GetProperty("selectedLayerIndex", BF_ALL)?.SetValue(act, layerIndex);
            }

            object gui = act.GetType().GetProperty("activeGraphGUI", BF_ALL)?.GetValue(act);

            if (gui == null)
            {
                return;
            }

            object crumbs = act.GetType().GetField("m_BreadCrumbs", BF_ALL)?.GetValue(act);
            crumbs?.GetType().GetMethod("Clear", BF_ALL)?.Invoke(crumbs, null);

            MethodInfo add_breadcrumb = act.GetType().GetMethod("AddBreadCrumb");

            if (add_breadcrumb != null)
            {
                for (int i = 0; i < stateBreadCrumbs.Count - 1; ++i)
                {
                    add_breadcrumb.Invoke(act, new object[] { stateBreadCrumbs[i], false });
                }

                add_breadcrumb.Invoke(act, new object[] { stateBreadCrumbs.Last(), true });
            }

            act.GetType().GetMethod("Repaint")?.Invoke(act, null);

            object graph = gui.GetType().GetProperty("graph", BF_ALL)?.GetValue(gui) ?? gui.GetType().GetField("graph", BF_ALL)?.GetValue(gui);

            if (graph != null)
            {
                object state_node_lookup = graph.GetType().GetField("m_StateNodeLookup", BF_ALL)?.GetValue(graph);

                if (state_node_lookup != null)
                {
                    object state_node = state_node_lookup.GetType().GetMethod("get_Item", BF_ALL)?.Invoke(state_node_lookup, new object[] { targetState });

                    if (state_node != null)
                    {
                        Type listType = typeof(List<>).MakeGenericType(nodeType);
                        object nodeList = Activator.CreateInstance(listType);
                        listType.GetMethod("Add")?.Invoke(nodeList, new object[] { state_node });
                        gui.GetType().GetProperty("selection", BF_ALL)?.SetValue(gui, nodeList);
                        gui.GetType().GetMethod("UpdateUnitySelection", BF_ALL)?.Invoke(gui, Array.Empty<object>());
                    }
                }
            }
        }
        else
        {
            Debug.LogWarning($"{Name}: Please open the Animator Window first to focus the state.");
        }
    }

    private class LayerReport
    {
        public string layerName;
        public AnimatorControllerLayer layerObject;
        public int layerIndex;
        public string layerSearchQuery = "";
        public HashSet<AnimProperty> allAccumulatedProperties = new HashSet<AnimProperty>();
        public List<StateReport> states = new List<StateReport>();
        public bool isExpanded = false;
        public bool isAccumulatedExpanded = false;
        public WDStatus wdStatus = WDStatus.Empty;
        public AvatarMask mask;
    }

    private class MultiStateReport
    {
        public AnimProperty property;
        public List<StateReport> states = new List<StateReport>();
        public bool isExpanded = false;
    }

    private class StateReport
    {
        public string statePath;
        public AnimatorState stateObject;
        public AnimatorControllerLayer layerObject;
        public int layerIndex;
        public Motion motion;
        public List<string> motionNames = new List<string>();
        public bool hasNoAnimation = false;
        public bool writeDefaults = false;
        public HashSet<AnimProperty> animatedProperties = new HashSet<AnimProperty>();
        public HashSet<AnimProperty> multiAnimatedInState = new HashSet<AnimProperty>();
        public HashSet<AnimProperty> missingProperties = new HashSet<AnimProperty>();
        public bool isMissingExpanded = false;
    }
}

// Priority Analysis Popup Window
public class PropertyPriorityWindow : EditorWindow
{
    private AnimProperty targetProperty;
    private AnimatorController controller;
    private Transform avatarRoot;
    private List<PriorityReport> reports = new List<PriorityReport>();
    private Vector2 scrollPos;

    private GUIStyle wrapStyle;
    private GUIStyle bigNumberStyle;
    private GUIStyle linkStyle;

    public static void ShowWindow(AnimProperty prop, AnimatorController ctrl, Transform root)
    {
        PropertyPriorityWindow window = GetWindow<PropertyPriorityWindow>("Property Priority");
        window.targetProperty = prop;
        window.controller = ctrl;
        window.avatarRoot = root;
        window.minSize = new Vector2(500, 350);
        window.ScanController();
        window.Show();
    }

    private void OnEnable()
    {
        wantsMouseMove = true;
    }

    private void ScanController()
    {
        reports.Clear();

        if (controller == null)
        {
            return;
        }

        for (int i = 0; i < controller.layers.Length; i++)
        {
            AnimatorControllerLayer layer = controller.layers[i];
            ScanStateMachine(layer.stateMachine, layer.stateMachine.name, layer, i);
        }
    }

    private void ScanStateMachine(AnimatorStateMachine sm, string currentPath, AnimatorControllerLayer layer, int layerIndex)
    {
        foreach (ChildAnimatorState childState in sm.states)
        {
            string statePath = $"{currentPath}/{childState.state.name}";
            ScanMotion(childState.state.motion, childState.state, statePath, layer, layerIndex, false);
        }

        foreach (ChildAnimatorStateMachine childSM in sm.stateMachines)
        {
            string nextPath = $"{currentPath}/{childSM.stateMachine.name}";
            ScanStateMachine(childSM.stateMachine, nextPath, layer, layerIndex);
        }
    }

    private void ScanMotion(Motion motion, AnimatorState state, string statePath, AnimatorControllerLayer layer, int layerIndex, bool inDirectBT)
    {
        if (motion == null)
        {
            return;
        }

        if (motion is AnimationClip clip)
        {
            if (ClipHasProperty(clip, targetProperty))
            {
                reports.Add(new PriorityReport
                {
                    layerIndex = layerIndex,
                    layer = layer,
                    state = state,
                    statePath = statePath,
                    clip = clip,
                    isDirectBlendTree = inDirectBT
                });
            }
        }
        else if (motion is BlendTree bt)
        {
            bool isDirect = bt.blendType == BlendTreeType.Direct;

            foreach (ChildMotion child in bt.children)
            {
                ScanMotion(child.motion, state, statePath, layer, layerIndex, inDirectBT || isDirect);
            }
        }
    }

    private bool ClipHasProperty(AnimationClip clip, AnimProperty target)
    {
        EditorCurveBinding[] fBindings = AnimationUtility.GetCurveBindings(clip);
        if (fBindings.Any(b => b.path == target.binding.path && b.type == target.binding.type && (b.propertyName == target.binding.propertyName || b.propertyName.StartsWith(target.binding.propertyName.Substring(0, target.binding.propertyName.Length - 1)))))
        {
            return true;
        }

        EditorCurveBinding[] oBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
        if (oBindings.Any(b => b.path == target.binding.path && b.type == target.binding.type && (b.propertyName == target.binding.propertyName || b.propertyName.StartsWith(target.binding.propertyName.Substring(0, target.binding.propertyName.Length - 1)))))
        {
            return true;
        }

        return false;
    }

    private void OnGUI()
    {
        if (Event.current.type == EventType.MouseMove)
        {
            Repaint();
        }

        if (wrapStyle == null)
        {
            wrapStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };
        }

        if (bigNumberStyle == null)
        {
            bigNumberStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter
            };
        }

        if (linkStyle == null)
        {
            linkStyle = new GUIStyle(EditorStyles.linkLabel) { wordWrap = true };
            linkStyle.normal.textColor = Color.white;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Property Ranking (Lowest to Highest Priority)", EditorStyles.boldLabel);

        GUIContent propContent = new GUIContent(targetProperty.displayString, "Click to ping object in Hierarchy");
        Rect propRect = GUILayoutUtility.GetRect(propContent, linkStyle);

        if (GUI.Button(propRect, propContent, linkStyle))
        {
            PingProperty();
        }

        EditorGUILayout.Space();

        if (reports.Count == 0)
        {
            EditorGUILayout.HelpBox("This property is no longer found in the controller.", MessageType.Warning);

            return;
        }

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        List<IGrouping<int, PriorityReport>> groupedByLayer = reports.GroupBy(r => r.layerIndex).OrderBy(g => g.Key).ToList();
        List<int> uniqueLayers = groupedByLayer.Select(g => g.Key).OrderByDescending(l => l).ToList();

        foreach (IGrouping<int, PriorityReport> group in groupedByLayer)
        {
            int layerIndex = group.Key;
            int priorityRank = uniqueLayers.IndexOf(layerIndex);
            PriorityReport firstReport = group.First();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Layer {layerIndex}: {firstReport.layer.name}", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            EditorGUILayout.LabelField($"#{priorityRank}", bigNumberStyle, GUILayout.Width(35), GUILayout.ExpandHeight(true));

            EditorGUILayout.BeginVertical();

            for (int i = 0; i < group.Count(); i++)
            {
                PriorityReport report = group.ElementAt(i);

                EditorGUILayout.LabelField($"State: {report.statePath}");

                EditorGUILayout.BeginHorizontal();
                GUIContent btnContent = EditorGUIUtility.ObjectContent(report.clip, typeof(AnimationClip));

                if (GUILayout.Button(btnContent, EditorStyles.objectField, GUILayout.Height(20)))
                {
                    Selection.activeObject = report.clip;
                    EditorGUIUtility.PingObject(report.clip);
                    EditorApplication.delayCall += () => EditorApplication.ExecuteMenuItem("Window/Animation/Animation");
                }

                if (GUILayout.Button("Focus", GUILayout.Width(130)))
                {
                    AFWDAgnosticHelper.ViewState(controller, report.layer, report.state);
                }

                EditorGUILayout.EndHorizontal();

                if (report.isDirectBlendTree)
                {
                    EditorGUILayout.LabelField("[!] Inside Direct Blend Tree: Blends additively.", EditorStyles.miniLabel);
                }

                if (i < group.Count() - 1)
                {
                    EditorGUILayout.Space(5);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    private void PingProperty()
    {
        if (avatarRoot == null)
        {
            return;
        }

        Transform targetObj = string.IsNullOrEmpty(targetProperty.path) ? avatarRoot : avatarRoot.Find(targetProperty.path);

        if (targetObj != null)
        {
            Selection.activeGameObject = targetObj.gameObject;
            EditorGUIUtility.PingObject(targetObj.gameObject);
        }
        else
        {
            Debug.LogWarning($"{AFWDAgnosticHelper.Name}: Could not find animated object path on avatar: {(string.IsNullOrEmpty(targetProperty.path) ? "Root" : targetProperty.path)}");
        }
    }

    private class PriorityReport
    {
        public int layerIndex;
        public AnimatorControllerLayer layer;
        public AnimatorState state;
        public string statePath;
        public AnimationClip clip;
        public bool isDirectBlendTree;
    }
}

#endif