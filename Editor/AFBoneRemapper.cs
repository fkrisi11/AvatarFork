#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

public class AFBoneRemapper : EditorWindow
{
    public const string Name = "Bone Remapper";

    private GameObject targetRoot;

    [MenuItem("TohruTheDragon/Avatar Fork/" + Name)]
    public static void ShowWindow() => GetWindow<AFBoneRemapper>($"AF {Name}");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("This tool can be used, if your avatar's mesh explodes, after overwriting the FBX outside of Unity.", MessageType.Info);

        targetRoot = (GameObject)EditorGUILayout.ObjectField("Avatar", targetRoot, typeof(GameObject), true);

        if (GUILayout.Button("Remap Bones", GUILayout.Height(40)))
        {
            if (targetRoot == null) return;
            RemapBones();
        }
    }

    private void RemapBones()
    {
        var smrs = targetRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        Undo.RecordObjects(smrs, "Bone Remapping");

        foreach (var smr in smrs)
        {
            if (smr.sharedMesh == null) continue;

            // 1. Get the FBX Asset data
            string assetPath = AssetDatabase.GetAssetPath(smr.sharedMesh);
            GameObject fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (fbxAsset == null) continue;

            var sourceSmr = fbxAsset.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                                    .FirstOrDefault(s => s.sharedMesh == smr.sharedMesh);
            if (sourceSmr == null) continue;

            // 2. Identify the common roots for path calculation
            Transform sceneRoot = smr.rootBone != null ? smr.rootBone : targetRoot.transform;
            Transform assetRoot = sourceSmr.rootBone != null ? sourceSmr.rootBone : fbxAsset.transform;

            // 3. Map scene bones by their relative path to the root
            // Key: "Spine/Chest/Shoulder_L", Value: Transform
            var scenePathMap = new Dictionary<string, Transform>();
            var allSceneTransforms = targetRoot.GetComponentsInChildren<Transform>(true);
            foreach (var t in allSceneTransforms)
            {
                string path = GetRelativePath(t, sceneRoot);
                if (!scenePathMap.ContainsKey(path)) scenePathMap.Add(path, t);
            }

            // 4. Rebuild the bones array
            Transform[] newBones = new Transform[sourceSmr.bones.Length];
            for (int i = 0; i < sourceSmr.bones.Length; i++)
            {
                Transform assetBone = sourceSmr.bones[i];
                if (assetBone == null) continue;

                string assetBonePath = GetRelativePath(assetBone, assetRoot);

                // Try to find the bone by Path first
                if (scenePathMap.TryGetValue(assetBonePath, out Transform foundByPath))
                {
                    newBones[i] = foundByPath;
                }
                else
                {
                    // Fallback: Try name matching if the path changed slightly
                    var foundByName = allSceneTransforms.FirstOrDefault(t => t.name == assetBone.name);
                    newBones[i] = foundByName;
                }
            }

            smr.bones = newBones;

            // 5. Fix Root Bone
            if (sourceSmr.rootBone != null)
            {
                string rootPath = GetRelativePath(sourceSmr.rootBone, assetRoot);
                if (scenePathMap.TryGetValue(rootPath, out Transform newRoot))
                    smr.rootBone = newRoot;
            }

            EditorUtility.SetDirty(smr);
        }

        Debug.Log($"{Name}: Bone Remapping complete.");
    }

    private string GetRelativePath(Transform t, Transform root)
    {
        if (t == root) return "";

        List<string> pathParts = new List<string>();
        Transform current = t;

        while (current != null && current != root)
        {
            pathParts.Add(current.name);
            current = current.parent;
        }

        pathParts.Reverse();
        return string.Join("/", pathParts);
    }
}

#endif