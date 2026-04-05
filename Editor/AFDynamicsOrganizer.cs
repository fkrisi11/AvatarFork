#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.Dynamics;
using System.Collections.Generic;
using System.Linq;

public class AFDynamicsOrganizer : EditorWindow
{
    public const string Name = "Dynamics Organier";

    [System.Serializable]
    public class SelectionItem<T> where T : Component
    {
        public bool IsSelected = true;
        public bool ShowClips = false;
        public T Component;
        public int AffectedClipCount = 0;
        public string OriginalPath = "";
        public List<AnimationClip> AffectedClips = new List<AnimationClip>();
        public SelectionItem(T component) => Component = component;
    }

    public class CopyMappingItem
    {
        public Component SourceComponent;
        public bool IsInsideArmature;
        public bool PathExists;
        public Transform TargetOverride;
        public bool CreateIfMissing = true;
        public bool SkipCopy = false;
    }

    private VRC.SDK3.Avatars.Components.VRCAvatarDescriptor avatarDescriptor;
    private VRC.SDK3.Avatars.Components.VRCAvatarDescriptor targetAvatar;
    private Vector2 scrollPos;
    private string searchFilter = "";
    private bool repathAnimations = true;
    private int toolMode = 0;

    private List<SelectionItem<VRCPhysBoneColliderBase>> colliderItems = new List<SelectionItem<VRCPhysBoneColliderBase>>();
    private List<SelectionItem<VRCPhysBone>> physboneItems = new List<SelectionItem<VRCPhysBone>>();
    private List<SelectionItem<VRCContactSender>> senderItems = new List<SelectionItem<VRCContactSender>>();
    private List<SelectionItem<VRCContactReceiver>> receiverItems = new List<SelectionItem<VRCContactReceiver>>();
    private Dictionary<VRCPhysBoneColliderBase, VRCPhysBoneColliderBase> colliderMap = new Dictionary<VRCPhysBoneColliderBase, VRCPhysBoneColliderBase>();

    private Dictionary<AnimationClip, EditorCurveBinding[]> clipCache = new Dictionary<AnimationClip, EditorCurveBinding[]>();
    private Dictionary<AnimationClip, EditorCurveBinding[]> objectClipCache = new Dictionary<AnimationClip, EditorCurveBinding[]>();

    private bool foldoutPhysBones = false, foldoutColliders = false, foldoutSenders = false, foldoutReceivers = false;

    // Mapping UI State
    private bool showMappingUI = false;
    private List<CopyMappingItem> mappingItems = new List<CopyMappingItem>();
    private Transform sourceArmatureRoot;

    [MenuItem("TohruTheDragon/Avatar Fork/" + Name)]
    public static void ShowWindow() => GetWindow<AFDynamicsOrganizer>($"AF {Name}");

    private void OnGUI()
    {
        EditorGUILayout.Space(5);
        EditorGUI.BeginDisabledGroup(showMappingUI);
        toolMode = GUILayout.Toolbar(toolMode, new string[] { "Organize", "Copy to Target" });
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.Space(5);

        if (showMappingUI)
        {
            DrawMappingUI();
            return;
        }

        // Avatar Selection
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        if (toolMode == 0)
        {
            EditorGUILayout.HelpBox("This tab is for organizing physbones, physbone colliders, and contacts, from the avatar's armature, to the avatar's root.", MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox("This tab is for copying the selected physbones, physbone collider, and contacts, from one avatar, to another version of it.", MessageType.Info);
        }

        EditorGUI.BeginChangeCheck();
        avatarDescriptor = (VRC.SDK3.Avatars.Components.VRCAvatarDescriptor)EditorGUILayout.ObjectField(
            "Source Avatar", avatarDescriptor, typeof(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor), true);

        if (toolMode == 1)
        {
            targetAvatar = (VRC.SDK3.Avatars.Components.VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                "Target Avatar", targetAvatar, typeof(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor), true);
        }

        if (EditorGUI.EndChangeCheck() || (avatarDescriptor != null && GUILayout.Button("Scan / Refresh Avatar")))
        {
            RefreshLists();

            if (avatarDescriptor != null)
            {
                sourceArmatureRoot = FindArmatureRoot(avatarDescriptor.transform);
            }
            else
            {
                sourceArmatureRoot = null;
            }
        }
        EditorGUILayout.EndVertical();

        if (avatarDescriptor == null) return;

        // Search
        EditorGUILayout.BeginHorizontal();
        searchFilter = EditorGUILayout.TextField("Filter", searchFilter, EditorStyles.toolbarSearchField);
        if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(20))) searchFilter = "";
        EditorGUILayout.EndHorizontal();

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
        foldoutPhysBones = DrawCategory("PhysBones", physboneItems, foldoutPhysBones);
        foldoutColliders = DrawCategory("Colliders", colliderItems, foldoutColliders);
        foldoutSenders = DrawCategory("Contact Senders", senderItems, foldoutSenders);
        foldoutReceivers = DrawCategory("Contact Receivers", receiverItems, foldoutReceivers);
        EditorGUILayout.EndScrollView();

        // Action Button
        int totalSelected = GetTotalSelectedCount();

        EditorGUILayout.BeginVertical();

        if (toolMode == 0 && totalSelected > 0)
        {
            repathAnimations = EditorGUILayout.ToggleLeft(new GUIContent("Repath Animations", "Updates animations affecting the original path, to target the new path."), repathAnimations);
            EditorGUILayout.Space(5);
        }

        GUI.enabled = totalSelected > 0 && (toolMode == 0 || targetAvatar != null);
        string btnText = toolMode == 0 ? $"Organize {totalSelected} Components" : $"Prepare Copy for {totalSelected} Items";

        if (GUILayout.Button(btnText, GUILayout.Height(40)))
        {
            if (toolMode == 0) Organize();
            else PrepareMappingUI();
        }
        GUI.enabled = true;

        EditorGUILayout.Space(5);

        // Remove dynamics
        if (toolMode == 0)
        {
            EditorGUILayout.Space(5);
            GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
            if (GUILayout.Button("Remove All Dynamics", GUILayout.Height(25)))
            {
                RemoveDynamics();
            }
            GUI.backgroundColor = Color.white;
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(5);
    }

    private void DrawMappingUI()
    {
        EditorGUILayout.HelpBox("Review the items below. Elements with missing paths must be resolved before copying.", MessageType.Info);

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        // A mismatched item is one that natively lacks a path AND hasn't been given a target override yet.
        var mismatchedItems = mappingItems.Where(m => !m.PathExists && m.TargetOverride == null).ToList();
        var validItems = mappingItems.Where(m => m.PathExists || m.TargetOverride != null).ToList();

        bool hasUnresolvedItems = false;

        // --- SECTION 1: ITEMS WITH ISSUES ---
        if (mismatchedItems.Count > 0)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField($"Action Required ({mismatchedItems.Count} Items)", EditorStyles.boldLabel);

            foreach (var mapping in mismatchedItems)
            {
                // Determine if this specific item is in an invalid state
                bool isUnresolved = !mapping.SkipCopy && mapping.TargetOverride == null && !mapping.CreateIfMissing;

                if (isUnresolved)
                {
                    hasUnresolvedItems = true;
                }

                // Determine the box color
                if (mapping.SkipCopy)
                    GUI.backgroundColor = new Color(0.8f, 0.8f, 0.8f); // Grey (Skipped)
                else if (isUnresolved)
                    GUI.backgroundColor = new Color(1f, 0f, 0f);   // Red (Error - Must fix!)
                else
                    GUI.backgroundColor = new Color(1f, 0.85f, 0.6f);  // Yellow (Warning - Fallback active)

                EditorGUILayout.BeginVertical("box");
                GUI.backgroundColor = Color.white;

                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField(mapping.SourceComponent, mapping.SourceComponent.GetType(), true);
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.LabelField(mapping.IsInsideArmature ? "[Armature]" : "[Outside Armature]", EditorStyles.miniLabel, GUILayout.Width(120));
                mapping.SkipCopy = EditorGUILayout.ToggleLeft("Skip", mapping.SkipCopy, GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();

                // Disable the inner controls if the user marked it to skip
                EditorGUI.BeginDisabledGroup(mapping.SkipCopy);

                MessageType msgType = isUnresolved ? MessageType.Error : MessageType.Warning;

                if (mapping.IsInsideArmature)
                {
                    EditorGUILayout.HelpBox("Target avatar is missing this armature path.", msgType);
                    mapping.TargetOverride = (Transform)EditorGUILayout.ObjectField("Drop Override Target Here:", mapping.TargetOverride, typeof(Transform), true);
                }
                else
                {
                    EditorGUILayout.HelpBox("The Root Transform target does not exist on the new avatar.", msgType);
                    mapping.TargetOverride = (Transform)EditorGUILayout.ObjectField("Drop Override Root Here:", mapping.TargetOverride, typeof(Transform), true);
                }

                mapping.CreateIfMissing = EditorGUILayout.ToggleLeft("Fallback: Create the missing object(s) automatically", mapping.CreateIfMissing);

                EditorGUI.EndDisabledGroup();

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }
        }

        // --- SECTION 2: READY TO COPY ---
        if (validItems.Count > 0)
        {
            if (mismatchedItems.Count > 0)
            {
                EditorGUILayout.Space(36);
            }

            EditorGUILayout.LabelField($"Ready to Copy ({validItems.Count} Items)", EditorStyles.boldLabel);

            foreach (var mapping in validItems)
            {
                // Grey out if skipped
                GUI.backgroundColor = mapping.SkipCopy ? new Color(0.8f, 0.8f, 0.8f) : Color.white;
                EditorGUILayout.BeginVertical("box");
                GUI.backgroundColor = Color.white;

                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField(mapping.SourceComponent, mapping.SourceComponent.GetType(), true);
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.LabelField(mapping.IsInsideArmature ? "[Armature]" : "[Outside Armature]", EditorStyles.miniLabel, GUILayout.Width(120));
                mapping.SkipCopy = EditorGUILayout.ToggleLeft("Skip", mapping.SkipCopy, GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();

                EditorGUI.BeginDisabledGroup(mapping.SkipCopy);
                if (mapping.IsInsideArmature)
                {
                    mapping.TargetOverride = (Transform)EditorGUILayout.ObjectField("Target Host Element", mapping.TargetOverride, typeof(Transform), true);
                }
                else
                {
                    mapping.TargetOverride = (Transform)EditorGUILayout.ObjectField("Target Root Transform", mapping.TargetOverride, typeof(Transform), true);
                }
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.EndVertical();
            }
        }

        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(5);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Cancel", GUILayout.Height(40)))
        {
            showMappingUI = false;
        }

        // Disable the copy button if there are unresolved errors
        EditorGUI.BeginDisabledGroup(hasUnresolvedItems);

        // Make the button grey if disabled, green if ready
        GUI.backgroundColor = hasUnresolvedItems ? Color.grey : new Color(0.6f, 1f, 0.6f);
        int count = mismatchedItems.Where(item => !item.SkipCopy).Count() + validItems.Where(item => !item.SkipCopy).Count();
        string buttonText = hasUnresolvedItems ? "Resolve Errors to Copy" : $"Confirm & Copy {count} items";

        if (GUILayout.Button(buttonText, GUILayout.Height(40)))
        {
            ExecuteCopy();
            showMappingUI = false;
        }

        GUI.backgroundColor = Color.white;
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.EndHorizontal();
    }

    private Transform FindArmatureRoot(Transform avatarRoot)
    {
        var anim = avatarRoot.GetComponent<Animator>();
        if (anim != null && anim.isHuman)
        {
            Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            if (hips != null && hips.parent != null) return hips.parent;
        }

        // Fallback
        Transform fallback = avatarRoot.Find("Armature");
        return fallback != null ? fallback : avatarRoot;
    }

    private void PrepareMappingUI()
    {
        mappingItems.Clear();

        var allSelected = physboneItems.Where(i => i.IsSelected).Select(i => i.Component).Cast<Component>()
            .Concat(colliderItems.Where(i => i.IsSelected).Select(i => i.Component))
            .Concat(senderItems.Where(i => i.IsSelected).Select(i => i.Component))
            .Concat(receiverItems.Where(i => i.IsSelected).Select(i => i.Component))
            .ToList();

        foreach (var comp in allSelected)
        {
            if (comp == null) continue;

            var mapping = new CopyMappingItem { SourceComponent = comp };
            mapping.IsInsideArmature = sourceArmatureRoot != null && comp.transform.IsChildOf(sourceArmatureRoot);

            if (mapping.IsInsideArmature)
            {
                // We are looking for the exact bone to place the component ON
                string path = AnimationUtility.CalculateTransformPath(comp.transform, avatarDescriptor.transform);
                Transform found = targetAvatar.transform.Find(path);
                mapping.PathExists = found != null;
                mapping.TargetOverride = found;
            }
            else
            {
                // Outside armature: We need to make sure the ROOT TRANSFORM target exists
                Transform rootRef = GetRootTransform(comp);
                if (rootRef == null) rootRef = comp.transform;

                string rootPath = AnimationUtility.CalculateTransformPath(rootRef, avatarDescriptor.transform);
                Transform found = targetAvatar.transform.Find(rootPath);
                mapping.PathExists = found != null;
                mapping.TargetOverride = found;
            }

            mappingItems.Add(mapping);
        }

        showMappingUI = true;
    }

    private Transform GetRootTransform(Component comp)
    {
        if (comp is VRCPhysBone pb) return pb.rootTransform;
        if (comp is VRCPhysBoneColliderBase col) return col.rootTransform;
        if (comp is ContactBase cb) return cb.rootTransform;
        return null;
    }

    private void SetRootTransform(Component comp, Transform targetT)
    {
        if (comp is VRCPhysBone pb) pb.rootTransform = targetT;
        else if (comp is VRCPhysBoneColliderBase col) col.rootTransform = targetT;
        else if (comp is ContactBase cb) cb.rootTransform = targetT;
    }

    private void ExecuteCopy()
    {
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Copy Dynamics");
        colliderMap.Clear();

        // Process Colliders first so PhysBones can map to them
        ProcessCopyGroup(mappingItems.Where(m => m.SourceComponent is VRCPhysBoneColliderBase));
        ProcessCopyGroup(mappingItems.Where(m => m.SourceComponent is VRCPhysBone));
        ProcessCopyGroup(mappingItems.Where(m => m.SourceComponent is VRCContactSender || m.SourceComponent is VRCContactReceiver));

        Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
        Debug.Log($"{Name}: Successfully copied dynamics to {targetAvatar.name}!");
    }

    private void ProcessCopyGroup(IEnumerable<CopyMappingItem> group)
    {
        foreach (var mapping in group)
        {
            if (mapping.SkipCopy) continue;

            Component oldC = mapping.SourceComponent;
            Transform targetHost = null;

            if (mapping.IsInsideArmature)
            {
                if (mapping.TargetOverride != null)
                {
                    targetHost = mapping.TargetOverride;
                }
                else if (mapping.CreateIfMissing)
                {
                    targetHost = RemapAndCreateTransform(oldC.transform);
                }
                else continue; // Skip if no target and user opted out of creating it
            }
            else
            {
                // For outside armature, always recreate the GameObject structure for the component itself
                targetHost = RemapAndCreateTransform(oldC.transform);
            }

            if (targetHost == null) continue;

            System.Type concreteType = oldC.GetType();
            Component newC = Undo.AddComponent(targetHost.gameObject, concreteType);

            if (newC != null)
            {
                EditorUtility.CopySerialized(oldC, newC);

                Transform finalRootT = null;

                if (!mapping.IsInsideArmature)
                {
                    if (mapping.TargetOverride != null) finalRootT = mapping.TargetOverride;
                    else if (mapping.CreateIfMissing)
                    {
                        Transform oldRoot = GetRootTransform(oldC) != null ? GetRootTransform(oldC) : oldC.transform;
                        finalRootT = RemapAndCreateTransform(oldRoot);
                    }
                }
                else
                {
                    // Armature components just use standard path recreation for root transforms (if they had one)
                    Transform oldRoot = GetRootTransform(oldC);
                    if (oldRoot != null) finalRootT = RemapAndCreateTransform(oldRoot);
                }

                // --- STRICT REFERENCE CLEANUP & MAPPING ---

                // 1. Map or Null Root Transforms
                if (finalRootT != null)
                {
                    SetRootTransform(newC, finalRootT);
                }
                else
                {
                    // If no valid target root was found, ensure we don't leave it pointing to the source avatar
                    Transform oldRoot = GetRootTransform(oldC);
                    if (oldRoot != null && oldRoot.IsChildOf(avatarDescriptor.transform))
                    {
                        SetRootTransform(newC, null);
                    }
                }

                // 2. Track copied colliders for the map
                if (newC is VRCPhysBoneColliderBase newCol)
                {
                    colliderMap[oldC as VRCPhysBoneColliderBase] = newCol;
                }

                // 3. Clean up VRCPhysBone arrays (Colliders & Ignore Transforms)
                if (newC is VRCPhysBone pb)
                {
                    // Clean Colliders List
                    if (pb.colliders != null)
                    {
                        for (int i = 0; i < pb.colliders.Count; i++)
                        {
                            if (pb.colliders[i] != null)
                            {
                                if (colliderMap.TryGetValue(pb.colliders[i], out var mapped))
                                {
                                    pb.colliders[i] = mapped; // Update to the newly copied collider
                                }
                                else if (pb.colliders[i].transform.IsChildOf(avatarDescriptor.transform))
                                {
                                    pb.colliders[i] = null; // Old collider wasn't copied, strip the reference
                                }
                            }
                        }
                        pb.colliders.RemoveAll(c => c == null); // Clear out the null slots for a clean UI
                    }

                    // Clean Ignore Transforms List
                    if (pb.ignoreTransforms != null)
                    {
                        for (int i = 0; i < pb.ignoreTransforms.Count; i++)
                        {
                            if (pb.ignoreTransforms[i] != null && pb.ignoreTransforms[i].IsChildOf(avatarDescriptor.transform))
                            {
                                // Try to find the equivalent ignore path on the new avatar
                                string ignorePath = AnimationUtility.CalculateTransformPath(pb.ignoreTransforms[i], avatarDescriptor.transform);
                                pb.ignoreTransforms[i] = targetAvatar.transform.Find(ignorePath); // Becomes null if missing
                            }
                        }
                        pb.ignoreTransforms.RemoveAll(t => t == null); // Clear out the broken references
                    }
                }

                EditorUtility.SetDirty(newC);
            }
        }
    }

    private bool DrawCategory<T>(string label, List<SelectionItem<T>> items, bool foldoutState) where T : Component
    {
        var filteredItems = items.Where(i => i.Component != null && i.Component.name.ToLower().Contains(searchFilter.ToLower())).ToList();
        if (filteredItems.Count == 0 && items.Count > 0 && !string.IsNullOrEmpty(searchFilter)) return foldoutState;
        if (items.Count == 0) return foldoutState;

        int selectedCount = filteredItems.Count(i => i.IsSelected);
        EditorGUILayout.Space(2);

        GUI.backgroundColor = new Color(0.8f, 0.8f, 0.9f);
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        foldoutState = EditorGUILayout.Foldout(foldoutState, $"{label} ({selectedCount} / {filteredItems.Count})", true);
        if (GUILayout.Button("All", EditorStyles.toolbarButton, GUILayout.Width(40))) filteredItems.ForEach(i => i.IsSelected = true);
        if (GUILayout.Button("None", EditorStyles.toolbarButton, GUILayout.Width(45))) filteredItems.ForEach(i => i.IsSelected = false);
        EditorGUILayout.EndHorizontal();
        GUI.backgroundColor = Color.white;

        if (foldoutState)
        {
            foreach (var item in filteredItems)
            {
                EditorGUILayout.BeginVertical(EditorStyles.inspectorDefaultMargins);
                EditorGUILayout.BeginHorizontal();

                item.IsSelected = EditorGUILayout.Toggle(item.IsSelected, GUILayout.Width(20));

                GUI.color = (item.Component.gameObject.activeInHierarchy && (item.Component as Behaviour).enabled) ? Color.white : new Color(0.7f, 0.7f, 0.7f, 1f);

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField(item.Component, typeof(T), true);
                EditorGUI.EndDisabledGroup();
                GUI.color = Color.white;

                // Show toggle button for animations
                if (item.AffectedClipCount > 0)
                {
                    GUI.backgroundColor = item.ShowClips ? Color.cyan : Color.white;
                    if (GUILayout.Button(new GUIContent(item.ShowClips ? "Hide" : $"[{item.AffectedClipCount}]", "Toggle Animation List"), EditorStyles.miniButton, GUILayout.Width(50)))
                    {
                        item.ShowClips = !item.ShowClips;
                    }
                    GUI.backgroundColor = Color.white;
                }

                EditorGUILayout.EndHorizontal();

                // Sub-item box
                if (item.ShowClips && item.AffectedClipCount > 0)
                {
                    GUI.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.3f);
                    EditorGUILayout.BeginVertical("box");
                    GUI.backgroundColor = Color.white;

                    EditorGUI.BeginDisabledGroup(true);
                    foreach (var clip in item.AffectedClips)
                    {
                        if (clip == null) continue;
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(25);
                        EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false);
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.EndDisabledGroup();
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndVertical();
            }
        }
        return foldoutState;
    }

    private void RefreshLists()
    {
        if (avatarDescriptor == null) return;
        clipCache.Clear(); objectClipCache.Clear();
        var layers = avatarDescriptor.baseAnimationLayers.Concat(avatarDescriptor.specialAnimationLayers);
        foreach (var layer in layers)
        {
            if (layer.animatorController is AnimatorController ac)
            {
                foreach (var state in ac.layers.SelectMany(l => GetAllStates(l.stateMachine)))
                {
                    foreach (var clip in GetClipsFromState(state))
                    {
                        if (clip != null && !clipCache.ContainsKey(clip))
                        {
                            clipCache[clip] = AnimationUtility.GetCurveBindings(clip);
                            objectClipCache[clip] = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                        }
                    }
                }
            }
        }
        colliderItems = ScanCategory<VRCPhysBoneColliderBase>("Physics/Colliders");
        physboneItems = ScanCategory<VRCPhysBone>("Physics/Physbones");
        senderItems = ScanCategory<VRCContactSender>("Physics/Contacts/Senders");
        receiverItems = ScanCategory<VRCContactReceiver>("Physics/Contacts/Receivers");
    }

    private List<SelectionItem<T>> ScanCategory<T>(string targetFolderPath) where T : Component
    {
        var components = avatarDescriptor.GetComponentsInChildren<T>(true).ToList();

        Transform targetFolder = avatarDescriptor.transform.Find(targetFolderPath);
        var items = new List<SelectionItem<T>>();

        foreach (var c in components)
        {
            if (toolMode == 0 && targetFolder != null && c.transform.IsChildOf(targetFolder))
                continue;

            var newItem = new SelectionItem<T>(c);
            newItem.OriginalPath = AnimationUtility.CalculateTransformPath(c.transform, avatarDescriptor.transform);

            foreach (var clipData in clipCache)
            {
                if (clipData.Value.Any(b => b.path == newItem.OriginalPath && IsBindingEligible(b)) ||
                    objectClipCache[clipData.Key].Any(b => b.path == newItem.OriginalPath && IsBindingEligible(b)))
                {
                    newItem.AffectedClipCount++;
                    newItem.AffectedClips.Add(clipData.Key);
                }
            }
            items.Add(newItem);
        }
        return items;
    }

    private static bool IsBindingEligible(EditorCurveBinding binding)
    {
        if (binding.type == typeof(Transform)) return false;
        if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive") return true;
        System.Type t = binding.type;
        return typeof(VRCPhysBone).IsAssignableFrom(t) || typeof(VRCPhysBoneColliderBase).IsAssignableFrom(t) || typeof(ContactBase).IsAssignableFrom(t);
    }

    private void Organize()
    {
        GameObject root = avatarDescriptor.gameObject;
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Organize Dynamics");
        Transform physicsRoot = null;
        Dictionary<string, string> pathMap = new Dictionary<string, string>();

        ProcessSection(colliderItems, "Colliders", ref physicsRoot, ref pathMap);
        ProcessSection(physboneItems, "Physbones", ref physicsRoot, ref pathMap);

        if (senderItems.Any(i => i.IsSelected) || receiverItems.Any(i => i.IsSelected))
        {
            if (physicsRoot == null) physicsRoot = GetOrCreateFolder(root.transform, "Physics");
            Transform contactRoot = GetOrCreateFolder(physicsRoot, "Contacts");
            ProcessSection(senderItems, "Senders", ref contactRoot, ref pathMap);
            ProcessSection(receiverItems, "Receivers", ref contactRoot, ref pathMap);
        }

        if (repathAnimations && pathMap.Count > 0)
        {
            foreach (var clip in clipCache.Keys) { Undo.RecordObject(clip, "Repath Animations"); RepathClip(clip, pathMap); }
        }
        Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
        RefreshLists();
        Debug.Log($"{Name}: Dynamics Organized!");
    }

    private void ProcessSection<T>(List<SelectionItem<T>> items, string folderName, ref Transform parent, ref Dictionary<string, string> pathMap) where T : Component
    {
        var selected = items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;
        if (parent == null) parent = GetOrCreateFolder(avatarDescriptor.transform, "Physics");
        Transform targetFolder = GetOrCreateFolder(parent, folderName);
        foreach (var item in selected)
        {
            var oldC = item.Component;
            GameObject newGO = new GameObject(GetUniqueName(targetFolder, oldC.gameObject.name));
            newGO.transform.SetParent(targetFolder, false);
            newGO.SetActive(oldC.gameObject.activeInHierarchy && (oldC as Behaviour).enabled);
            var newC = newGO.AddComponent(oldC.GetType());
            EditorUtility.CopySerialized(oldC, newC);
            if (newC is VRCPhysBoneColliderBase col) col.rootTransform = col.rootTransform ? col.rootTransform : oldC.transform;
            if (newC is VRCPhysBone pb) pb.rootTransform = pb.rootTransform ? pb.rootTransform : oldC.transform;
            if (newC is ContactBase cb) cb.rootTransform = cb.rootTransform ? cb.rootTransform : oldC.transform;
            pathMap[item.OriginalPath] = AnimationUtility.CalculateTransformPath(newGO.transform, avatarDescriptor.transform);
            Undo.RegisterCreatedObjectUndo(newGO, "Organize");
            Undo.DestroyObjectImmediate(oldC);
        }
    }

    private void RepathClip(AnimationClip clip, Dictionary<string, string> pathMap)
    {
        var floatBindings = AnimationUtility.GetCurveBindings(clip);
        var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
        bool changed = false;
        foreach (var b in floatBindings)
        {
            if (IsBindingEligible(b) && pathMap.TryGetValue(b.path, out string newPath))
            {
                var c = AnimationUtility.GetEditorCurve(clip, b);
                AnimationUtility.SetEditorCurve(clip, b, null);
                var nb = b; nb.path = newPath; AnimationUtility.SetEditorCurve(clip, nb, c);
                changed = true;
            }
        }
        foreach (var b in objectBindings)
        {
            if (IsBindingEligible(b) && pathMap.TryGetValue(b.path, out string newPath))
            {
                var k = AnimationUtility.GetObjectReferenceCurve(clip, b);
                AnimationUtility.SetObjectReferenceCurve(clip, b, null);
                var nb = b; nb.path = newPath; AnimationUtility.SetObjectReferenceCurve(clip, nb, k);
                changed = true;
            }
        }
        if (changed) EditorUtility.SetDirty(clip);
    }

    private Transform RemapAndCreateTransform(Transform sourceT)
    {
        if (sourceT == null) return null;
        if (sourceT == avatarDescriptor.transform) return targetAvatar.transform;

        string path = AnimationUtility.CalculateTransformPath(sourceT, avatarDescriptor.transform);
        Transform found = targetAvatar.transform.Find(path);

        if (found != null) return found;

        string[] parts = path.Split('/');
        Transform currentTargetParent = targetAvatar.transform;
        Transform currentSourceParent = avatarDescriptor.transform;

        foreach (string part in parts)
        {
            Transform nextTarget = currentTargetParent.Find(part);
            Transform nextSource = currentSourceParent.Find(part);

            if (nextTarget == null)
            {
                GameObject newGO = new GameObject(part);
                Undo.RegisterCreatedObjectUndo(newGO, "Create Missing Hierarchy");

                nextTarget = newGO.transform;
                nextTarget.SetParent(currentTargetParent, false);

                if (nextSource != null)
                {
                    nextTarget.localPosition = nextSource.localPosition;
                    nextTarget.localRotation = nextSource.localRotation;
                    nextTarget.localScale = nextSource.localScale;
                    newGO.SetActive(nextSource.gameObject.activeSelf);
                }
            }

            currentTargetParent = nextTarget;
            if (nextSource != null) currentSourceParent = nextSource;
        }

        return currentTargetParent;
    }

    private void RemoveDynamics()
    {
        if (avatarDescriptor == null) return;

        if (!EditorUtility.DisplayDialog("Remove Avatar Dynamics",
            $"Are you sure you want to remove ALL PhysBones, Colliders, and Contacts from {avatarDescriptor.name}?",
            "Yes, Remove Everything", "Cancel"))
        {
            return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Remove Dynamics");

        var pbs = avatarDescriptor.GetComponentsInChildren<VRCPhysBone>(true);
        var colliders = avatarDescriptor.GetComponentsInChildren<VRCPhysBoneColliderBase>(true);
        var contacts = avatarDescriptor.GetComponentsInChildren<ContactBase>(true);

        int totalRemoved = pbs.Length + colliders.Length + contacts.Length;

        foreach (var c in pbs) Undo.DestroyObjectImmediate(c);
        foreach (var c in colliders) Undo.DestroyObjectImmediate(c);
        foreach (var c in contacts) Undo.DestroyObjectImmediate(c);

        Undo.CollapseUndoOperations(Undo.GetCurrentGroup());

        RefreshLists();
        Debug.Log($"{Name}: Successfully removed {totalRemoved} components from {avatarDescriptor.name}.");
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

    private string GetUniqueName(Transform parent, string baseName)
    {
        string name = baseName;
        int count = 1;

        while (parent.Find(name) != null)
        {
            name = $"{baseName} ({count})";
            count++;
        }

        return name;
    }

    private Transform GetOrCreateFolder(Transform parent, string name)
    {
        Transform found = parent.Find(name);

        if (!found)
        {
            found = new GameObject(name).transform;
            found.SetParent(parent, false);
            Undo.RegisterCreatedObjectUndo(found.gameObject, "Folder");
        }

        return found;
    }

    private int GetTotalSelectedCount()
    {
        return physboneItems.Count(i => i.IsSelected) + colliderItems.Count(i => i.IsSelected) + senderItems.Count(i => i.IsSelected) + receiverItems.Count(i => i.IsSelected);
    }
}

#endif