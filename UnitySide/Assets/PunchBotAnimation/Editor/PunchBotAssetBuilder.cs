using System;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;

public static class PunchBotAssetBuilder
{
    [Serializable]
    private sealed class RobotData
    {
        public NodeData[] nodes;
        public PartData[] parts;
        public MaterialData[] materials;
        public ClipData[] clips;
    }

    [Serializable]
    private sealed class NodeData
    {
        public string path;
        public float[] position;
    }

    [Serializable]
    private sealed class PartData
    {
        public string name;
        public string parent;
        public int material;
        public float[] vertices;
        public int[] triangles;
    }

    [Serializable]
    private sealed class MaterialData
    {
        public string name;
        public float[] color;
    }

    [Serializable]
    private sealed class ClipData
    {
        public string name;
        public TrackData[] tracks;
    }

    [Serializable]
    private sealed class TrackData
    {
        public string path;
        public float[] times;
        public float[] positions;
        public float[] rotations;
    }

    [MenuItem("Tools/PunchBot/Create Animated Prefab")]
    public static void Build()
    {
        string source = FindSource();
        RobotData data = JsonUtility.FromJson<RobotData>(File.ReadAllText(source));
        string folder = AssetDatabase.GenerateUniqueAssetPath("Assets/PunchBotGenerated");
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
        GameObject root = new GameObject("PunchBot");

        try
        {
            foreach (NodeData node in data.nodes)
            {
                int separator = node.path.LastIndexOf('/');
                string name = separator < 0 ? node.path : node.path.Substring(separator + 1);
                Transform parent = separator < 0 ? root.transform : root.transform.Find(node.path.Substring(0, separator));
                GameObject child = new GameObject(name);
                child.transform.SetParent(parent, false);
                child.transform.localPosition = Vector(node.position, 0);
            }

            Material[] materials = BuildMaterials(data.materials, folder);
            foreach (PartData part in data.parts)
            {
                Vector3[] vertices = new Vector3[part.vertices.Length / 3];
                for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector(part.vertices, i * 3);
                Mesh mesh = new Mesh { name = part.name, vertices = vertices, triangles = part.triangles };
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                AssetDatabase.CreateAsset(mesh, folder + "/" + part.name + ".asset");
                GameObject child = new GameObject(part.name);
                child.transform.SetParent(root.transform.Find(part.parent), false);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                child.AddComponent<MeshRenderer>().sharedMaterial = materials[part.material];
            }

            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/PunchBot.controller");
            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            AnimatorState idle = null;
            AnimatorState hit = null;
            foreach (ClipData clipData in data.clips)
            {
                AnimationClip clip = BuildClip(clipData);
                AssetDatabase.CreateAsset(clip, folder + "/" + clip.name + ".anim");
                AnimatorState state = machine.AddState(clip.name);
                state.motion = clip;
                state.writeDefaultValues = false;
                if (clip.name == "Idle") idle = state;
                if (clip.name == "Hit") hit = state;
            }
            machine.defaultState = idle;
            AnimatorStateTransition recover = hit.AddTransition(idle);
            recover.hasExitTime = true;
            recover.exitTime = 1f;
            recover.hasFixedDuration = true;
            recover.duration = 0f;
            Animator animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            root.AddComponent<PunchBotMotion>();
            string prefabPath = folder + "/PunchBot.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
            Debug.Log("PunchBot created: " + prefabPath);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static string FindSource()
    {
        foreach (string guid in AssetDatabase.FindAssets("PunchBotAnimationData"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileName(path) == "PunchBotAnimationData.json") return path;
        }
        throw new FileNotFoundException("PunchBotAnimationData.json was not found in Assets.");
    }

    private static Material[] BuildMaterials(MaterialData[] data, string folder)
    {
        string pipeline = GraphicsSettings.currentRenderPipeline == null ? "" : GraphicsSettings.currentRenderPipeline.GetType().Name;
        string shaderName = pipeline.Contains("HDRenderPipeline") ? "HDRP/Lit" : (pipeline.Length == 0 ? "Standard" : "Universal Render Pipeline/Lit");
        Shader shader = Shader.Find(shaderName);
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) throw new InvalidOperationException("No supported material shader was found.");
        Material[] result = new Material[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            Material material = new Material(shader) { name = data[i].name };
            Color color = new Color(data[i].color[0], data[i].color[1], data[i].color[2], 1f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.25f);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.25f);
            AssetDatabase.CreateAsset(material, folder + "/" + material.name + ".mat");
            result[i] = material;
        }
        return result;
    }

    private static AnimationClip BuildClip(ClipData data)
    {
        AnimationClip clip = new AnimationClip { name = data.name, frameRate = 30f };
        foreach (TrackData track in data.tracks)
        {
            string[] axes = { "x", "y", "z", "w" };
            for (int axis = 0; axis < 3; axis++) SetCurve(clip, track, "m_LocalPosition." + axes[axis], track.positions, 3, axis);
            for (int axis = 0; axis < 4; axis++) SetCurve(clip, track, "m_LocalRotation." + axes[axis], track.rotations, 4, axis);
        }
        clip.EnsureQuaternionContinuity();
        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = data.name == "Idle";
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        return clip;
    }

    private static void SetCurve(AnimationClip clip, TrackData track, string property, float[] values, int stride, int component)
    {
        Keyframe[] keys = new Keyframe[track.times.Length];
        for (int i = 0; i < keys.Length; i++) keys[i] = new Keyframe(track.times[i], values[i * stride + component]);
        AnimationCurve curve = new AnimationCurve(keys);
        for (int i = 0; i < keys.Length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
        }
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(track.path, typeof(Transform), property), curve);
    }

    private static Vector3 Vector(float[] values, int index)
    {
        return new Vector3(values[index], values[index + 1], values[index + 2]);
    }
}
