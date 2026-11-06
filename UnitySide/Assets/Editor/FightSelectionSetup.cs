using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class FightSelectionSetup
{
    [MenuItem("FightEngineering/Setup/Repair Selection Layout")]
    public static void Repair()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("編集画面に反映する場合はPlayを停止してください。");
            return;
        }
        FightGameUI ui = Object.FindFirstObjectByType<FightGameUI>(FindObjectsInactive.Include);
        if (ui == null)
        {
            Debug.LogError("FightGameUIのある戦闘シーンを開いてください。");
            return;
        }
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Repair selection layout");
        try
        {
            ApplyTo(ui);
            Undo.CollapseUndoOperations(group);
            Debug.Log("選択画面のレイアウトを整理しました。シーンを保存してください。", ui);
        }
        catch (System.Exception exception)
        {
            Undo.RevertAllDownToGroup(group);
            Debug.LogException(exception);
        }
    }

    public static void ApplyTo(FightGameUI ui)
    {
        SerializedObject data = new SerializedObject(ui);
        SerializedProperty setup = data.FindProperty("view").FindPropertyRelative("setup");
        GameObject root = setup.objectReferenceValue as GameObject;
        if (root == null) return;
        Undo.RegisterFullObjectHierarchyUndo(root, "Arrange selection screen");
        Undo.RecordObject(ui, "Update selection references");
        CanvasScaler scaler = root.GetComponentInParent<CanvasScaler>();
        if (scaler != null) Undo.RecordObject(scaler, "Scale selection screen");
        ui.ApplySelectionLayout(created => Undo.RegisterCreatedObjectUndo(created, "Create selection layout"), (child, parent) => Undo.SetTransformParent(child, parent, "Move selection control"));
        EditorUtility.SetDirty(ui);
        EditorSceneManager.MarkSceneDirty(ui.gameObject.scene);
    }
}
