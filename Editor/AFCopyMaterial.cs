#if UNITY_EDITOR

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class AFCopyMaterial : EditorWindow
{
    public const string Name = "Copy Material";

    private GameObject avatarRoot;
    private bool duplicateTextures = true;
    private Vector2 rendererScroll;
    private Vector2 sharedScroll;

    private readonly List<RendererEntry> rendererEntries = new();
    private Dictionary<Material, List<RendererEntry>> selectedMaterialUsage = new();

    [MenuItem("TohruTheDragon/Avatar Fork/" + Name)]
    public static void ShowWindow()
    {
        GetWindow<AFCopyMaterial>($"AF {Name}");
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("This tool is for making unique copies of the selected materials of an avatar. With or without the textures being duplicated, based on the option selected.", MessageType.Info);
        EditorGUILayout.LabelField("Avatar Material + Texture Copy Tool", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        EditorGUI.BeginChangeCheck();
        avatarRoot = (GameObject)EditorGUILayout.ObjectField("Avatar Root", avatarRoot, typeof(GameObject), true);
        if (EditorGUI.EndChangeCheck())
        {
            RefreshRendererList();
        }

        duplicateTextures = EditorGUILayout.Toggle("Duplicate Textures", duplicateTextures);

        using (new EditorGUI.DisabledScope(avatarRoot == null))
        {
            if (GUILayout.Button("Scan Avatar"))
            {
                RefreshRendererList();
            }
        }

        EditorGUILayout.Space();

        if (avatarRoot == null)
        {
            EditorGUILayout.HelpBox("Select an avatar root to begin.", MessageType.Info);
            return;
        }

        if (rendererEntries.Count == 0)
        {
            EditorGUILayout.HelpBox("No MeshRenderer or SkinnedMeshRenderer objects found under the selected avatar.", MessageType.Info);
            return;
        }

        DrawSelectionButtons();
        EditorGUILayout.Space();

        RebuildSelectedMaterialUsage();

        DrawRendererList();
        EditorGUILayout.Space();
        DrawSharedMaterialSummary();
        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(!rendererEntries.Any(x => x.Selected)))
        {
            string btnText = duplicateTextures ? "Copy Materials + Textures" : "Copy Materials Only";
            if (GUILayout.Button(btnText))
            {
                ExportMaterialsAndTexturesBasedOnSharing();
            }
        }
    }

    private void DrawSelectionButtons()
    {
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Select All"))
        {
            foreach (RendererEntry entry in rendererEntries)
                entry.Selected = true;

            RebuildSelectedMaterialUsage();
        }

        if (GUILayout.Button("Select None"))
        {
            foreach (RendererEntry entry in rendererEntries)
                entry.Selected = false;

            RebuildSelectedMaterialUsage();
        }

        EditorGUILayout.EndHorizontal();
    }

    private void DrawRendererList()
    {
        EditorGUILayout.LabelField("Renderers", EditorStyles.boldLabel);

        rendererScroll = EditorGUILayout.BeginScrollView(rendererScroll, GUILayout.MinHeight(250));

        foreach (RendererEntry entry in rendererEntries)
        {
            EditorGUILayout.BeginVertical("box");

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            entry.Selected = EditorGUILayout.Toggle(entry.Selected, GUILayout.Width(18));
            EditorGUILayout.ObjectField(entry.GameObject, typeof(GameObject), true);
            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck())
            {
                RebuildSelectedMaterialUsage();
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Path", entry.HierarchyPath);
            EditorGUILayout.LabelField("Renderer Type", entry.Renderer.GetType().Name);

            Material[] materials = entry.Renderer.sharedMaterials;
            if (materials.Length == 0)
            {
                EditorGUILayout.LabelField("Materials", "None");
            }
            else
            {
                for (int i = 0; i < materials.Length; i++)
                {
                    DrawMaterialSlot(entry, materials[i], i);
                }
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawMaterialSlot(RendererEntry owner, Material material, int slotIndex)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel($"Material {slotIndex}");
        EditorGUILayout.ObjectField(material, typeof(Material), false);
        EditorGUILayout.EndHorizontal();

        EditorGUI.indentLevel++;

        if (material == null)
        {
            EditorGUILayout.LabelField("Usage", "Null");
            EditorGUI.indentLevel--;
            return;
        }

        if (!selectedMaterialUsage.TryGetValue(material, out List<RendererEntry> users))
        {
            EditorGUILayout.LabelField("Usage", "Not used by selected renderers");
            EditorGUI.indentLevel--;
            return;
        }

        if (users.Count <= 1)
        {
            EditorGUILayout.LabelField("Usage", "Unique among selected renderers");
        }
        else
        {
            string sharedWith = string.Join(", ", users.Where(x => x != owner).Select(x => x.GameObject.name));
            EditorGUILayout.LabelField("Usage", $"Shared by {users.Count} selected renderers");
            EditorGUILayout.LabelField("Shared With", sharedWith);
        }

        EditorGUI.indentLevel--;
    }

    private void DrawSharedMaterialSummary()
    {
        EditorGUILayout.LabelField("Shared Material Summary", EditorStyles.boldLabel);

        List<KeyValuePair<Material, List<RendererEntry>>> sharedMaterials = selectedMaterialUsage
            .Where(kvp => kvp.Key != null && kvp.Value.Count > 1)
            .OrderBy(kvp => kvp.Key.name)
            .ToList();

        if (sharedMaterials.Count == 0)
        {
            EditorGUILayout.HelpBox("No shared materials among currently selected renderers.", MessageType.Info);
            return;
        }

        sharedScroll = EditorGUILayout.BeginScrollView(sharedScroll, GUILayout.MinHeight(140));

        foreach (KeyValuePair<Material, List<RendererEntry>> kvp in sharedMaterials)
        {
            Material material = kvp.Key;
            List<RendererEntry> users = kvp.Value;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.ObjectField("Material", material, typeof(Material), false);
            EditorGUILayout.LabelField("Used By", $"{users.Count} selected renderers");
            EditorGUILayout.LabelField("Renderers", string.Join(", ", users.Select(x => x.GameObject.name)));
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndScrollView();
    }

    private void RefreshRendererList()
    {
        rendererEntries.Clear();

        if (avatarRoot == null)
        {
            selectedMaterialUsage = new Dictionary<Material, List<RendererEntry>>();
            return;
        }

        Renderer[] renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);

        foreach (Renderer renderer in renderers)
        {
            if (renderer is not SkinnedMeshRenderer && renderer is not MeshRenderer)
                continue;

            rendererEntries.Add(new RendererEntry
            {
                Selected = true,
                Renderer = renderer,
                GameObject = renderer.gameObject,
                HierarchyPath = GetHierarchyPath(renderer.transform, avatarRoot.transform)
            });
        }

        rendererEntries.Sort((a, b) => string.Compare(a.HierarchyPath, b.HierarchyPath, System.StringComparison.Ordinal));
        RebuildSelectedMaterialUsage();
    }

    private void RebuildSelectedMaterialUsage()
    {
        List<RendererEntry> selectedEntries = rendererEntries.Where(x => x.Selected).ToList();
        selectedMaterialUsage = BuildMaterialUsageMap(selectedEntries);
    }

    private Dictionary<Material, List<RendererEntry>> BuildMaterialUsageMap(List<RendererEntry> selectedEntries)
    {
        Dictionary<Material, List<RendererEntry>> materialUsage = new();

        foreach (RendererEntry entry in selectedEntries)
        {
            HashSet<Material> uniqueMaterialsOnRenderer = new();

            foreach (Material material in entry.Renderer.sharedMaterials)
            {
                if (material == null)
                    continue;

                if (!uniqueMaterialsOnRenderer.Add(material))
                    continue;

                if (!materialUsage.TryGetValue(material, out List<RendererEntry> users))
                {
                    users = new List<RendererEntry>();
                    materialUsage[material] = users;
                }

                users.Add(entry);
            }
        }

        return materialUsage;
    }

    private void ExportMaterialsAndTexturesBasedOnSharing()
    {
        string absoluteFolder = EditorUtility.OpenFolderPanel("Choose Output Folder", Application.dataPath, "");
        if (string.IsNullOrEmpty(absoluteFolder)) return;

        AssetDatabase.Refresh();

        absoluteFolder = NormalizePath(absoluteFolder);
        string dataPath = NormalizePath(Application.dataPath);
        if (!absoluteFolder.StartsWith(dataPath))
        {
            EditorUtility.DisplayDialog("Error", "Select a folder inside Assets.", "OK");
            return;
        }

        string rootExportPath = "Assets" + absoluteFolder.Substring(dataPath.Length);
        List<RendererEntry> selectedEntries = rendererEntries.Where(x => x.Selected).ToList();

        // Map Material and Texture usage
        var matToRenderers = BuildMaterialUsageMap(selectedEntries);
        Dictionary<Texture, List<Material>> texToMaterials = new();
        Dictionary<Material, List<TextureRef>> matToTexRefs = new();

        foreach (var mat in matToRenderers.Keys)
        {
            var refs = GetTextureRefs(mat);
            matToTexRefs[mat] = refs;

            if (duplicateTextures)
            {
                foreach (var r in refs)
                {
                    if (r.Texture == null) continue;
                    if (!texToMaterials.ContainsKey(r.Texture)) texToMaterials[r.Texture] = new List<Material>();
                    if (!texToMaterials[r.Texture].Contains(mat)) texToMaterials[r.Texture].Add(mat);
                }
            }
        }

        // Determine folder structure
        Dictionary<Material, string> matDestPaths = new();
        Dictionary<Texture, string> texDestPaths = new();
        HashSet<string> foldersToCreate = new();

        string sharedMatFolder = CombineAssetPath(rootExportPath, "SharedMaterials");
        string globalSharedTexFolder = CombineAssetPath(rootExportPath, "SharedTextures");

        // Path Materials first
        foreach (var kvp in matToRenderers)
        {
            Material mat = kvp.Key;
            var renderers = kvp.Value;

            string targetFolder = (renderers.Count > 1)
                ? sharedMatFolder
                : CombineAssetPath(rootExportPath, SanitizeFileName(renderers[0].GameObject.name));

            foldersToCreate.Add(targetFolder);
            matDestPaths[mat] = CombineAssetPath(targetFolder, SanitizeFileName(mat.name) + ".mat");
        }

        // Path Textures based on Material locations
        if (duplicateTextures)
        {
            foreach (var kvp in texToMaterials)
            {
                Texture tex = kvp.Key;
                var matsUsingThisTex = kvp.Value;
                string ext = Path.GetExtension(AssetDatabase.GetAssetPath(tex));
                string targetFolder;

                if (matsUsingThisTex.Count > 1)
                {
                    targetFolder = globalSharedTexFolder;
                }
                else
                {
                    Material soleMat = matsUsingThisTex[0];
                    string matFilePath = matDestPaths[soleMat];
                    string matDirectory = Path.GetDirectoryName(matFilePath);

                    targetFolder = CombineAssetPath(matDirectory, SanitizeFileName(soleMat.name) + ".fbm");
                }

                foldersToCreate.Add(targetFolder);
                texDestPaths[tex] = CombineAssetPath(targetFolder, SanitizeFileName(tex.name) + ext);
            }
        }

        // File system operations
        foreach (string folder in foldersToCreate)
        {
            EnsureFolderExists(folder);
        }

        Dictionary<Material, string> finalMatPaths = new();
        Dictionary<Texture, string> finalTexPaths = new();

        AssetDatabase.StartAssetEditing();
        try
        {
            if (duplicateTextures)
            {
                foreach (var kvp in texDestPaths)
                {
                    string p = AssetDatabase.GenerateUniqueAssetPath(kvp.Value);
                    if (AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(kvp.Key), p)) finalTexPaths[kvp.Key] = p;
                }
            }

            foreach (var kvp in matDestPaths)
            {
                string p = AssetDatabase.GenerateUniqueAssetPath(kvp.Value);
                if (AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(kvp.Key), p)) finalMatPaths[kvp.Key] = p;
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }

        AssetDatabase.Refresh();

        // Relinking
        if (duplicateTextures)
        {
            foreach (var kvp in finalMatPaths)
            {
                Material newMat = AssetDatabase.LoadAssetAtPath<Material>(kvp.Value);
                if (newMat == null) continue;

                bool changed = false;
                if (matToTexRefs.TryGetValue(kvp.Key, out var refs))
                {
                    foreach (var r in refs)
                    {
                        if (finalTexPaths.TryGetValue(r.Texture, out string newTexPath))
                        {
                            Texture newTex = AssetDatabase.LoadAssetAtPath<Texture>(newTexPath);
                            SerializedObject so = new SerializedObject(newMat);
                            if (TryReplaceTextureReference(so.FindProperty("m_SavedProperties"), r.PropertyName, newTex))
                            {
                                so.ApplyModifiedPropertiesWithoutUndo();
                                changed = true;
                            }
                        }
                    }
                }
                if (changed) EditorUtility.SetDirty(newMat);
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Done", "Material copies are finished.", "OK");
    }

    private List<TextureRef> GetTextureRefs(Material material)
    {
        List<TextureRef> results = new();
        if (material == null)
            return results;

        SerializedObject so = new SerializedObject(material);
        SerializedProperty savedProps = so.FindProperty("m_SavedProperties");
        if (savedProps == null)
            return results;

        SerializedProperty texEnvs = savedProps.FindPropertyRelative("m_TexEnvs");
        if (texEnvs == null || !texEnvs.isArray)
            return results;

        for (int i = 0; i < texEnvs.arraySize; i++)
        {
            SerializedProperty entry = texEnvs.GetArrayElementAtIndex(i);
            SerializedProperty displayNameProp = entry.FindPropertyRelative("first");
            SerializedProperty dataProp = entry.FindPropertyRelative("second");
            if (displayNameProp == null || dataProp == null)
                continue;

            string propertyName = displayNameProp.stringValue;

            SerializedProperty textureProp = dataProp.FindPropertyRelative("m_Texture");
            if (textureProp == null)
                continue;

            Texture texture = textureProp.objectReferenceValue as Texture;
            if (texture == null)
                continue;

            results.Add(new TextureRef
            {
                PropertyName = propertyName,
                Texture = texture
            });
        }

        return results;
    }

    private bool TryReplaceTextureReference(SerializedProperty savedProps, string propertyName, Texture newTexture)
    {
        SerializedProperty texEnvs = savedProps.FindPropertyRelative("m_TexEnvs");
        if (texEnvs == null || !texEnvs.isArray)
            return false;

        for (int i = 0; i < texEnvs.arraySize; i++)
        {
            SerializedProperty entry = texEnvs.GetArrayElementAtIndex(i);
            SerializedProperty displayNameProp = entry.FindPropertyRelative("first");
            SerializedProperty dataProp = entry.FindPropertyRelative("second");
            if (displayNameProp == null || dataProp == null)
                continue;

            if (displayNameProp.stringValue != propertyName)
                continue;

            SerializedProperty textureProp = dataProp.FindPropertyRelative("m_Texture");
            if (textureProp == null)
                return false;

            textureProp.objectReferenceValue = newTexture;
            return true;
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace("\\", "/").TrimEnd('/');
    }

    private static string CombineAssetPath(string a, string b)
    {
        a = NormalizePath(a);
        b = NormalizePath(b);
        if (string.IsNullOrEmpty(a))
            return b;
        if (string.IsNullOrEmpty(b))
            return a;
        return a + "/" + b;
    }

    private static void EnsureFolderExists(string folderPath)
    {
        folderPath = NormalizePath(folderPath);

        if (string.IsNullOrEmpty(folderPath) || folderPath == "Assets")
            return;

        if (AssetDatabase.IsValidFolder(folderPath))
            return;

        string[] parts = folderPath.Split('/');
        if (parts.Length == 0 || parts[0] != "Assets")
            return;

        string current = "Assets";
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }
            current = next;
        }
    }

    private static string GetHierarchyPath(Transform current, Transform root)
    {
        List<string> parts = new();

        while (current != null)
        {
            parts.Add(current.name);

            if (current == root)
                break;

            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c.ToString(), "_");
        }

        name = name.Replace("/", "_").Replace("\\", "_");

        if (string.IsNullOrWhiteSpace(name))
            name = "Unnamed";

        return name;
    }

    [System.Serializable]
    private class RendererEntry
    {
        public bool Selected;
        public Renderer Renderer;
        public GameObject GameObject;
        public string HierarchyPath;
    }

    private class TextureRef
    {
        public string PropertyName;
        public Texture Texture;
    }
}

#endif