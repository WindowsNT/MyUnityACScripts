using UnityEngine;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
using AC;

/// <summary>
/// Attach to any GameObject in the scene (e.g. "Editor Helpers").
/// Filters which Hotspot gizmos are visible in the Scene view based on
/// a chosen AC camera.
///
/// Modes:
///   None   - hide every hotspot
///   All    - show every hotspot
///   Manual - show hotspots limited to a camera you pick from the inspector
///   Auto   - automatically follow whichever AC camera is selected in the
///            Hierarchy / Scene. Selecting a non-camera (e.g. a hotspot you
///            want to edit) keeps the LAST selected camera's hotspots visible.
///
/// In every mode, hotspots with no "Limit to Camera" set are always shown.
/// </summary>
[ExecuteInEditMode]
public class HotspotCameraGizmoFilter : MonoBehaviour
{
    public enum FilterMode { None, All, Manual, Auto }

    [Tooltip("How hotspots are filtered in the Scene view.")]
    public FilterMode mode = FilterMode.Auto;

    [Tooltip("Used when Mode = Manual. The camera whose hotspots to show.")]
    public _Camera previewCamera;

    [Tooltip("Also hide the Hotspot GameObjects in the Hierarchy.")]
    public bool hideInHierarchy = false;

    // The camera tracked while in Auto mode (the last camera that was selected).
    [SerializeField] private _Camera _autoCamera;

    private readonly List<Hotspot> _managed = new List<Hotspot>();

    void OnEnable()
    {
        EditorApplication.update += Refresh;
    }

    void OnDisable()
    {
        EditorApplication.update -= Refresh;
        // Restore all hotspots to visible when script is disabled
        foreach (var h in _managed)
            if (h != null) SetVisible(h, true);
    }

    void Refresh()
    {
        if (Application.isPlaying) return;

        // In Auto mode, follow whatever camera is currently selected.
        // If the selection is NOT a camera, keep the last one so you can
        // still click & edit the hotspots without them disappearing.
        if (mode == FilterMode.Auto)
        {
            _Camera sel = GetSelectedCamera();
            if (sel != null) _autoCamera = sel;
        }

        // Resolve the single camera we filter against (null for None / All).
        _Camera target = null;
        switch (mode)
        {
            case FilterMode.Manual: target = previewCamera; break;
            case FilterMode.Auto:   target = _autoCamera;   break;
        }

        Hotspot[] all = FindObjectsByType<Hotspot>(FindObjectsSortMode.None);
        _managed.Clear();

        foreach (var h in all)
        {
            _managed.Add(h);

            if (mode == FilterMode.All)  { SetVisible(h, true);  continue; }
            if (mode == FilterMode.None) { SetVisible(h, false); continue; }

            // Manual / Auto with no camera resolved yet -> hide everything.
            if (target == null) { SetVisible(h, false); continue; }

            // Show hotspots limited to this camera, plus any with no limit set.
            _Camera limitCam = h.limitToCamera;
            bool belongs = (limitCam == null || limitCam == target);
            SetVisible(h, belongs);
        }
    }

    /// <summary>
    /// Returns a _Camera if the current Unity selection is (or lives under) one.
    /// </summary>
    static _Camera GetSelectedCamera()
    {
        GameObject go = Selection.activeGameObject;
        if (go == null) return null;

        _Camera cam = go.GetComponent<_Camera>();
        if (cam == null) cam = go.GetComponentInParent<_Camera>();
        return cam;
    }

    void SetVisible(Hotspot h, bool visible)
    {
        var svm = SceneVisibilityManager.instance;
        if (visible)
        {
            svm.Show(h.gameObject, false);
            svm.EnablePicking(h.gameObject, false);
        }
        else
        {
            svm.Hide(h.gameObject, false);
            svm.DisablePicking(h.gameObject, false);
        }

        if (hideInHierarchy)
        {
            h.gameObject.hideFlags = visible
                ? HideFlags.None
                : HideFlags.HideInHierarchy;
        }
    }
}

[CustomEditor(typeof(HotspotCameraGizmoFilter))]
public class HotspotCameraGizmoFilterEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var s = (HotspotCameraGizmoFilter)target;

        DrawDefaultInspector();
        EditorGUILayout.Space();

        // --- Mode buttons -------------------------------------------------
        EditorGUILayout.LabelField("Mode", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        ModeButton(s, HotspotCameraGizmoFilter.FilterMode.Auto, "Auto (selected cam)");
        ModeButton(s, HotspotCameraGizmoFilter.FilterMode.None, "Show None");
        ModeButton(s, HotspotCameraGizmoFilter.FilterMode.All,  "Show All");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        // --- Manual camera picker ----------------------------------------
        _Camera[] cams = FindObjectsByType<_Camera>(FindObjectsSortMode.None);
        if (cams.Length == 0)
        {
            EditorGUILayout.HelpBox("No AC cameras found in scene.", MessageType.Info);
            return;
        }

        EditorGUILayout.LabelField("Manual — pick a camera", EditorStyles.boldLabel);
        foreach (var cam in cams)
        {
            bool isActive = (s.mode == HotspotCameraGizmoFilter.FilterMode.Manual
                             && s.previewCamera == cam);
            GUI.backgroundColor = isActive ? new Color(0.4f, 1f, 0.4f) : Color.white;
            if (GUILayout.Button(cam.gameObject.name, GUILayout.Height(24)))
            {
                Undo.RecordObject(s, "Switch Preview Camera");
                s.mode = HotspotCameraGizmoFilter.FilterMode.Manual;
                s.previewCamera = cam;
                EditorUtility.SetDirty(s);
            }
            GUI.backgroundColor = Color.white;
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Auto: select a camera in the Hierarchy/Scene and its hotspots appear " +
            "automatically. Selecting a hotspot (or anything that isn't a camera) " +
            "keeps the last camera's hotspots visible so you can edit them.\n\n" +
            "Green = active mode / camera.\n" +
            "Hotspots with no 'Limit to Camera' set are always shown.",
            MessageType.None);
    }

    static void ModeButton(HotspotCameraGizmoFilter s,
                           HotspotCameraGizmoFilter.FilterMode mode,
                           string label)
    {
        bool isActive = s.mode == mode;
        GUI.backgroundColor = isActive ? new Color(0.4f, 1f, 0.4f) : Color.white;
        if (GUILayout.Button(label, GUILayout.Height(24)))
        {
            Undo.RecordObject(s, "Set Filter Mode");
            s.mode = mode;
            EditorUtility.SetDirty(s);
        }
        GUI.backgroundColor = Color.white;
    }
}
#endif