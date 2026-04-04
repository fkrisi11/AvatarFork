#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using System.Collections.Generic;
using System.IO;

public class AFCopyExpression : EditorWindow
{
    public const string Name = "Copy Expression";

    private VRCAvatarDescriptor avatarDescriptor;
    private List<VRCExpressionsMenu> previewMenus = new List<VRCExpressionsMenu>();
    private bool copyParameters = true;
    private bool copyIcons = true;
    private Vector2 scrollPos;

    private int toolbarIndex = 0;
    private string[] toolbarLabels = { "Copy", "Analysis" };
    private bool missingSectionExpanded = true;
    private bool duplicateSectionExpanded = true;

    private Dictionary<VRCExpressionsMenu, bool> menuFoldoutStates = new Dictionary<VRCExpressionsMenu, bool>();
    private Dictionary<Texture2D, bool> iconFoldoutStates = new Dictionary<Texture2D, bool>();
    private Dictionary<VRCExpressionsMenu, List<string>> missingIconGroups = new Dictionary<VRCExpressionsMenu, List<string>>();
    private List<DuplicateIconGroup> duplicateIcons = new List<DuplicateIconGroup>();

    private GUIStyle bigNumberStyle;
    private GUIStyle bigControlStyle;
    private GUIStyle largeInfoStyle;
    private GUIStyle typeLabelStyle;

    [MenuItem("TohruTheDragon/Avatar Fork/" + Name)]
    public static void ShowWindow()
    {
        GetWindow<AFCopyExpression>($"AF {Name}");
    }

    private void InitializeStyles()
    {
        if (bigNumberStyle != null) return;
        bigNumberStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, alignment = TextAnchor.MiddleRight };
        bigControlStyle = new GUIStyle(EditorStyles.label) { fontSize = 13, wordWrap = true };
        largeInfoStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        typeLabelStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
    }

    private void OnGUI()
    {
        InitializeStyles();
        EditorGUILayout.Space();
        toolbarIndex = GUILayout.Toolbar(toolbarIndex, toolbarLabels);
        EditorGUILayout.Space();

        if (toolbarIndex == 0)
        {
            EditorGUILayout.HelpBox("This tab is for making unique copies of the expression menu, all sub-menus, all menu icons, and also the expression parameters, based on the selection.", MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox("This tab is for finding menu items, that don't have an icon assigned or have an icon assigned, that is already used for another menu item somewhere.", MessageType.Info);
        }

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        avatarDescriptor = (VRCAvatarDescriptor)EditorGUILayout.ObjectField("Target Avatar", avatarDescriptor, typeof(VRCAvatarDescriptor), true);

        if (EditorGUI.EndChangeCheck())
        {
            UpdatePreview();
        }

        if (GUILayout.Button(EditorGUIUtility.IconContent("Refresh"), GUILayout.Width(30), GUILayout.Height(20)))
        {
            UpdatePreview();
        }

        EditorGUILayout.EndHorizontal();

        if (toolbarIndex == 0)
        {
            DrawClonerTab();
        }
        else
        {
            DrawAnalysisTab();
        }
    }

    private void DrawClonerTab()
    {
        copyParameters = EditorGUILayout.Toggle("Copy Parameters Asset", copyParameters);
        copyIcons = EditorGUILayout.Toggle("Copy Icons", copyIcons);

        if (avatarDescriptor != null && avatarDescriptor.expressionParameters != null && copyParameters)
        {
            ParameterStats stats = GetParameterStats(avatarDescriptor.expressionParameters);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.color = stats.totalBits > 256 ? new Color(1f, 0.4f, 0.4f) : Color.cyan;
            EditorGUILayout.LabelField($"Network Memory: {stats.totalBits} / 256 bits", largeInfoStyle);
            GUI.color = Color.white;

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(GUILayout.Width(180));
            EditorGUILayout.LabelField($"Synced: {stats.syncedCount}", largeInfoStyle);
            EditorGUILayout.LabelField($"Un-synced: {stats.unsyncedCount}", largeInfoStyle);
            EditorGUILayout.LabelField($"Total: {stats.syncedCount + stats.unsyncedCount}", largeInfoStyle);
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            GUI.color = Color.green;
            EditorGUILayout.LabelField($"Bool: {stats.bools}", typeLabelStyle);
            GUI.color = new Color(0f, 1f, 1f);
            EditorGUILayout.LabelField($"Int: {stats.ints}", typeLabelStyle);
            GUI.color = Color.yellow;
            EditorGUILayout.LabelField($"Float: {stats.floats}", typeLabelStyle);
            GUI.color = Color.white;
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space();

        int totalCount = previewMenus.Count + (copyParameters && avatarDescriptor?.expressionParameters != null ? 1 : 0);
        EditorGUILayout.LabelField($"Assets to Copy: {totalCount}", EditorStyles.boldLabel);

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, EditorStyles.helpBox);
        EditorGUI.BeginDisabledGroup(true);

        if (copyParameters && avatarDescriptor?.expressionParameters != null)
        {
            DrawAssetRow(avatarDescriptor.expressionParameters, $"{avatarDescriptor.expressionParameters.parameters.Length} parameters");
        }

        foreach (var menu in previewMenus)
        {
            DrawAssetRow(menu, $"{(menu.controls != null ? menu.controls.Count : 0)} items");
        }

        if (avatarDescriptor != null && copyIcons)
        {
            EditorGUILayout.LabelField($"...plus {GetUniqueIconCount()} icons", EditorStyles.miniLabel);
        }

        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space();

        GUI.enabled = avatarDescriptor != null && avatarDescriptor.expressionsMenu != null;

        if (GUILayout.Button("Copy Assets", GUILayout.Height(40)))
        {
            StartCloningProcess();
        }

        GUI.enabled = true;
    }

    private void DrawAnalysisTab()
    {
        if (avatarDescriptor == null)
        {
            EditorGUILayout.HelpBox("Assign an avatar.", MessageType.Info);
            return;
        }

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        missingSectionExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(missingSectionExpanded, $"Missing Icons ({missingIconGroups.Count})");
        if (missingSectionExpanded)
        {
            foreach (KeyValuePair<VRCExpressionsMenu, List<string>> group in missingIconGroups)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();

                if (!menuFoldoutStates.ContainsKey(group.Key))
                {
                    menuFoldoutStates[group.Key] = false;
                }

                menuFoldoutStates[group.Key] = EditorGUILayout.Foldout(menuFoldoutStates[group.Key], "", true);

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField(GUIContent.none, group.Key, typeof(VRCExpressionsMenu), false);
                EditorGUI.EndDisabledGroup();

                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField($"{group.Value.Count} missing", bigNumberStyle, GUILayout.Width(150));
                EditorGUILayout.EndHorizontal();

                if (menuFoldoutStates[group.Key])
                {
                    EditorGUI.indentLevel++;

                    foreach (string name in group.Value)
                    {
                        EditorGUILayout.LabelField($"• {name}", bigControlStyle);
                    }

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
            }
        }
        EditorGUILayout.EndFoldoutHeaderGroup();

        EditorGUILayout.Space(18);

        duplicateSectionExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(duplicateSectionExpanded, $"Duplicate Icons ({duplicateIcons.Count})");

        if (duplicateSectionExpanded)
        {
            foreach (DuplicateIconGroup group in duplicateIcons)
            {
                if (!iconFoldoutStates.ContainsKey(group.icon))
                {
                    iconFoldoutStates[group.icon] = false;
                }

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();

                iconFoldoutStates[group.icon] = EditorGUILayout.Foldout(iconFoldoutStates[group.icon], "", true);

                if (GUILayout.Button(group.icon, GUIStyle.none, GUILayout.Width(25), GUILayout.Height(25)))
                {
                    EditorGUIUtility.PingObject(group.icon);
                }

                EditorGUILayout.LabelField(group.icon.name, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField($"{group.usages.Count} duplicates", bigNumberStyle, GUILayout.Width(150));
                EditorGUILayout.EndHorizontal();

                if (iconFoldoutStates[group.icon])
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(true);

                    foreach (ControlUsage usage in group.usages)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.ObjectField(GUIContent.none, usage.menu, typeof(VRCExpressionsMenu), false);
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.LabelField(usage.controlName, bigControlStyle);
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
            }
        }

        EditorGUILayout.EndFoldoutHeaderGroup();
        EditorGUILayout.EndScrollView();
    }

    private int GetUniqueIconCount()
    {
        HashSet<Texture2D> uniqueIcons = new HashSet<Texture2D>();

        foreach (var menu in previewMenus)
        {
            foreach (var control in menu.controls)
            {
                if (control.icon != null)
                {
                    uniqueIcons.Add(control.icon);
                }
            }
        }

        return uniqueIcons.Count;
    }

    private void StartCloningProcess()
    {
        UpdatePreview();
        VRCExpressionsMenu rootMenu = avatarDescriptor.expressionsMenu;
        string systemPath = EditorUtility.OpenFolderPanel("Select Export Folder", Path.GetDirectoryName(AssetDatabase.GetAssetPath(rootMenu)), "");

        if (string.IsNullOrEmpty(systemPath))
        {
            return;
        }

        string projectPath = systemPath.Replace(Application.dataPath, "Assets").Replace("\\", "/");

        // Step 1: Perform all file copies
        AssetDatabase.StartAssetEditing();
        Dictionary<VRCExpressionsMenu, string> menuPathMapping = new Dictionary<VRCExpressionsMenu, string>();
        Dictionary<Texture2D, string> iconPathMapping = new Dictionary<Texture2D, string>();

        try
        {
            string iconFolderPath = $"{projectPath}/Icons";
            if (copyIcons && !AssetDatabase.IsValidFolder(iconFolderPath)) AssetDatabase.CreateFolder(projectPath, "Icons");

            // Copy Menus
            foreach (VRCExpressionsMenu menu in previewMenus)
            {
                menuPathMapping[menu] = GetClonePath(menu, projectPath);
                AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(menu), menuPathMapping[menu]);
            }

            // Copy Icons
            if (copyIcons)
            {
                foreach (VRCExpressionsMenu menu in previewMenus)
                {
                    foreach (VRCExpressionsMenu.Control control in menu.controls)
                    {
                        if (control.icon != null && !iconPathMapping.ContainsKey(control.icon))
                        {
                            iconPathMapping[control.icon] = GetClonePath(control.icon, iconFolderPath);
                            AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(control.icon), iconPathMapping[control.icon]);
                        }
                    }
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }

        AssetDatabase.Refresh();
        LinkAssets(rootMenu, menuPathMapping, iconPathMapping);
    }

    private string GetClonePath(Object original, string targetFolder)
    {
        string originalPath = AssetDatabase.GetAssetPath(original);
        return AssetDatabase.GenerateUniqueAssetPath($"{targetFolder}/{Path.GetFileName(originalPath)}");
    }

    private void LinkAssets(VRCExpressionsMenu rootMenu, Dictionary<VRCExpressionsMenu, string> menuPaths, Dictionary<Texture2D, string> iconPaths)
    {
        foreach (var kvp in menuPaths)
        {
            VRCExpressionsMenu newMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(kvp.Value);

            if (newMenu == null)
            {
                continue;
            }

            foreach (var control in newMenu.controls)
            {
                // Link Submenus
                if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu && control.subMenu != null)
                {
                    if (menuPaths.TryGetValue(control.subMenu, out string newSubPath))
                    {
                        control.subMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(newSubPath);
                    }
                }

                // Link Icons
                if (control.icon != null && iconPaths.TryGetValue(control.icon, out string newIconPath))
                {
                    control.icon = AssetDatabase.LoadAssetAtPath<Texture2D>(newIconPath);
                }
            }

            EditorUtility.SetDirty(newMenu);
        }

        AssetDatabase.SaveAssets();
    }

    private void UpdatePreview()
    {
        previewMenus.Clear();
        missingIconGroups.Clear();
        duplicateIcons.Clear();

        if (avatarDescriptor == null || avatarDescriptor.expressionsMenu == null)
        {
            return;
        }

        VRCExpressionsMenu root = avatarDescriptor.expressionsMenu;
        HashSet<VRCExpressionsMenu> collected = new HashSet<VRCExpressionsMenu> { root };

        CollectAllSubmenus(root, collected);
        previewMenus.AddRange(collected);

        Dictionary<Texture2D, List<ControlUsage>> iconTracker = new Dictionary<Texture2D, List<ControlUsage>>();

        foreach (VRCExpressionsMenu menu in previewMenus)
        {
            foreach (VRCExpressionsMenu.Control control in menu.controls)
            {
                if (control.icon == null)
                {
                    if (!missingIconGroups.ContainsKey(menu))
                    {
                        missingIconGroups[menu] = new List<string>();
                    }

                    missingIconGroups[menu].Add(control.name);
                }
                else
                {
                    if (!iconTracker.ContainsKey(control.icon))
                    {
                        iconTracker[control.icon] = new List<ControlUsage>();
                    }

                    iconTracker[control.icon].Add(new ControlUsage
                    {
                        menu = menu,
                        controlName = control.name
                    });
                }
            }
        }

        foreach (KeyValuePair<Texture2D, List<ControlUsage>> kvp in iconTracker)
        {
            if (kvp.Value.Count > 1)
            {
                duplicateIcons.Add(new DuplicateIconGroup
                {
                    icon = kvp.Key,
                    usages = kvp.Value
                });
            }
        }

        Repaint();
    }

    private static void CollectAllSubmenus(VRCExpressionsMenu current, HashSet<VRCExpressionsMenu> collected)
    {
        if (current == null || current.controls == null)
        {
            return;
        }

        foreach (var control in current.controls)
        {
            if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu && control.subMenu != null)
            {
                if (collected.Add(control.subMenu))
                {
                    CollectAllSubmenus(control.subMenu, collected);
                }
            }
        }
    }

    private struct ParameterStats
    {
        public int totalBits, syncedCount, unsyncedCount, bools, ints, floats;
    }

    private ParameterStats GetParameterStats(VRCExpressionParameters parameters)
    {
        ParameterStats stats = new ParameterStats();

        foreach (var p in parameters.parameters)
        {
            if (string.IsNullOrEmpty(p.name))
            {
                continue;
            }

            switch (p.valueType)
            {
                case VRCExpressionParameters.ValueType.Bool:
                    stats.bools++;
                    break;
                case VRCExpressionParameters.ValueType.Int:
                    stats.ints++;
                    break;
                case VRCExpressionParameters.ValueType.Float:
                    stats.floats++;
                    break;
            }

            if (p.networkSynced)
            {
                stats.syncedCount++;
                stats.totalBits += (p.valueType == VRCExpressionParameters.ValueType.Bool) ? 1 : 8;
            }
            else
            {
                stats.unsyncedCount++;
            }
        }

        return stats;
    }

    private void DrawAssetRow(Object obj, string rightText)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.ObjectField(GUIContent.none, obj, obj.GetType(), false);
        EditorGUILayout.LabelField(rightText, EditorStyles.miniLabel, GUILayout.Width(85));
        EditorGUILayout.EndHorizontal();
    }

    private struct ControlUsage
    {
        public VRCExpressionsMenu menu;
        public string controlName;
    }

    private struct DuplicateIconGroup
    {
        public Texture2D icon;
        public List<ControlUsage> usages;
    }
}

#endif