using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class FightUIInstaller
{
    private const string RequestPath = "Temp/CodexGameUI.request";
    private const string ReceiptPath = "Library/CodexGameUI.receipt.json";
    private static bool running;

    [Serializable]
    private sealed class Receipt
    {
        public string status;
        public string scene;
        public string backup;
        public string message;
        public string time;
    }

    static FightUIInstaller()
    {
        EditorApplication.delayCall += TryApply;
    }

    public static void ApplyFromBatch()
    {
        Apply("Assets/Scenes/SampleScene.unity");
    }

    private static void TryApply()
    {
        if (running || !File.Exists(RequestPath)) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += TryApply;
            return;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.isPlaying = false;
            EditorApplication.delayCall += TryApply;
            return;
        }
        Apply("Assets/Scenes/SampleScene.unity");
    }

    private static void Apply(string scenePath)
    {
        if (running) return;
        running = true;
        Scene previous = SceneManager.GetActiveScene();
        Scene scene = SceneManager.GetSceneByPath(scenePath);
        bool opened = !scene.IsValid() || !scene.isLoaded;
        string backup = "";
        try
        {
            if (opened) scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            if (!AssetDatabase.IsValidFolder("Assets/SceneBackups")) AssetDatabase.CreateFolder("Assets", "SceneBackups");
            backup = "Assets/SceneBackups/SampleScene_BeforeUI_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".unity";
            if (!EditorSceneManager.SaveScene(scene, backup, true)) throw new Exception("Could not save scene backup.");
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Improve Fight Engineering game UI");
            foreach (GameObject root in scene.GetRootGameObjects()) Undo.RegisterFullObjectHierarchyUndo(root, "Improve Fight Engineering game UI");
            FightUIBuilder.Build(scene);
            if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new Exception("Could not save UI scene.");
            if (File.Exists(RequestPath)) File.Delete(RequestPath);
            File.WriteAllText(ReceiptPath, JsonUtility.ToJson(new Receipt { status = "applied", scene = scenePath, backup = backup, message = "Selection, settings, three HUDs, result screen, prediction templates and audio listener configured.", time = DateTime.Now.ToString("O") }, true));
            Debug.Log("Fight Engineering UI installed. Backup: " + backup);
            if (opened && previous.IsValid() && previous.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
                SceneManager.SetActiveScene(previous);
            }
        }
        catch (Exception exception)
        {
            File.WriteAllText(ReceiptPath, JsonUtility.ToJson(new Receipt { status = "failed", scene = scenePath, backup = backup, message = exception.ToString(), time = DateTime.Now.ToString("O") }, true));
            Debug.LogException(exception);
        }
        finally
        {
            running = false;
        }
    }
}
