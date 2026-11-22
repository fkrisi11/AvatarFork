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

        EditorGUILayout.Space(10);
        EditorGUILayout.HelpBox("If remapping is not enough, this resets the FBX's rig: it saves the Humanoid bone mapping, switches the FBX to Generic and applies, back to Humanoid and applies, then restores the saved bone mapping and muscle settings, and applies.", MessageType.Info);

        if (GUILayout.Button("Reset Humanoid Rig", GUILayout.Height(40)))
        {
            if (targetRoot == null) return;
            ResetHumanoidRig();
        }
    }

    private void ResetHumanoidRig()
    {
        var animator = targetRoot.GetComponent<Animator>();
        string assetPath = animator != null && animator.avatar != null ? AssetDatabase.GetAssetPath(animator.avatar) : null;
        var importer = string.IsNullOrEmpty(assetPath) ? null : AssetImporter.GetAtPath(assetPath) as ModelImporter;

        if (importer == null)
        {
            EditorUtility.DisplayDialog(Name, "Could not find the FBX of the avatar's Animator Avatar.", "OK");
            return;
        }
        if (importer.animationType != ModelImporterAnimationType.Human || importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
        {
            EditorUtility.DisplayDialog(Name, "The FBX has to be Humanoid, with Avatar Definition set to \"Create From This Model\".", "OK");
            return;
        }
        if (importer.humanDescription.human == null || importer.humanDescription.human.Length == 0)
        {
            EditorUtility.DisplayDialog(Name, "The FBX has no Humanoid bone mapping to restore.", "OK");
            return;
        }
        if (!EditorUtility.DisplayDialog(Name, $"This will reimport {assetPath} 3 times, and changes its Rig settings. This cannot be undone with Ctrl+Z.", "Reset Rig", "Cancel"))
        {
            return;
        }

        ResetRig(importer, assetPath);
    }

    private static void ResetRig(ModelImporter importer, string assetPath)
    {
        HumanDescription saved = importer.humanDescription;

        try
        {
            // 1. Generic, with the old configuration cleared, so the Humanoid step configures the rig from scratch
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.humanDescription = new HumanDescription();
            importer.SaveAndReimport();

            // 2. Humanoid, Unity auto-configures the rig and takes the rest pose from the current FBX
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.SaveAndReimport();

            HumanDescription fresh = importer.humanDescription;
            if (fresh.skeleton == null || fresh.skeleton.Length == 0)
            {
                throw new System.Exception("Unity could not create a skeleton for the FBX.");
            }

            // 3. Saved bone mapping and muscle settings, on top of the new rest pose
            var skeletonNames = new HashSet<string>(fresh.skeleton.Select(s => s.name));
            var missing = saved.human.Where(h => !skeletonNames.Contains(h.boneName)).ToList();
            foreach (var h in missing)
            {
                Debug.LogWarning($"{Name}: Bone '{h.boneName}' ({h.humanName}) no longer exists in the FBX, it was left out of the mapping.");
            }

            fresh.human = saved.human.Where(h => skeletonNames.Contains(h.boneName)).ToArray();
            fresh.upperArmTwist = saved.upperArmTwist;
            fresh.lowerArmTwist = saved.lowerArmTwist;
            fresh.upperLegTwist = saved.upperLegTwist;
            fresh.lowerLegTwist = saved.lowerLegTwist;
            fresh.armStretch = saved.armStretch;
            fresh.legStretch = saved.legStretch;
            fresh.feetSpacing = saved.feetSpacing;
            fresh.hasTranslationDoF = saved.hasTranslationDoF;
            importer.humanDescription = fresh;
            importer.SaveAndReimport();
        }
        catch (System.Exception e)
        {
            // Put the original configuration back, so the FBX isn't left Generic or half configured
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.humanDescription = saved;
            importer.SaveAndReimport();
            Debug.LogError($"{Name}: Resetting the rig failed, the original rig settings were restored. {e.Message}");
            return;
        }

        var avatar = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
        {
            Debug.LogWarning($"{Name}: The rig was reset, but the resulting Avatar is not a valid Humanoid. Check the Rig tab of {assetPath}.");
            return;
        }

        Debug.Log($"{Name}: Humanoid rig of {assetPath} was reset.");
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