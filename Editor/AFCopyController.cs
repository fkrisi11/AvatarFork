#if UNITY_EDITOR

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public class AFCopyController : EditorWindow
{
    public const string Name = "Copy Controller";

    private GameObject avatarRoot;
    private bool copyBase, copyAdditive, copyGesture, copyAction, copyFX = true;
    private Vector2 scrollPos;

    private bool showShared = false;
    private bool showUnique = false;
    private Dictionary<string, bool> layerFoldouts = new();

    // Key: AnimationClip, Value: List of "Controller/Layer" identifiers
    private Dictionary<AnimationClip, List<string>> clipLayerMap = new();

    private string sharedSearch = "";
    private string uniqueSearch = "";

    private class ExportJob
    {
        public string ControllerName;
        public string NewControllerPath;
        public Dictionary<int, string> ClipPathMapping = new();
    }

    [MenuItem("TohruTheDragon/Avatar Fork/" + Name)]
    public static void ShowWindow() => GetWindow<AFCopyController>($"AF {Name}");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("This tool copies the selected controllers, and animations within them, and assigns the new animations to the new controllers.", MessageType.Info);
        avatarRoot = (GameObject)EditorGUILayout.ObjectField("Avatar Root", avatarRoot, typeof(GameObject), true);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Layers to Copy", EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        copyBase = EditorGUILayout.ToggleLeft("Base", copyBase, GUILayout.Width(60));
        copyAdditive = EditorGUILayout.ToggleLeft("Add", copyAdditive, GUILayout.Width(60));
        copyGesture = EditorGUILayout.ToggleLeft("Gest", copyGesture, GUILayout.Width(60));
        copyAction = EditorGUILayout.ToggleLeft("Act", copyAction, GUILayout.Width(60));
        copyFX = EditorGUILayout.ToggleLeft("FX", copyFX, GUILayout.Width(60));
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("Scan & Analyze Animations", GUILayout.Height(30)))
        {
            AnalyzeControllers();
        }

        DrawAnalysisResults();

        GUILayout.FlexibleSpace();

        using (new EditorGUI.DisabledScope(avatarRoot == null || clipLayerMap.Count == 0))
        {
            if (GUILayout.Button("Copy Selected Controllers", GUILayout.Height(40)))
            {
                AnalyzeControllers();
                ExecuteExport();
            }
        }
    }

    private void AnalyzeControllers()
    {
        clipLayerMap.Clear();

        if (avatarRoot == null)
        {
            return;
        }

        Dictionary<string, AnimatorController> controllers = GetTargetControllers();

        foreach (var kvp in controllers)
        {
            if (kvp.Value == null)
            {
                continue;
            }

            foreach (var layer in kvp.Value.layers)
            {
                string layerId = $"{kvp.Key}/{layer.name}";
                ExtractClipsFromStateMachine(layer.stateMachine, layerId);
            }
        }
    }

    private void ExtractClipsFromStateMachine(AnimatorStateMachine sm, string layerId)
    {
        if (sm == null)
        {
            return;
        }

        foreach (var state in sm.states)
        {
            ExtractFromMotion(state.state.motion, layerId);
        }

        foreach (var subSm in sm.stateMachines)
        {
            ExtractClipsFromStateMachine(subSm.stateMachine, layerId);
        }
    }

    private void ExtractFromMotion(Motion motion, string layerId)
    {
        if (motion == null)
        {
            return;
        }

        if (motion is AnimationClip clip)
        {
            if (!clipLayerMap.ContainsKey(clip))
            {
                clipLayerMap[clip] = new List<string>();
            }

            if (!clipLayerMap[clip].Contains(layerId))
            {
                clipLayerMap[clip].Add(layerId);
            }
        }
        else if (motion is BlendTree tree)
        {
            foreach (var child in tree.children)
            {
                ExtractFromMotion(child.motion, layerId);
            }
        }
    }

    private void DrawAnalysisResults()
    {
        if (clipLayerMap.Count == 0)
        {
            return;
        }

        EditorGUILayout.Space();
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, "box", GUILayout.MaxHeight(400));

        // Prepare filtered lists based on search strings
        var sharedClips = clipLayerMap
            .Where(x => x.Value.Distinct().Count() > 1)
            .Where(x => string.IsNullOrEmpty(sharedSearch) || x.Key.name.ToLower().Contains(sharedSearch.ToLower()))
            .ToList();

        var uniqueClips = clipLayerMap
            .Where(x => x.Value.Distinct().Count() == 1)
            .Where(x => string.IsNullOrEmpty(uniqueSearch) || x.Key.name.ToLower().Contains(uniqueSearch.ToLower()))
            .ToList();

        // Shared section
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        showShared = EditorGUILayout.Foldout(showShared, $"Shared Animations ({sharedClips.Count})", true);
        sharedSearch = GUILayout.TextField(sharedSearch, GUI.skin.FindStyle("ToolbarSearchTextField"), GUILayout.Width(150));
        if (GUILayout.Button("", GUI.skin.FindStyle("ToolbarSearchCancelButton"))) { sharedSearch = ""; GUI.FocusControl(null); }
        EditorGUILayout.EndHorizontal();

        if (showShared)
        {
            EditorGUI.indentLevel++;
            foreach (var item in sharedClips)
            {
                DrawAnimationRow(item.Key, $"(Used in {item.Value.Count} layers)");
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(5);

        // Unique section
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        showUnique = EditorGUILayout.Foldout(showUnique, $"Unique Animations ({uniqueClips.Count})", true);
        uniqueSearch = GUILayout.TextField(uniqueSearch, GUI.skin.FindStyle("ToolbarSearchTextField"), GUILayout.Width(150));
        if (GUILayout.Button("", GUI.skin.FindStyle("ToolbarSearchCancelButton"))) { uniqueSearch = ""; GUI.FocusControl(null); }
        EditorGUILayout.EndHorizontal();

        if (showUnique)
        {
            EditorGUI.indentLevel++;
            var groups = uniqueClips.GroupBy(x => x.Value[0]);
            foreach (var group in groups)
            {
                if (!layerFoldouts.ContainsKey(group.Key)) layerFoldouts[group.Key] = false;

                // Only show the layer group if it has clips matching the search
                if (group.Any())
                {
                    layerFoldouts[group.Key] = EditorGUILayout.Foldout(layerFoldouts[group.Key], $"{group.Key} ({group.Count()})");
                    if (layerFoldouts[group.Key])
                    {
                        EditorGUI.indentLevel++;
                        foreach (var item in group) DrawAnimationRow(item.Key);
                        EditorGUI.indentLevel--;
                    }
                }
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawAnimationRow(AnimationClip clip, string subText = "")
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(clip.name, subText, EditorStyles.miniLabel);

        if (GUILayout.Button(new GUIContent("Focus", "Focus animation in Project window"), GUILayout.Width(50), GUILayout.Height(18)))
        {
            FocusAsset(clip);
        }

        EditorGUILayout.EndHorizontal();
    }

    private void FocusAsset(UnityEngine.Object obj)
    {
        if (obj == null)
        {
            return;
        }

        EditorGUIUtility.PingObject(obj);
        Selection.activeObject = obj;
    }

    private void ExecuteExport()
    {
        string absolutePath = EditorUtility.OpenFolderPanel("Select Export Folder", "Assets", "");

        if (string.IsNullOrEmpty(absolutePath))
        {
            return;
        }

        AssetDatabase.Refresh();
        string projectPath = "Assets" + absolutePath.Replace(Application.dataPath, "").Replace("\\", "/");
        Dictionary<string, AnimatorController> controllersToProcess = GetTargetControllers();

        Dictionary<AnimationClip, string> globalClipDestinations = new Dictionary<AnimationClip, string>();
        HashSet<string> foldersToCreate = new HashSet<string>();
        List<ExportJob> activeJobs = new List<ExportJob>();

        foreach (var entry in controllersToProcess)
        {
            if (entry.Value == null)
            {
                continue;
            }

            string ctrlFolder = Path.Combine(projectPath, entry.Key).Replace("\\", "/");
            foldersToCreate.Add(ctrlFolder);

            ExportJob job = new ExportJob { ControllerName = entry.Key };
            activeJobs.Add(job);

            foreach (var layer in entry.Value.layers)
            {
                List<AnimationClip> clipsInLayer = new List<AnimationClip>();
                FindClipsRecursively(layer.stateMachine, clipsInLayer);

                foreach (var clip in clipsInLayer.Distinct())
                {
                    if (globalClipDestinations.ContainsKey(clip)) continue;

                    var usageIds = clipLayerMap[clip].Distinct().ToList();
                    var usedControllers = usageIds.Select(id => id.Split('/')[0]).Distinct().ToList();

                    string targetDir;

                    if (usedControllers.Count > 1)
                    {
                        // Shared across multiple controllers
                        targetDir = Path.Combine(projectPath, "SharedAnimations").Replace("\\", "/");
                    }
                    else if (usageIds.Count > 1)
                    {
                        // Shared across layers but within ONLY one controller
                        targetDir = Path.Combine(ctrlFolder, "SharedAnimations").Replace("\\", "/");
                    }
                    else
                    {
                        // Unique to one specific layer
                        targetDir = Path.Combine(ctrlFolder, SanitizePath(layer.name)).Replace("\\", "/");
                    }

                    foldersToCreate.Add(targetDir);
                    globalClipDestinations[clip] = Path.Combine(targetDir, clip.name + ".anim").Replace("\\", "/");
                }
            }
        }

        // Creation pass
        foreach (var folder in foldersToCreate.OrderBy(s => s.Length))
        {
            EnsureFolder(folder);
        }

        AssetDatabase.Refresh();

        // Copy pass
        AssetDatabase.StartAssetEditing();

        try
        {
            foreach (var job in activeJobs)
            {
                AnimatorController original = controllersToProcess[job.ControllerName];
                string ctrlFolder = Path.Combine(projectPath, job.ControllerName).Replace("\\", "/");

                job.NewControllerPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(ctrlFolder, original.name + ".controller").Replace("\\", "/"));
                AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(original), job.NewControllerPath);

                List<AnimationClip> allClipsInThisController = new List<AnimationClip>();
                foreach (var layer in original.layers)
                {
                    FindClipsRecursively(layer.stateMachine, allClipsInThisController);
                }

                foreach (var clip in allClipsInThisController.Distinct())
                {
                    if (!globalClipDestinations.TryGetValue(clip, out string targetPath))
                    {
                        continue;
                    }

                    string diskPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", targetPath));

                    if (!File.Exists(diskPath))
                    {
                        string uniquePath = AssetDatabase.GenerateUniqueAssetPath(targetPath);
                        AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(clip), uniquePath);
                        globalClipDestinations[clip] = uniquePath;
                    }

                    job.ClipPathMapping[clip.GetInstanceID()] = globalClipDestinations[clip];
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }

        AssetDatabase.Refresh();

        // Relink pass
        foreach (var job in activeJobs)
        {
            AnimatorController newCtrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(job.NewControllerPath);

            if (newCtrl == null)
            {
                continue;
            }

            Dictionary<AnimationClip, AnimationClip> finalMap = new Dictionary<AnimationClip, AnimationClip>();

            foreach (var map in job.ClipPathMapping)
            {
                AnimationClip oldClip = EditorUtility.InstanceIDToObject(map.Key) as AnimationClip;
                AnimationClip newClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(map.Value);
                if (oldClip && newClip) finalMap[oldClip] = newClip;
            }

            if (newCtrl.layers != null)
            {
                for (int i = 0; i < newCtrl.layers.Length; i++)
                {
                    if (newCtrl.layers[i] != null && newCtrl.layers[i].stateMachine != null)
                        RelinkStateMachine(newCtrl.layers[i].stateMachine, finalMap);
                }
            }

            EditorUtility.SetDirty(newCtrl);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Success", "Process Complete!", "OK");
    }

    private void RelinkStateMachine(AnimatorStateMachine sm, Dictionary<AnimationClip, AnimationClip> mapping)
    {
        if (sm == null)
        {
            return;
        }

        foreach (var state in sm.states)
        {
            if (state.state.motion != null)
            {
                state.state.motion = GetRelinkedMotion(state.state.motion, mapping);
            }
        }

        foreach (var subSm in sm.stateMachines)
        {
            RelinkStateMachine(subSm.stateMachine, mapping);
        }
    }

    private Motion GetRelinkedMotion(Motion motion, Dictionary<AnimationClip, AnimationClip> mapping)
    {
        if (motion == null)
        {
            return null;
        }

        if (motion is AnimationClip clip)
        {
            return mapping.TryGetValue(clip, out var newClip) ? newClip : clip;
        }

        if (motion is BlendTree tree)
        {
            ChildMotion[] children = tree.children;

            for (int i = 0; i < children.Length; i++)
            {
                children[i].motion = GetRelinkedMotion(children[i].motion, mapping);
            }

            tree.children = children;

            return tree;
        }

        return motion;
    }

    private void FindClipsRecursively(AnimatorStateMachine sm, List<AnimationClip> results)
    {
        if (sm == null)
        {
            return;
        }

        foreach (var state in sm.states)
        {
            AddClipsFromMotion(state.state.motion, results);
        }

        foreach (var subSm in sm.stateMachines)
        {
            FindClipsRecursively(subSm.stateMachine, results);
        }
    }

    private void AddClipsFromMotion(Motion motion, List<AnimationClip> results)
    {
        if (motion == null)
        {
            return;
        }

        if (motion is AnimationClip clip)
        {
            results.Add(clip);
        }
        else if (motion is BlendTree tree)
        {
            foreach (var child in tree.children)
            {
                AddClipsFromMotion(child.motion, results);
            }
        }
    }

    private Dictionary<string, AnimatorController> GetTargetControllers()
    {
        var dict = new Dictionary<string, AnimatorController>();

        if (avatarRoot == null)
        {
            return dict;
        }

        Component descriptor = avatarRoot.GetComponent("VRCAvatarDescriptor");

        if (descriptor != null)
        {
            var baseField = descriptor.GetType().GetField("baseAnimationLayers");

            if (baseField != null)
            {
                var layers = (System.Array)baseField.GetValue(descriptor);
                if (copyBase && layers.Length > 0) dict["Base"] = GetVRCAnim(layers.GetValue(0));
                if (copyAdditive && layers.Length > 1) dict["Additive"] = GetVRCAnim(layers.GetValue(1));
                if (copyGesture && layers.Length > 2) dict["Gesture"] = GetVRCAnim(layers.GetValue(2));
                if (copyAction && layers.Length > 3) dict["Action"] = GetVRCAnim(layers.GetValue(3));
                if (copyFX && layers.Length > 4) dict["FX"] = GetVRCAnim(layers.GetValue(4));
            }
        }

        return dict;
    }

    private AnimatorController GetVRCAnim(object layerStruct) => layerStruct.GetType().GetField("animatorController")?.GetValue(layerStruct) as AnimatorController;

    private void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        string parent = Path.GetDirectoryName(path).Replace("\\", "/");
        string folder = Path.GetFileName(path);

        if (!AssetDatabase.IsValidFolder(parent))
        {
            EnsureFolder(parent);
        }

        AssetDatabase.CreateFolder(parent, folder);
    }

    private string SanitizePath(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
    }
}

#endif