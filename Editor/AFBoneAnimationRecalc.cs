#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Rewrites transform animations after bones were moved in the source model (e.g. bones moved
/// in Blender edit mode and re-exported), so the mesh deforms exactly as it did with the old rest pose.
///
/// Skinning only cares about how far a bone is from its bind pose: deformation = World(anim) * World(rest)^-1.
/// For every animated bone and every key time, that deformation is computed on the OLD rest pose and the
/// local TRS that produces the same deformation on the NEW rest pose is solved for (parents first).
///
/// </summary>
public class AFBoneAnimationRecalc : EditorWindow
{
    public const string Name = "Bone Animation Recalc";

    [Serializable]
    public class BoneOverride
    {
        public string path = "";
        public bool foldout = true;

        public Vector3 oldPosition;
        public bool oldUseRotation;
        public Vector3 oldRotation;
        public bool oldUseScale;
        public Vector3 oldScale = Vector3.one;

        public bool overrideNew;
        public Vector3 newPosition;
        public bool newUseRotation;
        public Vector3 newRotation;
        public bool newUseScale;
        public Vector3 newScale = Vector3.one;
    }

    private struct TRS
    {
        public Vector3 p;
        public Quaternion r;
        public Vector3 s;

        public static readonly TRS Identity = new TRS { p = Vector3.zero, r = Quaternion.identity, s = Vector3.one };
        public Matrix4x4 M => Matrix4x4.TRS(p, r, s);
    }

    private enum RotKind { None, Quaternion, Euler }

    private class PathResult
    {
        public string path;
        public bool include = true;
        public bool hadPos, hadScale;
        public RotKind rotKind;
        public string eulerPrefix;
        public bool writePos, writeRot, writeScale;
        public float maxShear;
        public readonly List<TRS> oldLocals = new List<TRS>();
        public readonly List<TRS> newLocals = new List<TRS>();
        public bool Changed => writePos || writeRot || writeScale;
    }

    private class ClipResult
    {
        public AnimationClip clip;
        public bool foldout = true;
        public List<float> times = new List<float>();
        public readonly List<PathResult> paths = new List<PathResult>();
        public readonly List<string> warnings = new List<string>();
    }

    private const float PosEpsilon = 1e-6f;
    private const float ScaleEpsilon = 1e-6f;
    private const float RotEpsilonDeg = 1e-3f;

    [SerializeField] private GameObject newRig;
    [SerializeField] private GameObject oldRig;
    [SerializeField] private List<BoneOverride> overrides = new List<BoneOverride>();
    [SerializeField] private List<AnimationClip> clips = new List<AnimationClip>();
    [SerializeField] private string outputFolder = "";
    [SerializeField] private string suffix = " (Recalc)";

    private List<ClipResult> results;
    private Vector2 scroll;

    [MenuItem("TohruTheDragon/Avatar Fork/" + Name)]
    public static void ShowWindow() => GetWindow<AFBoneAnimationRecalc>($"AF {Name}");

    // ------------------------------------------------------------------ UI

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "Keeps the mesh deformation of transform animations identical after bones were moved in the model.\n" +
            "1. New Rig: the avatar root (or model asset) with the NEW bone positions. Clip paths are relative to it.\n" +
            "2. Old rest pose: drop the old avatar/model as Old Rig, and/or enter old local values below (Inspector values).\n" +
            "3. Add clips, Analyze, review, Write New Clips.",
            MessageType.Info);

        DrawRigs();
        EditorGUILayout.Space();
        DrawOverrides();
        EditorGUILayout.Space();
        DrawClips();
        EditorGUILayout.Space();
        DrawOutput();
        EditorGUILayout.Space();
        DrawResults();

        EditorGUILayout.EndScrollView();
    }

    private void DrawRigs()
    {
        EditorGUILayout.LabelField("Rigs", EditorStyles.boldLabel);
        using (new EditorGUI.ChangeCheckScope())
        {
            newRig = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("New Rig (root)", "Avatar root / model with the NEW bone rest positions"),
                newRig, typeof(GameObject), true);
            oldRig = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Old Rig (optional)", "Avatar root / model with the OLD bone rest positions. " +
                               "Leave empty to use the New Rig plus the old values entered below"),
                oldRig, typeof(GameObject), true);
        }
        if (oldRig == null)
            EditorGUILayout.HelpBox(
                "No Old Rig: the old rest pose is the New Rig with the Old values below applied. " +
                "Rotation/scale not entered are assumed unchanged.", MessageType.None);
        if ((newRig != null && newRig.scene.IsValid()) || (oldRig != null && oldRig.scene.IsValid()))
            EditorGUILayout.HelpBox(
                "Scene objects are read as they are now: make sure the Animation window is not previewing and " +
                "the avatar is in its rest pose (or drop the model/prefab asset instead).", MessageType.None);
    }

    private void DrawOverrides()
    {
        EditorGUILayout.LabelField("Bone Rest Values (local, as in the Inspector)", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Selected Transforms")) AddSelectedTransforms();
            if (GUILayout.Button("Add Empty")) overrides.Add(new BoneOverride());
        }

        for (int i = 0; i < overrides.Count; i++)
        {
            var o = overrides[i];
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    o.foldout = EditorGUILayout.Foldout(o.foldout, string.IsNullOrEmpty(o.path) ? "(no path)" : LeafName(o.path), true);
                    var picked = (Transform)EditorGUILayout.ObjectField(null, typeof(Transform), true, GUILayout.Width(120));
                    if (picked != null) SetOverrideFromTransform(o, picked, fillOld: false);
                    if (GUILayout.Button("X", GUILayout.Width(22)))
                    {
                        overrides.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }
                if (!o.foldout) continue;

                o.path = EditorGUILayout.TextField("Path", o.path);
                if (newRig != null && !string.IsNullOrEmpty(o.path) && newRig.transform.Find(o.path) == null)
                    EditorGUILayout.HelpBox("Path not found under the New Rig.", MessageType.Warning);

                EditorGUILayout.LabelField("Old", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                o.oldPosition = EditorGUILayout.Vector3Field("Position", o.oldPosition);
                o.oldUseRotation = EditorGUILayout.Toggle("Rotation", o.oldUseRotation);
                if (o.oldUseRotation) o.oldRotation = EditorGUILayout.Vector3Field(" ", o.oldRotation);
                o.oldUseScale = EditorGUILayout.Toggle("Scale", o.oldUseScale);
                if (o.oldUseScale) o.oldScale = EditorGUILayout.Vector3Field(" ", o.oldScale);
                EditorGUI.indentLevel--;

                o.overrideNew = EditorGUILayout.ToggleLeft("Override New values (otherwise read from New Rig)", o.overrideNew);
                if (o.overrideNew)
                {
                    EditorGUI.indentLevel++;
                    o.newPosition = EditorGUILayout.Vector3Field("Position", o.newPosition);
                    o.newUseRotation = EditorGUILayout.Toggle("Rotation", o.newUseRotation);
                    if (o.newUseRotation) o.newRotation = EditorGUILayout.Vector3Field(" ", o.newRotation);
                    o.newUseScale = EditorGUILayout.Toggle("Scale", o.newUseScale);
                    if (o.newUseScale) o.newScale = EditorGUILayout.Vector3Field(" ", o.newScale);
                    EditorGUI.indentLevel--;
                }
                else if (newRig != null && !string.IsNullOrEmpty(o.path))
                {
                    var t = newRig.transform.Find(o.path);
                    if (t != null)
                        EditorGUILayout.LabelField("New (from rig)", Fmt(t.localPosition), EditorStyles.miniLabel);
                }
            }
        }
    }

    private void DrawClips()
    {
        EditorGUILayout.LabelField("Animation Clips", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Selected Clips"))
            {
                foreach (var c in Selection.GetFiltered<AnimationClip>(SelectionMode.Assets))
                    if (!clips.Contains(c)) clips.Add(c);
            }
            if (GUILayout.Button("Clear")) clips.Clear();
        }
        var dropped = (AnimationClip)EditorGUILayout.ObjectField("Add Clip", null, typeof(AnimationClip), false);
        if (dropped != null && !clips.Contains(dropped)) clips.Add(dropped);

        for (int i = 0; i < clips.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                clips[i] = (AnimationClip)EditorGUILayout.ObjectField(clips[i], typeof(AnimationClip), false);
                if (GUILayout.Button("X", GUILayout.Width(22)))
                {
                    clips.RemoveAt(i);
                    GUIUtility.ExitGUI();
                }
            }
        }
    }

    private void DrawOutput()
    {
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Folder", string.IsNullOrEmpty(outputFolder) ? "(same folder as each source clip)" : outputFolder);
            if (GUILayout.Button("Choose", GUILayout.Width(60)))
            {
                string abs = EditorUtility.OpenFolderPanel("Output folder", "Assets", "");
                if (!string.IsNullOrEmpty(abs))
                {
                    string rel = ToAssetPath(abs);
                    if (rel == null) EditorUtility.DisplayDialog(Name, "Folder must be inside Assets.", "OK");
                    else outputFolder = rel;
                }
            }
            if (GUILayout.Button("Reset", GUILayout.Width(50))) outputFolder = "";
        }
        suffix = EditorGUILayout.TextField("Name Suffix", suffix);

        using (new EditorGUI.DisabledScope(newRig == null || clips.Count == 0))
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Analyze", GUILayout.Height(26))) Analyze();
            using (new EditorGUI.DisabledScope(results == null || !results.Any(r => r.paths.Any(p => p.include && p.Changed))))
            {
                if (GUILayout.Button("Write New Clips", GUILayout.Height(26))) WriteClips();
            }
        }
    }

    private void DrawResults()
    {
        if (results == null) return;
        EditorGUILayout.LabelField("Analysis", EditorStyles.boldLabel);
        foreach (var cr in results)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                int changed = cr.paths.Count(p => p.Changed);
                cr.foldout = EditorGUILayout.Foldout(cr.foldout,
                    $"{(cr.clip ? cr.clip.name : "(missing)")}  -  {changed} bone(s) to rewrite, {cr.times.Count} key time(s)", true);
                if (!cr.foldout) continue;
                foreach (var w in cr.warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
                foreach (var pr in cr.paths)
                {
                    if (!pr.Changed)
                    {
                        EditorGUILayout.LabelField(LeafName(pr.path), "unchanged", EditorStyles.miniLabel);
                        continue;
                    }
                    pr.include = EditorGUILayout.ToggleLeft(
                        $"{pr.path}   [{(pr.writePos ? "pos " : "")}{(pr.writeRot ? "rot " : "")}{(pr.writeScale ? "scale" : "")}]",
                        pr.include, EditorStyles.boldLabel);
                    if (!pr.include) continue;
                    EditorGUI.indentLevel++;
                    var o = pr.oldLocals[0];
                    var n = pr.newLocals[0];
                    string t0 = cr.times.Count > 0 ? $"t={cr.times[0]:0.###}: " : "";
                    if (pr.writePos) EditorGUILayout.LabelField($"{t0}Position", $"{Fmt(o.p)}  ->  {Fmt(n.p)}");
                    if (pr.writeRot) EditorGUILayout.LabelField($"{t0}Rotation", $"{Fmt(o.r.eulerAngles)}  ->  {Fmt(n.r.eulerAngles)}" +
                                                                                (pr.rotKind == RotKind.None ? "  (new curves)" : ""));
                    if (pr.writeScale) EditorGUILayout.LabelField($"{t0}Scale", $"{Fmt(o.s)}  ->  {Fmt(n.s)}" +
                                                                               (!pr.hadScale ? "  (new curves)" : ""));
                    if (pr.maxShear > 1e-4f)
                        EditorGUILayout.HelpBox(
                            $"Not exactly representable (shear {pr.maxShear:0.#####}): the bone's axes changed relative to a " +
                            "non-uniform scale. Result is the closest TRS.", MessageType.Warning);
                    EditorGUI.indentLevel--;
                }
            }
        }
    }

    // ------------------------------------------------------------------ Overrides helpers

    private void AddSelectedTransforms()
    {
        foreach (var t in Selection.transforms)
        {
            var o = new BoneOverride();
            if (!SetOverrideFromTransform(o, t, fillOld: true))
            {
                Debug.LogWarning($"{Name}: '{t.name}' is not under the New or Old Rig.");
                continue;
            }
            overrides.RemoveAll(x => x.path == o.path);
            overrides.Add(o);
        }
    }

    private bool SetOverrideFromTransform(BoneOverride o, Transform t, bool fillOld)
    {
        string path = null;
        if (newRig != null && t.IsChildOf(newRig.transform) && t != newRig.transform)
            path = AnimationUtility.CalculateTransformPath(t, newRig.transform);
        else if (oldRig != null && t.IsChildOf(oldRig.transform) && t != oldRig.transform)
            path = AnimationUtility.CalculateTransformPath(t, oldRig.transform);
        if (path == null) return false;
        o.path = path;
        if (fillOld)
        {
            // Pre-fill with the current values; edit them to the old ones.
            o.oldPosition = t.localPosition;
            o.oldRotation = t.localEulerAngles;
            o.oldScale = t.localScale;
        }
        return true;
    }

    // ------------------------------------------------------------------ Rest pose

    private BoneOverride FindOverride(string path) => overrides.FirstOrDefault(o => o.path == path);

    private bool TryGetRest(string path, bool old, out TRS trs)
    {
        trs = TRS.Identity;
        if (string.IsNullOrEmpty(path)) return true; // avatar root = reference frame

        GameObject rig = old && oldRig != null ? oldRig : newRig;
        Transform t = rig != null ? rig.transform.Find(path) : null;
        var ov = FindOverride(path);
        if (t == null && ov == null) return false;

        if (t != null) trs = new TRS { p = t.localPosition, r = t.localRotation, s = t.localScale };
        if (ov != null)
        {
            if (old)
            {
                trs.p = ov.oldPosition;
                if (ov.oldUseRotation) trs.r = Quaternion.Euler(ov.oldRotation);
                if (ov.oldUseScale) trs.s = ov.oldScale;
            }
            else if (ov.overrideNew)
            {
                trs.p = ov.newPosition;
                if (ov.newUseRotation) trs.r = Quaternion.Euler(ov.newRotation);
                if (ov.newUseScale) trs.s = ov.newScale;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ Analysis

    private static bool IsTransformProp(string prop) =>
        prop.StartsWith("m_LocalPosition.") || prop.StartsWith("m_LocalRotation.") ||
        prop.StartsWith("m_LocalScale.") || prop.StartsWith("localEulerAngles");

    private void Analyze()
    {
        results = new List<ClipResult>();
        foreach (var clip in clips.Where(c => c != null).Distinct())
            results.Add(AnalyzeClip(clip));
    }

    private ClipResult AnalyzeClip(AnimationClip clip)
    {
        var cr = new ClipResult { clip = clip };

        // path -> property -> curve
        var curves = new Dictionary<string, Dictionary<string, AnimationCurve>>();
        foreach (var b in AnimationUtility.GetCurveBindings(clip))
        {
            if (b.type != typeof(Transform) || !IsTransformProp(b.propertyName)) continue;
            if (!curves.TryGetValue(b.path, out var map)) curves[b.path] = map = new Dictionary<string, AnimationCurve>();
            map[b.propertyName] = AnimationUtility.GetEditorCurve(clip, b);
        }
        if (curves.Count == 0)
        {
            cr.warnings.Add("No transform curves in this clip; nothing to do.");
            return cr;
        }

        var times = new SortedSet<float>();
        foreach (var map in curves.Values)
            foreach (var c in map.Values)
                foreach (var k in c.keys) times.Add(k.time);
        cr.times = times.ToList();

        // Validate paths (animated bones and all their ancestors)
        var animatedPaths = curves.Keys.OrderBy(Depth).ToList();
        var needed = new HashSet<string>();
        foreach (var p in animatedPaths)
            for (string q = p; !string.IsNullOrEmpty(q); q = ParentPath(q)) needed.Add(q);
        var missing = new HashSet<string>();
        foreach (var p in needed)
        {
            if (!TryGetRest(p, true, out _) || !TryGetRest(p, false, out _)) missing.Add(p);
        }
        foreach (var m in missing) cr.warnings.Add($"Path not found in rig (skipped): {m}");

        // Rest world matrices
        var restOld = new Dictionary<string, Matrix4x4>();
        var restNew = new Dictionary<string, Matrix4x4>();
        Matrix4x4 RestWorld(string path, bool old)
        {
            if (string.IsNullOrEmpty(path)) return Matrix4x4.identity;
            var memo = old ? restOld : restNew;
            if (memo.TryGetValue(path, out var w)) return w;
            TryGetRest(path, old, out var trs);
            w = RestWorld(ParentPath(path), old) * trs.M;
            memo[path] = w;
            return w;
        }

        foreach (var path in animatedPaths)
        {
            if (missing.Any(m => path == m || path.StartsWith(m + "/"))) continue;
            var map = curves[path];
            var pr = new PathResult
            {
                path = path,
                hadPos = map.Keys.Any(k => k.StartsWith("m_LocalPosition.")),
                hadScale = map.Keys.Any(k => k.StartsWith("m_LocalScale.")),
            };
            if (map.Keys.Any(k => k.StartsWith("m_LocalRotation."))) pr.rotKind = RotKind.Quaternion;
            else
            {
                var e = map.Keys.FirstOrDefault(k => k.StartsWith("localEulerAngles"));
                if (e != null)
                {
                    pr.rotKind = RotKind.Euler;
                    pr.eulerPrefix = e.Substring(0, e.IndexOf('.'));
                }
            }
            cr.paths.Add(pr);
        }

        foreach (float t in cr.times)
        {
            var animOld = new Dictionary<string, Matrix4x4>();
            var animNew = new Dictionary<string, Matrix4x4>();
            var solved = new Dictionary<string, TRS>();

            Matrix4x4 OldAnimWorld(string path)
            {
                if (string.IsNullOrEmpty(path)) return Matrix4x4.identity;
                if (animOld.TryGetValue(path, out var w)) return w;
                TryGetRest(path, true, out var rest);
                var local = curves.TryGetValue(path, out var map) ? Sample(map, t, rest) : rest;
                w = OldAnimWorld(ParentPath(path)) * local.M;
                animOld[path] = w;
                return w;
            }

            Matrix4x4 NewAnimWorld(string path)
            {
                if (string.IsNullOrEmpty(path)) return Matrix4x4.identity;
                if (animNew.TryGetValue(path, out var w)) return w;
                TRS local;
                if (!solved.TryGetValue(path, out local)) TryGetRest(path, false, out local);
                w = NewAnimWorld(ParentPath(path)) * local.M;
                animNew[path] = w;
                return w;
            }

            foreach (var pr in cr.paths) // depth-sorted: parents are solved before children
            {
                // Deformation the skin sees on the OLD rest pose...
                Matrix4x4 deform = OldAnimWorld(pr.path) * RestWorld(pr.path, true).inverse;
                // ...reproduced on the NEW rest pose.
                Matrix4x4 targetWorld = deform * RestWorld(pr.path, false);
                Matrix4x4 local = NewAnimWorld(ParentPath(pr.path)).inverse * targetWorld;
                var trs = Decompose(local, out float shear);
                pr.maxShear = Mathf.Max(pr.maxShear, shear);
                solved[pr.path] = trs;

                TryGetRest(pr.path, true, out var restOldTrs);
                pr.oldLocals.Add(Sample(curves[pr.path], t, restOldTrs));
                pr.newLocals.Add(trs);
            }
        }

        // Decide what to write: animated channels when their values change, un-animated channels when the
        // solved value differs from the new rest value (the bone would otherwise sit at its rest value).
        foreach (var pr in cr.paths)
        {
            TryGetRest(pr.path, false, out var restNewTrs);
            for (int i = 0; i < pr.newLocals.Count; i++)
            {
                var o = pr.oldLocals[i];
                var n = pr.newLocals[i];
                var posRef = pr.hadPos ? o.p : restNewTrs.p;
                var rotRef = pr.rotKind != RotKind.None ? o.r : restNewTrs.r;
                var sclRef = pr.hadScale ? o.s : restNewTrs.s;
                if (MaxAbs(n.p - posRef) > PosEpsilon) pr.writePos = true;
                if (Quaternion.Angle(n.r, rotRef) > RotEpsilonDeg) pr.writeRot = true;
                if (MaxAbs(n.s - sclRef) > ScaleEpsilon) pr.writeScale = true;
            }
        }

        if (!cr.paths.Any(p => p.Changed))
            cr.warnings.Add("Old and new rest poses give identical results for this clip; nothing to rewrite.");
        if (cr.times.Count > 1)
            cr.warnings.Add("Clip has several key times: values are exact at keys; in-between interpolation is " +
                            "re-derived (tangent modes are copied where keys line up).");
        return cr;
    }

    private static TRS Sample(Dictionary<string, AnimationCurve> map, float t, TRS rest)
    {
        float Ev(string prop, float fallback) => map.TryGetValue(prop, out var c) && c != null ? c.Evaluate(t) : fallback;

        var r = rest;
        r.p = new Vector3(Ev("m_LocalPosition.x", rest.p.x), Ev("m_LocalPosition.y", rest.p.y), Ev("m_LocalPosition.z", rest.p.z));
        r.s = new Vector3(Ev("m_LocalScale.x", rest.s.x), Ev("m_LocalScale.y", rest.s.y), Ev("m_LocalScale.z", rest.s.z));

        if (map.Keys.Any(k => k.StartsWith("m_LocalRotation.")))
        {
            var q = new Quaternion(Ev("m_LocalRotation.x", rest.r.x), Ev("m_LocalRotation.y", rest.r.y),
                                   Ev("m_LocalRotation.z", rest.r.z), Ev("m_LocalRotation.w", rest.r.w));
            r.r = Normalize(q);
        }
        else
        {
            var e = map.Keys.FirstOrDefault(k => k.StartsWith("localEulerAngles"));
            if (e != null)
            {
                string pre = e.Substring(0, e.IndexOf('.'));
                var re = rest.r.eulerAngles;
                r.r = Quaternion.Euler(Ev(pre + ".x", re.x), Ev(pre + ".y", re.y), Ev(pre + ".z", re.z));
            }
        }
        return r;
    }

    private static TRS Decompose(Matrix4x4 m, out float shear)
    {
        Vector3 c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2);
        var s = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
        if (m.determinant < 0f) s.x = -s.x;
        var r = Quaternion.LookRotation(c2 / Mathf.Max(s.z, 1e-12f), c1 / Mathf.Max(s.y, 1e-12f));
        var trs = new TRS { p = m.GetColumn(3), r = r, s = s };

        var back = trs.M;
        shear = 0f;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                shear = Mathf.Max(shear, Mathf.Abs(back[row, col] - m[row, col]));
        return trs;
    }

    // ------------------------------------------------------------------ Writing

    private void WriteClips()
    {
        var written = new List<string>();
        try
        {
            AssetDatabase.StartAssetEditing();
            foreach (var cr in results)
            {
                if (cr.clip == null || !cr.paths.Any(p => p.include && p.Changed)) continue;
                string path = WriteClip(cr);
                if (path != null) written.Add(path);
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.SaveAssets();
        }

        if (written.Count > 0)
        {
            Selection.objects = written.Select(AssetDatabase.LoadAssetAtPath<AnimationClip>).Cast<UnityEngine.Object>().ToArray();
            Debug.Log($"{Name}: Wrote:\n" + string.Join("\n", written));
        }
        EditorUtility.DisplayDialog(Name, $"Wrote {written.Count} new clip(s).", "OK");
    }

    private string WriteClip(ClipResult cr)
    {
        var src = cr.clip;
        var copy = Instantiate(src);
        copy.name = src.name + suffix;

        var srcCurves = new Dictionary<(string, string), AnimationCurve>();
        foreach (var b in AnimationUtility.GetCurveBindings(src))
            if (b.type == typeof(Transform)) srcCurves[(b.path, b.propertyName)] = AnimationUtility.GetEditorCurve(src, b);

        foreach (var pr in cr.paths.Where(p => p.include && p.Changed))
        {
            var t = cr.times;
            if (pr.writePos || pr.hadPos)
            {
                SetCurve(copy, srcCurves, pr.path, "m_LocalPosition.x", t, pr.newLocals.Select(v => v.p.x));
                SetCurve(copy, srcCurves, pr.path, "m_LocalPosition.y", t, pr.newLocals.Select(v => v.p.y));
                SetCurve(copy, srcCurves, pr.path, "m_LocalPosition.z", t, pr.newLocals.Select(v => v.p.z));
            }
            if (pr.writeScale || pr.hadScale)
            {
                SetCurve(copy, srcCurves, pr.path, "m_LocalScale.x", t, pr.newLocals.Select(v => v.s.x));
                SetCurve(copy, srcCurves, pr.path, "m_LocalScale.y", t, pr.newLocals.Select(v => v.s.y));
                SetCurve(copy, srcCurves, pr.path, "m_LocalScale.z", t, pr.newLocals.Select(v => v.s.z));
            }
            if (pr.writeRot || pr.rotKind != RotKind.None)
            {
                if (pr.rotKind == RotKind.Quaternion)
                {
                    var qs = ContinuousQuats(pr.newLocals.Select(v => v.r));
                    SetCurve(copy, srcCurves, pr.path, "m_LocalRotation.x", t, qs.Select(q => q.x));
                    SetCurve(copy, srcCurves, pr.path, "m_LocalRotation.y", t, qs.Select(q => q.y));
                    SetCurve(copy, srcCurves, pr.path, "m_LocalRotation.z", t, qs.Select(q => q.z));
                    SetCurve(copy, srcCurves, pr.path, "m_LocalRotation.w", t, qs.Select(q => q.w));
                }
                else
                {
                    string pre = pr.rotKind == RotKind.Euler ? pr.eulerPrefix : "localEulerAnglesRaw";
                    Vector3 hint = pr.rotKind == RotKind.Euler ? pr.oldLocals[0].r.eulerAngles : Vector3.zero;
                    if (pr.rotKind == RotKind.Euler &&
                        srcCurves.TryGetValue((pr.path, pre + ".x"), out var hx) && hx.length > 0 &&
                        srcCurves.TryGetValue((pr.path, pre + ".y"), out var hy) && hy.length > 0 &&
                        srcCurves.TryGetValue((pr.path, pre + ".z"), out var hz) && hz.length > 0)
                        hint = new Vector3(hx.keys[0].value, hy.keys[0].value, hz.keys[0].value);
                    var es = ContinuousEulers(pr.newLocals.Select(v => v.r), hint);
                    SetCurve(copy, srcCurves, pr.path, pre + ".x", t, es.Select(e => e.x));
                    SetCurve(copy, srcCurves, pr.path, pre + ".y", t, es.Select(e => e.y));
                    SetCurve(copy, srcCurves, pr.path, pre + ".z", t, es.Select(e => e.z));
                }
            }
        }

        string folder = string.IsNullOrEmpty(outputFolder)
            ? Path.GetDirectoryName(AssetDatabase.GetAssetPath(src)).Replace('\\', '/')
            : outputFolder;
        string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{copy.name}.anim");
        AssetDatabase.CreateAsset(copy, assetPath);
        return assetPath;
    }

    private static void SetCurve(AnimationClip clip, Dictionary<(string, string), AnimationCurve> srcCurves,
                         string path, string prop, List<float> times, IEnumerable<float> values)
    {
        var vals = values.ToList();
        var curve = new AnimationCurve();
        for (int i = 0; i < times.Count; i++) curve.AddKey(new Keyframe(times[i], vals[i]));

        srcCurves.TryGetValue((path, prop), out var src);
        if (src == null)
        {
            // Same-channel sibling (e.g. .y when adding .x) gives the best tangent hint.
            string baseProp = prop.Substring(0, prop.LastIndexOf('.'));
            src = srcCurves.Where(kv => kv.Key.Item1 == path && kv.Key.Item2.StartsWith(baseProp + "."))
                           .Select(kv => kv.Value).FirstOrDefault();
        }

        for (int i = 0; i < curve.length; i++)
        {
            var left = AnimationUtility.TangentMode.ClampedAuto;
            var right = AnimationUtility.TangentMode.ClampedAuto;
            if (src != null)
            {
                for (int k = 0; k < src.length; k++)
                {
                    if (Mathf.Abs(src.keys[k].time - curve.keys[i].time) < 1e-5f)
                    {
                        left = AnimationUtility.GetKeyLeftTangentMode(src, k);
                        right = AnimationUtility.GetKeyRightTangentMode(src, k);
                        break;
                    }
                }
            }
            AnimationUtility.SetKeyLeftTangentMode(curve, i, left);
            AnimationUtility.SetKeyRightTangentMode(curve, i, right);
        }

        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), prop), curve);
    }

    private static List<Quaternion> ContinuousQuats(IEnumerable<Quaternion> qs)
    {
        var list = new List<Quaternion>();
        foreach (var q0 in qs)
        {
            var q = q0;
            if (list.Count > 0 && Quaternion.Dot(list[list.Count - 1], q) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            list.Add(q);
        }
        return list;
    }

    private static List<Vector3> ContinuousEulers(IEnumerable<Quaternion> qs, Vector3 hint)
    {
        var list = new List<Vector3>();
        Vector3 prev = hint;
        foreach (var q in qs)
        {
            var e = q.eulerAngles;
            e = new Vector3(Unwrap(e.x, prev.x), Unwrap(e.y, prev.y), Unwrap(e.z, prev.z));
            list.Add(e);
            prev = e;
        }
        return list;
    }

    private static float Unwrap(float a, float near)
    {
        while (a - near > 180f) a -= 360f;
        while (a - near < -180f) a += 360f;
        return a;
    }

    // ------------------------------------------------------------------ Utils

    private static Quaternion Normalize(Quaternion q)
    {
        float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return m < 1e-12f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
    }

    private static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));
    private static int Depth(string path) => string.IsNullOrEmpty(path) ? 0 : path.Count(c => c == '/') + 1;
    private static string ParentPath(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? "" : path.Substring(0, i);
    }
    private static string LeafName(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? path : path.Substring(i + 1);
    }
    private static string Fmt(Vector3 v) => $"({v.x:0.######}, {v.y:0.######}, {v.z:0.######})";

    private static string ToAssetPath(string abs)
    {
        string data = Application.dataPath.Replace('\\', '/');
        abs = abs.Replace('\\', '/');
        if (abs == data) return "Assets";
        return abs.StartsWith(data + "/") ? "Assets" + abs.Substring(data.Length) : null;
    }
}
#endif
