#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.Components;

public class AFParameterOrganizer : EditorWindow
{
    public const string Name = "Parameter Organizer";

    private VRCAvatarDescriptor avatarDescriptor;
    private AnimatorController currentController;

    private int selectedLayerIndex = 4; // Defaults to FX layer
    private readonly string[] layerNames = { "Base", "Additive", "Action", "Gesture", "FX", "Sitting", "T Pose", "IK Pose" };
    private readonly VRCAvatarDescriptor.AnimLayerType[] layerTypes = {
        VRCAvatarDescriptor.AnimLayerType.Base,
        VRCAvatarDescriptor.AnimLayerType.Additive,
        VRCAvatarDescriptor.AnimLayerType.Action,
        VRCAvatarDescriptor.AnimLayerType.Gesture,
        VRCAvatarDescriptor.AnimLayerType.FX,
        VRCAvatarDescriptor.AnimLayerType.Sitting,
        VRCAvatarDescriptor.AnimLayerType.TPose,
        VRCAvatarDescriptor.AnimLayerType.IKPose
    };

    // Sorting & Grouping Options
    private bool enablePinnedOptions = true;
    private readonly List<string> pinnedParams = new List<string>
    {
        // Built-in Parameters
        "IsLocal",
        "PreviewMode",
        "Viseme",
        "Voice",
        "GestureLeft",
        "GestureRight",
        "GestureLeftWeight",
        "GestureRightWeight",
        "AngularY",
        "VelocityX",
        "VelocityY",
        "VelocityZ",
        "VelocityMagnitude",
        "Upright",
        "Grounded",
        "Seated",
        "AFK",
        "TrackingType",
        "VRMode",
        "MuteSelf",
        "InStation",
        "Earmuffs",
        "IsOnFriendsList",
        "AvatarVersion",
        "IsAnimatorEnabled",

        // Avatar Scaling Parameters
        "ScaleModified",
        "ScaleFactor",
        "ScaleFactorInverse",
        "EyeHeightAsMeters",
        "EyeHeightAsPercent",
    };

    // List & Selection State
    private Vector2 scrollPosition;
    private List<int> selectedIndices = new List<int>();
    private int lastSelectedIndex = -1;

    // Performance & Layout Constants
    private const float ROW_HEIGHT = 20f;
    private const float LABEL_PADDING_X = 5f;
    private const float LABEL_PADDING_Y = 2f;
    private const float NAME_WIDTH_RESERVE = 40f;
    private const float TYPE_RIGHT_OFFSET = 95f;
    private const float TYPE_WIDTH = 90f;

    // Cached Styles
    private GUIStyle rightAlignedStyle;

    [MenuItem("TohruTheDragon/Avatar Fork/" + Name)]
    public static void ShowWindow()
    {
        GetWindow<AFParameterOrganizer>($"AF {Name}");
    }

    private void OnEnable()
    {
        wantsMouseMove = true;
        Undo.undoRedoPerformed += OnUndoRedo;
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    private void OnUndoRedo()
    {
        ClearSelection();
        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Organize your animator parameters, either with the buttons, or by selecting parameters, and dragging them around. \nYou can use Shift to select multiple parameters at once.", MessageType.Info);

        Event e = Event.current;

        // Scroll wheel intercept
        if (e.rawType == EventType.ScrollWheel && DragAndDrop.GetGenericData("DragItemIndices") != null)
        {
            scrollPosition.y += e.delta.y * 40f;
            scrollPosition.y = Mathf.Max(0f, scrollPosition.y); // Prevent negative drag-scroll
            e.Use();
            Repaint();
        }

        GUILayout.Space(10);

        EditorGUI.BeginChangeCheck();
        avatarDescriptor = (VRCAvatarDescriptor)EditorGUILayout.ObjectField("Avatar Descriptor", avatarDescriptor, typeof(VRCAvatarDescriptor), true);
        if (EditorGUI.EndChangeCheck())
        {
            UpdateCurrentController();
            ClearSelection();
        }

        if (avatarDescriptor == null)
        {
            EditorGUILayout.HelpBox("Please assign a VRC Avatar Descriptor.", MessageType.Info);
            return;
        }

        EditorGUI.BeginChangeCheck();
        selectedLayerIndex = EditorGUILayout.Popup("Playable Layer", selectedLayerIndex, layerNames);
        if (EditorGUI.EndChangeCheck())
        {
            UpdateCurrentController();
            ClearSelection();
        }

        if (currentController == null)
        {
            EditorGUILayout.HelpBox($"No Animator Controller found on the {layerNames[selectedLayerIndex]} layer.", MessageType.Warning);
            return;
        }

        DrawOptionsAndActions();
        DrawParameterList();
    }

    private void UpdateCurrentController()
    {
        currentController = null;
        if (avatarDescriptor == null) return;

        var layerType = layerTypes[selectedLayerIndex];

        foreach (var layer in avatarDescriptor.baseAnimationLayers)
        {
            if (layer.type == layerType && !layer.isDefault && layer.animatorController != null)
            {
                currentController = layer.animatorController as AnimatorController;
                break;
            }
        }

        if (currentController == null)
        {
            foreach (var layer in avatarDescriptor.specialAnimationLayers)
            {
                if (layer.type == layerType && !layer.isDefault && layer.animatorController != null)
                {
                    currentController = layer.animatorController as AnimatorController;
                    break;
                }
            }
        }
    }

    private void DrawOptionsAndActions()
    {
        GUILayout.Space(10);
        EditorGUILayout.LabelField("Sorting Options", EditorStyles.boldLabel);

        enablePinnedOptions = EditorGUILayout.Toggle(new GUIContent("Pin VRC Parameters", "Keep the Built-in VRChat parameters at the top or bottom of the list, based on the order or sorting."), enablePinnedOptions);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent("Sort A-Z", "Sort all parameters in this layer in alphabetical order."))) SortParameters(false);
        if (GUILayout.Button(new GUIContent("Sort Z-A", "Sort all parameters in this layer in reverse alphabetical order."))) SortParameters(true);
        if (GUILayout.Button(new GUIContent("Group by Path", "Groups parameters together, that share the same prefix (for ex: Clothes/Top/1, Clothes/Top/2)"))) GroupByPath();
        GUILayout.EndHorizontal();
    }

    private void DrawParameterList()
    {
        GUILayout.Space(10);
        EditorGUILayout.LabelField($"Parameters ({currentController.parameters.Length})", EditorStyles.boldLabel);

        Event e = Event.current;
        AnimatorControllerParameter[] parameters = currentController.parameters;
        int totalParams = parameters.Length;

        scrollPosition = GUILayout.BeginScrollView(scrollPosition, "box", GUILayout.ExpandHeight(true));

        // Prevent negative scroll bounce from breaking the scrollbar thumb
        if (scrollPosition.y < 0)
            scrollPosition.y = 0;

        Rect contentRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.Height(totalParams * ROW_HEIGHT));

        float relativeMouseY = e.mousePosition.y - contentRect.y;
        int mathematicalHoverIndex = Mathf.Clamp(Mathf.RoundToInt(relativeMouseY / ROW_HEIGHT), 0, totalParams);

        if (DragAndDrop.GetGenericData("DragItemIndices") != null)
        {
            if (e.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Move;
                Repaint();
                e.Use();
            }
            else if (e.type == EventType.Repaint && DragAndDrop.visualMode == DragAndDropVisualMode.Move)
            {
                Rect lineRect = new Rect(contentRect.x, contentRect.y + mathematicalHoverIndex * ROW_HEIGHT - 1, contentRect.width, 3);
                EditorGUI.DrawRect(lineRect, new Color(0.3f, 0.6f, 1f, 1f));
            }
            else if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                PerformDragDrop(mathematicalHoverIndex);
                e.Use();
            }
        }

        int firstVisible = Mathf.Max(0, Mathf.FloorToInt(scrollPosition.y / ROW_HEIGHT));
        int visibleCount = Mathf.CeilToInt(position.height / ROW_HEIGHT) + 2;
        int lastVisible = Mathf.Min(totalParams - 1, firstVisible + visibleCount);

        for (int i = firstVisible; i <= lastVisible; i++)
        {
            Rect rowRect = new Rect(contentRect.x, contentRect.y + i * ROW_HEIGHT, contentRect.width, ROW_HEIGHT);

            if (selectedIndices.Contains(i) && e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rowRect, new Color(0.2f, 0.4f, 0.8f, 0.5f));
            }

            if (e.type == EventType.MouseDown && rowRect.Contains(e.mousePosition))
            {
                if (e.shift && lastSelectedIndex != -1)
                {
                    selectedIndices.Clear();
                    int start = Mathf.Min(lastSelectedIndex, i);
                    int end = Mathf.Max(lastSelectedIndex, i);
                    for (int j = start; j <= end; j++) selectedIndices.Add(j);
                    e.Use();
                }
                else if (!selectedIndices.Contains(i))
                {
                    selectedIndices.Clear();
                    selectedIndices.Add(i);
                    lastSelectedIndex = i;
                    GUI.FocusControl(null);
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseUp && rowRect.Contains(e.mousePosition))
            {
                if (!e.shift && selectedIndices.Contains(i) && selectedIndices.Count > 1)
                {
                    selectedIndices.Clear();
                    selectedIndices.Add(i);
                    lastSelectedIndex = i;
                    e.Use();
                }
            }

            if (e.type == EventType.MouseDrag && rowRect.Contains(e.mousePosition) && selectedIndices.Contains(i))
            {
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.SetGenericData("DragItemIndices", selectedIndices.ToList());
                DragAndDrop.StartDrag("Dragging Parameters");
                e.Use();
            }

            if (rightAlignedStyle == null)
            {
                rightAlignedStyle = new GUIStyle(EditorStyles.miniLabel);
                rightAlignedStyle.alignment = TextAnchor.UpperRight;
            }

            Rect nameRect = new Rect(
                rowRect.x + LABEL_PADDING_X,
                rowRect.y + LABEL_PADDING_Y,
                rowRect.width - NAME_WIDTH_RESERVE,
                rowRect.height
            );

            Rect typeRect = new Rect(
                rowRect.xMax - TYPE_RIGHT_OFFSET,
                rowRect.y + LABEL_PADDING_Y,
                TYPE_WIDTH,
                rowRect.height
            );

            GUI.Label(nameRect, parameters[i].name);
            GUI.Label(typeRect, parameters[i].type.ToString(), rightAlignedStyle);
        }

        GUILayout.EndScrollView();
    }

    private void PerformDragDrop(int dropIndex)
    {
        var dragData = DragAndDrop.GetGenericData("DragItemIndices") as List<int>;
        if (dragData == null) return;

        dragData.Sort();

        var list = currentController.parameters.ToList();
        var itemsToMove = new List<AnimatorControllerParameter>();

        foreach (int index in dragData)
        {
            itemsToMove.Add(list[index]);
        }

        int adjustedDropIndex = dropIndex;
        foreach (int index in dragData)
        {
            if (index < dropIndex) adjustedDropIndex--;
        }

        for (int i = dragData.Count - 1; i >= 0; i--)
        {
            list.RemoveAt(dragData[i]);
        }

        list.InsertRange(adjustedDropIndex, itemsToMove);

        selectedIndices.Clear();
        for (int i = 0; i < itemsToMove.Count; i++)
        {
            selectedIndices.Add(adjustedDropIndex + i);
        }
        lastSelectedIndex = selectedIndices.Last();

        ApplyNewParameters(list.ToArray());
    }

    private void SortParameters(bool reverseOrder)
    {
        var list = currentController.parameters.ToList();

        list.Sort((a, b) =>
        {
            if (enablePinnedOptions)
            {
                bool aPinned = pinnedParams.Contains(a.name);
                bool bPinned = pinnedParams.Contains(b.name);

                if (aPinned && !bPinned) return reverseOrder ? 1 : -1;
                if (!aPinned && bPinned) return reverseOrder ? -1 : 1;
            }

            int comparison = a.name.CompareTo(b.name);
            return reverseOrder ? -comparison : comparison;
        });

        ApplyNewParameters(list.ToArray());
    }

    private void GroupByPath()
    {
        var oldParams = currentController.parameters.ToList();
        var groupedList = new List<AnimatorControllerParameter>();
        var processedRoots = new HashSet<string>();

        foreach (var p in oldParams)
        {
            string rootFolder = GetRootFolder(p.name);

            // If we haven't processed this main category yet
            if (!processedRoots.Contains(rootFolder))
            {
                processedRoots.Add(rootFolder);

                // Grab every parameter that shares this root folder
                var itemsInGroup = oldParams.Where(x => GetRootFolder(x.name) == rootFolder).ToList();

                // If this is a folder group (contains slashes), sort it alphabetically internally
                if (itemsInGroup.Any(x => x.name.Contains("/")))
                {
                    itemsInGroup = itemsInGroup.OrderBy(x => x.name).ToList();
                }

                // Add the organized chunk to our final list
                groupedList.AddRange(itemsInGroup);
            }
        }

        ApplyNewParameters(groupedList.ToArray());
    }

    private string GetRootFolder(string paramName)
    {
        int firstSlash = paramName.IndexOf('/');
        if (firstSlash >= 0)
        {
            // Returns just the very first part of the path
            return paramName.Substring(0, firstSlash);
        }

        // Items with no slashes are treated as their own unique root so they don't get clustered
        return paramName;
    }

    private void ApplyNewParameters(AnimatorControllerParameter[] newParams)
    {
        Undo.RecordObject(currentController, "Organize Animator Parameters");
        currentController.parameters = newParams;
        EditorUtility.SetDirty(currentController);
        Repaint();
    }

    private void ClearSelection()
    {
        selectedIndices.Clear();
        lastSelectedIndex = -1;
    }
}

#endif