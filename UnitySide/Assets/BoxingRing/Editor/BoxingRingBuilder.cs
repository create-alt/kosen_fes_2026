using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BoxingRingBuilder
{
    private const float FloorHeight = 0.15f;
    private const float RopeHalfWidth = 2.5f;

    [MenuItem("Tools/Stage/Create Boxing Ring")]
    public static void Build()
    {
        string folder = AssetDatabase.GenerateUniqueAssetPath("Assets/BoxingRingGenerated");
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
        Shader shader = Shader.Find(GraphicsSettings.currentRenderPipeline == null ? "Standard" : "Universal Render Pipeline/Lit");
        if (shader == null) throw new InvalidOperationException("The ring material shader was not found.");

        Material mat = CreateMaterial(folder, shader, "CanvasBlue", new Color(0.12f, 0.28f, 0.39f), 0f, 0.12f);
        Material apron = CreateMaterial(folder, shader, "ApronNavy", new Color(0.025f, 0.045f, 0.075f), 0f, 0.2f);
        Material white = CreateMaterial(folder, shader, "WarmWhite", new Color(0.89f, 0.88f, 0.8f), 0f, 0.25f);
        Material red = CreateMaterial(folder, shader, "CornerRed", new Color(0.64f, 0.035f, 0.055f), 0f, 0.32f);
        Material blue = CreateMaterial(folder, shader, "CornerBlue", new Color(0.035f, 0.15f, 0.55f), 0f, 0.32f);
        Material steel = CreateMaterial(folder, shader, "Steel", new Color(0.17f, 0.2f, 0.24f), 0.7f, 0.55f);
        Material gold = CreateMaterial(folder, shader, "RingGold", new Color(0.76f, 0.5f, 0.17f), 0.25f, 0.35f);
        Material ground = CreateMaterial(folder, shader, "SurroundDark", new Color(0.035f, 0.045f, 0.06f), 0f, 0.08f);
        GameObject root = new GameObject("BoxingRing");

        try
        {
            Transform deck = Group(root.transform, "Platform");
            Box(deck, "PlatformBase", new Vector3(0f, -0.25f, 0f), new Vector3(6f, 0.56f, 6f), apron, true);
            Box(deck, "Canvas", new Vector3(0f, 0.09f, 0f), new Vector3(6f, 0.12f, 6f), mat, true);
            Box(root.transform, "Surround", new Vector3(0f, -0.67f, 0f), new Vector3(18f, 0.1f, 18f), ground, true);

            Transform markings = Group(root.transform, "MatMarkings");
            for (int side = -1; side <= 1; side += 2)
            {
                Box(markings, "SideLine", new Vector3(side * 2.25f, FloorHeight + 0.002f, 0f), new Vector3(0.035f, 0.003f, 4.5f), white);
                Box(markings, "EndLine", new Vector3(0f, FloorHeight + 0.002f, side * 2.25f), new Vector3(4.5f, 0.003f, 0.035f), white);
            }

            Mesh emblem = CreateCircleMesh(0.62f, 0.645f);
            AssetDatabase.CreateAsset(emblem, folder + "/CenterCircle.asset");
            MeshObject(markings, "CenterCircle", emblem, white, new Vector3(0f, FloorHeight + 0.004f, 0f), Quaternion.identity);
            Box(markings, "CenterStripe", new Vector3(0f, FloorHeight + 0.003f, 0f), new Vector3(0.055f, 0.003f, 0.65f), gold);
            Box(markings, "CenterStripeLeft", new Vector3(-0.13f, FloorHeight + 0.003f, 0f), new Vector3(0.055f, 0.003f, 0.36f), white);
            Box(markings, "CenterStripeRight", new Vector3(0.13f, FloorHeight + 0.003f, 0f), new Vector3(0.055f, 0.003f, 0.36f), white);

            Transform corners = Group(root.transform, "Corners");
            float[] ropeHeights = { 0.52f, 0.82f, 1.12f, 1.42f };
            for (int x = -1; x <= 1; x += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Material cornerMaterial = x == z ? (x < 0 ? red : blue) : white;
                    Transform corner = Group(corners, "Corner_" + x + "_" + z);
                    Box(corner, "FootPlate", new Vector3(x * 2.62f, FloorHeight + 0.025f, z * 2.62f), new Vector3(0.34f, 0.05f, 0.34f), steel);
                    Primitive(corner, "Post", PrimitiveType.Cylinder, new Vector3(x * 2.62f, 0.91f, z * 2.62f), new Vector3(0.14f, 0.76f, 0.14f), steel);
                    Primitive(corner, "PostCap", PrimitiveType.Sphere, new Vector3(x * 2.62f, 1.68f, z * 2.62f), Vector3.one * 0.17f, gold);
                    Box(corner, "CornerMat", new Vector3(x * 2.57f, FloorHeight + 0.003f, z * 2.57f), new Vector3(0.72f, 0.004f, 0.72f), cornerMaterial);

                    foreach (float height in ropeHeights)
                    {
                        GameObject pad = Box(corner, "TurnbucklePad", new Vector3(x * 2.45f, height, z * 2.45f), new Vector3(0.24f, 0.19f, 0.16f), cornerMaterial);
                        pad.transform.localRotation = Quaternion.LookRotation(new Vector3(-x, 0f, -z));
                    }
                }
            }

            Mesh rope = CreateRopeMesh();
            AssetDatabase.CreateAsset(rope, folder + "/Rope.asset");
            Transform ropes = Group(root.transform, "Ropes");
            Material[] ropeMaterials = { white, blue, white, red };
            for (int level = 0; level < ropeHeights.Length; level++)
            {
                float height = ropeHeights[level];
                for (int side = -1; side <= 1; side += 2)
                {
                    MeshObject(ropes, "RopeZ_" + level + "_" + side, rope, ropeMaterials[level], new Vector3(side * RopeHalfWidth, height, 0f), Quaternion.Euler(0f, 90f, 0f));
                    MeshObject(ropes, "RopeX_" + level + "_" + side, rope, ropeMaterials[level], new Vector3(0f, height, side * RopeHalfWidth), Quaternion.identity);
                }
            }

            Transform straps = Group(root.transform, "RopeStraps");
            for (int side = -1; side <= 1; side += 2)
            {
                for (int offset = -1; offset <= 1; offset += 2)
                {
                    Box(straps, "StrapX", new Vector3(offset * 1.1f, 0.94f, side * RopeHalfWidth), new Vector3(0.045f, 0.96f, 0.055f), white);
                    Box(straps, "StrapZ", new Vector3(side * RopeHalfWidth, 0.94f, offset * 1.1f), new Vector3(0.055f, 0.96f, 0.045f), white);
                }
            }

            Transform trim = Group(root.transform, "ApronTrim");
            for (int side = -1; side <= 1; side += 2)
            {
                Box(trim, "TopTrimX", new Vector3(0f, 0.005f, side * 3.005f), new Vector3(6.02f, 0.045f, 0.02f), gold);
                Box(trim, "TopTrimZ", new Vector3(side * 3.005f, 0.005f, 0f), new Vector3(0.02f, 0.045f, 6.02f), gold);
                Box(trim, "BottomTrimX", new Vector3(0f, -0.48f, side * 3.005f), new Vector3(6.02f, 0.025f, 0.02f), steel);
                Box(trim, "BottomTrimZ", new Vector3(side * 3.005f, -0.48f, 0f), new Vector3(0.02f, 0.025f, 6.02f), steel);
                Box(trim, "ApronBadgeX", new Vector3(0f, -0.23f, side * 3.018f), new Vector3(0.85f, 0.18f, 0.012f), gold);
                Box(trim, "ApronBadgeZ", new Vector3(side * 3.018f, -0.23f, 0f), new Vector3(0.012f, 0.18f, 0.85f), gold);
            }

            Transform steps = Group(root.transform, "Steps");
            for (int step = 0; step < 3; step++)
            {
                float top = -0.42f + step * 0.19f;
                float height = top + 0.62f;
                Box(steps, "Step_" + (step + 1), new Vector3(1.7f, -0.62f + height * 0.5f, -3.98f + step * 0.34f), new Vector3(1.05f, height, 0.36f), steel, true);
                Box(steps, "StepTread_" + (step + 1), new Vector3(1.7f, top + 0.003f, -3.98f + step * 0.34f), new Vector3(0.93f, 0.006f, 0.27f), apron);
            }

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/BoxingRing.prefab");
            AssetDatabase.SaveAssets();
            Verify(prefab);
            string previewPath = GetArgument("-ringPreviewPath");
            if (!string.IsNullOrEmpty(previewPath)) ExportPreview(prefab, previewPath);
            Debug.Log("PASS: BoxingRing created at " + folder + "/BoxingRing.prefab; floor top Y=0.15, 16 ropes and 4 corners.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static Material CreateMaterial(string folder, Shader shader, string name, Color color, float metallic, float smoothness)
    {
        Material material = new Material(shader) { name = name };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
        AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
        return material;
    }

    private static Transform Group(Transform parent, string name)
    {
        GameObject group = new GameObject(name);
        group.transform.SetParent(parent, false);
        return group.transform;
    }

    private static GameObject Box(Transform parent, string name, Vector3 position, Vector3 scale, Material material, bool collision = false)
    {
        return Primitive(parent, name, PrimitiveType.Cube, position, scale, material, collision);
    }

    private static GameObject Primitive(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material, bool collision = false)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        part.name = name;
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;
        part.GetComponent<Renderer>().sharedMaterial = material;
        if (!collision) UnityEngine.Object.DestroyImmediate(part.GetComponent<Collider>());
        return part;
    }

    private static void MeshObject(Transform parent, string name, Mesh mesh, Material material, Vector3 position, Quaternion rotation)
    {
        GameObject part = new GameObject(name);
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localRotation = rotation;
        part.AddComponent<MeshFilter>().sharedMesh = mesh;
        part.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    private static Mesh CreateCircleMesh(float inner, float outer)
    {
        const int segments = 64;
        Vector3[] vertices = new Vector3[(segments + 1) * 2];
        int[] triangles = new int[segments * 6];
        for (int index = 0; index <= segments; index++)
        {
            float angle = index * Mathf.PI * 2f / segments;
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            vertices[index * 2] = direction * inner;
            vertices[index * 2 + 1] = direction * outer;
            if (index == segments) continue;
            int vertex = index * 2;
            int triangle = index * 6;
            triangles[triangle] = vertex;
            triangles[triangle + 1] = vertex + 2;
            triangles[triangle + 2] = vertex + 1;
            triangles[triangle + 3] = vertex + 1;
            triangles[triangle + 4] = vertex + 2;
            triangles[triangle + 5] = vertex + 3;
        }

        Mesh mesh = new Mesh { name = "CenterCircle", vertices = vertices, triangles = triangles };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Mesh CreateRopeMesh()
    {
        const int segments = 40;
        const int sides = 10;
        Vector3[] vertices = new Vector3[(segments + 1) * (sides + 1)];
        int[] triangles = new int[segments * sides * 6];
        for (int segment = 0; segment <= segments; segment++)
        {
            float t = (float)segment / segments;
            Vector3 center = new Vector3(Mathf.Lerp(-RopeHalfWidth, RopeHalfWidth, t), -0.035f * Mathf.Sin(t * Mathf.PI), 0f);
            for (int side = 0; side <= sides; side++)
            {
                float angle = side * Mathf.PI * 2f / sides;
                vertices[segment * (sides + 1) + side] = center + new Vector3(0f, Mathf.Cos(angle), Mathf.Sin(angle)) * 0.022f;
                if (segment == segments || side == sides) continue;
                int vertex = segment * (sides + 1) + side;
                int next = vertex + sides + 1;
                int triangle = (segment * sides + side) * 6;
                triangles[triangle] = vertex;
                triangles[triangle + 1] = vertex + 1;
                triangles[triangle + 2] = next;
                triangles[triangle + 3] = vertex + 1;
                triangles[triangle + 4] = next + 1;
                triangles[triangle + 5] = next;
            }
        }

        Mesh mesh = new Mesh { name = "Rope", vertices = vertices, triangles = triangles };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void Verify(GameObject prefab)
    {
        if (prefab == null) throw new InvalidOperationException("The ring prefab was not saved.");
        if (prefab.transform.Find("Ropes").childCount != 16) throw new InvalidOperationException("The ring needs 16 ropes.");
        if (prefab.transform.Find("Corners").childCount != 4) throw new InvalidOperationException("The ring needs 4 corners.");
        Transform canvas = prefab.transform.Find("Platform/Canvas");
        if (Mathf.Abs(canvas.localPosition.y + canvas.localScale.y * 0.5f - FloorHeight) > 0.0001f) throw new InvalidOperationException("The ring floor height is incorrect.");
        if (canvas.GetComponent<BoxCollider>() == null) throw new InvalidOperationException("The ring floor needs a collider.");
        foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>())
        {
            if (renderer.sharedMaterial == null || renderer.sharedMaterial.shader == null) throw new InvalidOperationException("A ring material is missing.");
        }
    }

    private static string GetArgument(string name)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index] == name) return arguments[index + 1];
        }

        return null;
    }

    private static void ExportPreview(GameObject prefab, string path)
    {
        bool useBatching = GraphicsSettings.useScriptableRenderPipelineBatching;
        GraphicsSettings.useScriptableRenderPipelineBatching = false;
        PreviewRenderUtility preview = new PreviewRenderUtility();
        GameObject instance = UnityEngine.Object.Instantiate(prefab);
        try
        {
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>())
            {
                Material material = renderer.sharedMaterial;
                MaterialPropertyBlock properties = new MaterialPropertyBlock();
                Color color = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : material.color;
                properties.SetColor("_BaseColor", color);
                properties.SetColor("_Color", color);
                renderer.SetPropertyBlock(properties);
            }

            preview.AddSingleGO(instance);
            preview.camera.transform.position = new Vector3(8f, 6.2f, -8f);
            preview.camera.transform.LookAt(new Vector3(0f, 0.35f, 0f));
            preview.camera.fieldOfView = 38f;
            preview.camera.nearClipPlane = 0.1f;
            preview.camera.farClipPlane = 50f;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(0.035f, 0.045f, 0.065f);
            preview.lights[0].intensity = 1.4f;
            preview.lights[0].transform.rotation = Quaternion.Euler(45f, -35f, 0f);
            preview.lights[1].intensity = 0.7f;
            preview.lights[1].transform.rotation = Quaternion.Euler(25f, 145f, 0f);
            preview.ambientColor = new Color(0.35f, 0.35f, 0.38f);
            preview.BeginStaticPreview(new Rect(0f, 0f, 1200f, 900f));
            preview.Render(true);
            Texture2D image = preview.EndStaticPreview();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
        finally
        {
            preview.Cleanup();
            if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            GraphicsSettings.useScriptableRenderPipelineBatching = useBatching;
        }
    }

    public static void Preview()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BoxingRingGenerated/BoxingRing.prefab");
        Verify(prefab);
        ExportPreview(prefab, GetArgument("-ringPreviewPath"));
    }
}
