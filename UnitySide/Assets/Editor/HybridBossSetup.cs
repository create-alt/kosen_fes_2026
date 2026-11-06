using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class HybridBossSetup
{
    [MenuItem("FightEngineering/Setup/Add Hybrid Overdrive")]
    private static void Install()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("Playを停止してからHYBRID OVERDRIVEを追加してください。");
            return;
        }
        EnemySelectionController selection = UnityEngine.Object.FindFirstObjectByType<EnemySelectionController>(FindObjectsInactive.Include);
        FightGameUI ui = UnityEngine.Object.FindFirstObjectByType<FightGameUI>(FindObjectsInactive.Include);
        if (selection == null || ui == null)
        {
            Debug.LogError("EnemySelectionControllerとFightGameUIのある戦闘シーンを開いてください。");
            return;
        }
        for (int index = 0; index < selection.EnemyCount; index++)
        {
            GameObject existing = selection.GetEnemy(index);
            if (existing == null || existing.GetComponentInChildren<HybridBossAttack>(true) == null) continue;
            FightSelectionSetup.Repair();
            Selection.activeGameObject = existing;
            Debug.Log("HYBRID OVERDRIVEは既に選択画面へ登録されています。", existing);
            return;
        }

        SerializedObject selectionData = new SerializedObject(selection);
        SerializedObject uiData = new SerializedObject(ui);
        SerializedProperty choices = selectionData.FindProperty("enemyChoices");
        SerializedProperty view = uiData.FindProperty("view");
        SerializedProperty cards = view.FindPropertyRelative("enemyCards");
        SerializedProperty faces = view.FindPropertyRelative("enemyFaces");
        SerializedProperty marks = view.FindPropertyRelative("selectedMarks");
        PlayerController player = selectionData.FindProperty("player").objectReferenceValue as PlayerController;
        int sourceIndex = -1;
        for (int index = 0; index < selection.EnemyCount; index++)
        {
            GameObject candidate = selection.GetEnemy(index);
            if (candidate != null && candidate.GetComponentInChildren<DilatantEnemy>(true) != null) sourceIndex = index;
        }
        if (sourceIndex < 0 || player == null || cards.arraySize != choices.arraySize || faces.arraySize != cards.arraySize || marks.arraySize != cards.arraySize)
        {
            Debug.LogError("既存の液体の敵、Player、選択カードの登録を確認してください。", selection);
            return;
        }
        GameObject sourceEnemy = selection.GetEnemy(sourceIndex);
        Button sourceCard = cards.GetArrayElementAtIndex(sourceIndex).objectReferenceValue as Button;
        Image sourceFace = faces.GetArrayElementAtIndex(sourceIndex).objectReferenceValue as Image;
        GameObject sourceMark = marks.GetArrayElementAtIndex(sourceIndex).objectReferenceValue as GameObject;
        PunchBotContoroller punchBot = UnityEngine.Object.FindFirstObjectByType<PunchBotContoroller>(FindObjectsInactive.Include);
        GameObject missilePrefab = punchBot != null ? new SerializedObject(punchBot).FindProperty("missilePrefab").objectReferenceValue as GameObject : null;
        if (sourceCard == null || sourceFace == null || sourceMark == null || missilePrefab == null || sourceEnemy.GetComponentInChildren<DilatantLiquidAttack>(true) == null)
        {
            Debug.LogError("液体の敵の攻撃、選択カード、PunchBotのミサイルPrefabを設定してください。");
            return;
        }
        if (!sourceFace.transform.IsChildOf(sourceCard.transform) || !sourceMark.transform.IsChildOf(sourceCard.transform))
        {
            Debug.LogError("選択カードのImageとSelected Markはカード内に配置してください。", sourceCard);
            return;
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Add Hybrid Overdrive");
        try
        {
            GameObject bossRoot = UnityEngine.Object.Instantiate(sourceEnemy, sourceEnemy.transform.parent);
            bossRoot.name = "HybridOverdrive";
            bossRoot.SetActive(false);
            Undo.RegisterCreatedObjectUndo(bossRoot, "Create hybrid enemy");
            DilatantEnemy actor = bossRoot.GetComponentInChildren<DilatantEnemy>(true);
            HybridBossAttack boss = Undo.AddComponent<HybridBossAttack>(actor.gameObject);
            SerializedObject bossData = new SerializedObject(boss);
            bossData.FindProperty("player").objectReferenceValue = player;
            bossData.FindProperty("missilePrefab").objectReferenceValue = missilePrefab;
            bossData.ApplyModifiedProperties();
            DilatantLiquidAttack liquid = actor.GetComponent<DilatantLiquidAttack>();
            SerializedObject liquidData = new SerializedObject(liquid);
            liquidData.FindProperty("player").objectReferenceValue = player;
            liquidData.FindProperty("externalControl").boolValue = true;
            liquidData.FindProperty("showPrediction").boolValue = true;
            liquidData.ApplyModifiedProperties();
            liquid.enabled = true;

            GameObject cardRoot = UnityEngine.Object.Instantiate(sourceCard.gameObject, sourceCard.transform.parent);
            cardRoot.name = "HybridOverdriveCard";
            Undo.RegisterCreatedObjectUndo(cardRoot, "Create hybrid selection card");
            Button card = cardRoot.GetComponent<Button>();
            card.onClick = new Button.ButtonClickedEvent();
            Image face = FindClone(sourceFace.transform, sourceCard.transform, cardRoot.transform).GetComponent<Image>();
            GameObject mark = FindClone(sourceMark.transform, sourceCard.transform, cardRoot.transform).gameObject;
            mark.SetActive(false);
            foreach (Text text in cardRoot.GetComponentsInChildren<Text>(true))
            {
                if (text.name == "EnemyName") text.text = "HYBRID\nOVERDRIVE";
                if (text.name == "Description") text.text = "高難易度\nしゃがみ波＋正面3～5連射\n体力低下で複合攻撃";
            }

            int addedIndex = choices.arraySize;
            choices.arraySize++;
            SerializedProperty choice = choices.GetArrayElementAtIndex(addedIndex);
            choice.FindPropertyRelative("enemy").objectReferenceValue = bossRoot;
            choice.FindPropertyRelative("selectButton").objectReferenceValue = card;
            selectionData.FindProperty("requireStartButton").boolValue = true;
            selectionData.ApplyModifiedProperties();
            cards.arraySize++;
            faces.arraySize++;
            marks.arraySize++;
            cards.GetArrayElementAtIndex(addedIndex).objectReferenceValue = card;
            faces.GetArrayElementAtIndex(addedIndex).objectReferenceValue = face;
            marks.GetArrayElementAtIndex(addedIndex).objectReferenceValue = mark;
            uiData.ApplyModifiedProperties();
            FightSelectionSetup.ApplyTo(ui);
            EditorSceneManager.MarkSceneDirty(selection.gameObject.scene);
            Selection.activeGameObject = bossRoot;
            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log("HYBRID OVERDRIVEと選択カードを追加しました。シーンを保存してください。", bossRoot);
        }
        catch (Exception exception)
        {
            Undo.RevertAllDownToGroup(undoGroup);
            Debug.LogException(exception);
        }
    }

    private static Transform FindClone(Transform original, Transform sourceRoot, Transform cloneRoot)
    {
        string path = AnimationUtility.CalculateTransformPath(original, sourceRoot);
        return string.IsNullOrEmpty(path) ? cloneRoot : cloneRoot.Find(path);
    }
}
