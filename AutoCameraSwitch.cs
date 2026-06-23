#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using AC;

[InitializeOnLoad]
public static class Auto25DBackgroundOnSelect
{
    static Auto25DBackgroundOnSelect()
    {
        Selection.selectionChanged += OnSelectionChanged;
    }

    static void OnSelectionChanged()
    {
        if (Application.isPlaying) return;

        GameObject go = Selection.activeGameObject;
        if (go == null) return;

        GameCamera25D cam = go.GetComponent<GameCamera25D>();
        if (cam == null || cam.backgroundImage == null) return;

        cam.SetActiveBackground();

        // Προαιρετικά, για να ανανεωθεί άμεσα η εικόνα στο Game view:
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }
}
#endif