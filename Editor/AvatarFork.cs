#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;

public class AvatarFork : EditorWindow
{
    private Vector2 scrollPosition;

    [MenuItem("TohruTheDragon/Avatar Fork/Main Window", false, -100)]
    public static void ShowWindow()
    {
        GetWindow<AvatarFork>("Avatar Fork");
    }

    private void OnGUI()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        EditorGUILayout.HelpBox("Welcome to Avatar Fork: a set of tools, that allows you to create custom versions of avatars, that arrived in a unitypackage.\nThe order of the tools on the UI represent my recommendation, for dealing with brand new avatars.", MessageType.Info);

        EditorGUILayout.Space(15);

        DrawButton(AFCopyMaterial.Name,
            "Makes unique copies of materials used by the avatar", () =>
            {
                GetWindow<AFCopyMaterial>(AFCopyMaterial.Name);
            }, AFCopyMaterial.Name);

        DrawButton(AFCopyExpression.Name,
            "Makes unique copies of the avatar's menu, menu icons, and parameter list", () =>
            {
                GetWindow<AFCopyExpression>(AFCopyExpression.Name);
            }, AFCopyExpression.Name);

        DrawButton(AFCopyController.Name,
            "Makes unique copies of the avatar's selected controllers, and all animations in them", () =>
            {
                GetWindow<AFCopyController>(AFCopyController.Name);
            }, AFCopyController.Name);

        DrawButton(AFParameterOrganizer.Name,
            "Organize your controller parameters easily", () =>
            {
                GetWindow<AFParameterOrganizer>(AFParameterOrganizer.Name);
            }, AFParameterOrganizer.Name);

        DrawButton(AFWDAgnosticHelper.Name,
            "Find what properties are not animated in animations, that are animated in other states", () =>
            {
                GetWindow<AFWDAgnosticHelper>(AFWDAgnosticHelper.Name);
            }, AFWDAgnosticHelper.Name);

        DrawButton(AFWDOffResetHelper.Name,
            "Only reset blendshapes in your Reset animation, that you actually use elsewhere", () =>
            {
                GetWindow<AFWDOffResetHelper>(AFWDOffResetHelper.Name);
            }, AFWDOffResetHelper.Name);

        DrawButton(AFDynamicsOrganizer.Name,
            "Move dynamics (Physbone, Physbone Collider, Contacts) to the avatar's root, in an organized way, or copy dynamics to another avatar", () =>
            {
                GetWindow<AFDynamicsOrganizer>(AFDynamicsOrganizer.Name);
            }, AFDynamicsOrganizer.Name);

        DrawButton(AFBoneRemapper.Name,
            "Try to fix your avatar, if you overwrote the FBX outside of Unity, and the meshes exploded", () =>
            {
                GetWindow<AFBoneRemapper>(AFBoneRemapper.Name);
            }, AFBoneRemapper.Name);

        EditorGUILayout.EndScrollView();
    }

    private void DrawButton(string title, string description, System.Action action, string buttonText)
    {
        Color bgColor = GUI.backgroundColor;
        GUI.backgroundColor = Color.gray;
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        Rect headerRect = EditorGUILayout.BeginHorizontal();

        GUILayout.Label(title, EditorStyles.boldLabel);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField(description, EditorStyles.wordWrappedLabel);

        EditorGUILayout.Space(5);

        if (GUILayout.Button(buttonText, GUILayout.Height(25)))
        {
            action.Invoke();
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(5);
        GUI.backgroundColor = bgColor;
    }
}

#endif