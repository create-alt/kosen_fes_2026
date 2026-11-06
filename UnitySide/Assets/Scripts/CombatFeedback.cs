using UnityEngine;
using UnityEngine.UI;

public enum PunchResult
{
    Hit, Block, Miss
}

public sealed class CombatFeedback : MaskableGraphic
{
    private PlayerController player;
    private Camera frontCamera;
    private Text hitLabel;
    private Text finishLabel;
    private Text finishDetail;
    private Vector2 impact;
    private PunchResult lastResult;
    private float hitAge = 10f;
    private float victoryAge = 10f;
    private int battleVersion;
    private static readonly Color Gold = new Color(1f, 0.79f, 0.24f);
    public bool IsCelebrating => victoryAge < 3f;

    public void Configure(PlayerController owner, Font font)
    {
        player = owner;
        battleVersion = player.BattleVersion;
        foreach (Camera camera in player.GetComponentsInChildren<Camera>(true))
        {
            if (camera.targetDisplay == 0) frontCamera = camera;
        }
        raycastTarget = false;
        hitLabel = MakeLabel("PunchResult", font, 44);
        finishLabel = MakeLabel("Knockout", font, 134);
        finishLabel.rectTransform.sizeDelta = new Vector2(1600f, 360f);
        finishDetail = MakeLabel("ClearMessage", font, 38);
        player.PunchResolved += ShowPunch;
        ResetEffects();
    }

    private Text MakeLabel(string labelName, Font font, int size)
    {
        GameObject labelObject = new GameObject(labelName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Outline));
        labelObject.transform.SetParent(transform, false);
        Text label = labelObject.GetComponent<Text>();
        label.font = font;
        label.fontSize = size;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;
        label.rectTransform.sizeDelta = new Vector2(1000f, 220f);
        Outline outline = labelObject.GetComponent<Outline>();
        outline.effectColor = new Color(0.02f, 0.035f, 0.06f, 0.95f);
        outline.effectDistance = new Vector2(3f, -3f);
        return label;
    }

    private void ShowPunch(PunchResult result, Vector3 worldPosition)
    {
        if (IsCelebrating || result == PunchResult.Miss) return;
        lastResult = result;
        hitAge = 0f;
        Vector3 viewport = frontCamera != null ? frontCamera.WorldToViewportPoint(worldPosition) : new Vector3(0.5f, 0.5f, 1f);
        if (viewport.z <= 0f) viewport = new Vector3(0.5f, 0.5f, 1f);
        Rect bounds = rectTransform.rect;
        impact = new Vector2(Mathf.Lerp(bounds.xMin, bounds.xMax, Mathf.Clamp(viewport.x, 0.2f, 0.8f)), Mathf.Lerp(bounds.yMin, bounds.yMax, Mathf.Clamp(viewport.y, 0.25f, 0.7f)));
        hitLabel.text = result == PunchResult.Hit ? "HIT!  −10" : "GUARD";
        if (player.IsFinisherRush && result == PunchResult.Hit) hitLabel.text = "RUSH HIT!";
        hitLabel.color = result == PunchResult.Hit ? Gold : new Color(0.4f, 0.85f, 1f);
        SetVerticesDirty();
    }

    public void PlayVictory()
    {
        victoryAge = 0f;
        hitAge = 10f;
        finishLabel.text = player.RushSucceeded ? "RUSH K.O.!" : "K.O.!";
        finishLabel.color = Gold;
        finishDetail.text = "相手を撃破！";
        finishDetail.color = Color.white;
        SetVerticesDirty();
    }

    public void ResetEffects()
    {
        hitAge = 10f;
        victoryAge = 10f;
        if (hitLabel != null) hitLabel.enabled = false;
        if (finishLabel != null) finishLabel.enabled = false;
        if (finishDetail != null) finishDetail.enabled = false;
        SetVerticesDirty();
    }

    private void Update()
    {
        if (player == null) return;
        if (battleVersion != player.BattleVersion || !player.GameplayEnabled)
        {
            battleVersion = player.BattleVersion;
            ResetEffects();
            return;
        }
        hitAge += Time.unscaledDeltaTime;
        victoryAge += Time.unscaledDeltaTime;
        hitLabel.enabled = hitAge < 0.55f && !IsCelebrating;
        if (hitLabel.enabled)
        {
            hitLabel.rectTransform.anchoredPosition = impact + Vector2.up * (80f + hitAge * 70f);
            hitLabel.rectTransform.localScale = Vector3.one * (1f + 0.3f * Mathf.Exp(-hitAge * 18f));
            Color tint = hitLabel.color;
            tint.a = 1f - Mathf.Clamp01((hitAge - 0.25f) / 0.3f);
            hitLabel.color = tint;
        }
        finishLabel.enabled = victoryAge < 1.1f;
        finishDetail.enabled = finishLabel.enabled;
        if (finishLabel.enabled)
        {
            finishLabel.rectTransform.anchoredPosition = Vector2.up * 45f;
            finishLabel.rectTransform.localScale = Vector3.one * Mathf.Lerp(1.65f, 1f, Mathf.Clamp01(victoryAge / 0.18f));
            finishDetail.rectTransform.anchoredPosition = Vector2.down * 85f;
        }
        if (hitAge < 0.65f || victoryAge < 3.1f) SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        if (hitAge < 0.4f && lastResult != PunchResult.Miss && !IsCelebrating)
        {
            Color tint = lastResult == PunchResult.Hit ? Gold : new Color(0.4f, 0.85f, 1f);
            tint.a = 1f - hitAge / 0.4f;
            float radius = 22f + hitAge * 190f;
            Ring(mesh, impact, radius, 7f, tint);
            for (int index = 0; index < 12; index++)
            {
                float angle = index * Mathf.PI / 6f;
                Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                Line(mesh, impact + direction * (radius + 8f), impact + direction * (radius + 32f), 5f, tint);
            }
        }
        if (!IsCelebrating) return;
        Rect bounds = rectTransform.rect;
        float fade = 1f - Mathf.Clamp01((victoryAge - 1.4f) / 1.6f);
        if (victoryAge < 1.1f)
        {
            Color backdrop = new Color(0.015f, 0.025f, 0.05f, 0.65f);
            Quad(mesh, bounds.center, bounds.size, backdrop);
            Color ring = Gold;
            ring.a = 0.65f * (1f - victoryAge / 1.1f);
            Ring(mesh, Vector2.zero, 110f + victoryAge * 380f, 12f, ring);
        }
        for (int index = 0; index < 72; index++)
        {
            float spread = Mathf.Repeat(index * 0.618034f, 1f);
            float speed = 230f + Mathf.Repeat(index * 0.417f, 1f) * 300f;
            Vector2 position = new Vector2(Mathf.Lerp(bounds.xMin, bounds.xMax, spread) + Mathf.Sin(index + victoryAge * 3f) * 35f, bounds.yMax + 70f - victoryAge * speed);
            if (victoryAge >= 1.1f) position.x = Mathf.Sign(position.x) * (bounds.width * 0.3f + Mathf.Abs(position.x) * 0.4f);
            Color tint = index % 3 == 0 ? Color.white : index % 3 == 1 ? Gold : new Color(0.16f, 0.88f, 0.78f);
            tint.a = fade;
            Line(mesh, position, position + new Vector2(Mathf.Sin(index + victoryAge * 5f) * 10f, 13f), 5f, tint);
        }
    }

    private static void Ring(VertexHelper mesh, Vector2 center, float radius, float width, Color tint)
    {
        for (int index = 0; index < 48; index++)
        {
            float a = index * Mathf.PI / 24f;
            float b = (index + 1) * Mathf.PI / 24f;
            Line(mesh, center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, center + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * radius, width, tint);
        }
    }

    private static void Line(VertexHelper mesh, Vector2 start, Vector2 end, float width, Color tint)
    {
        Vector2 side = Vector2.Perpendicular(end - start).normalized * width * 0.5f;
        int first = mesh.currentVertCount;
        mesh.AddVert(start - side, tint, Vector2.zero);
        mesh.AddVert(start + side, tint, Vector2.zero);
        mesh.AddVert(end + side, tint, Vector2.zero);
        mesh.AddVert(end - side, tint, Vector2.zero);
        mesh.AddTriangle(first, first + 1, first + 2);
        mesh.AddTriangle(first, first + 2, first + 3);
    }

    private static void Quad(VertexHelper mesh, Vector2 center, Vector2 size, Color tint)
    {
        Line(mesh, center - Vector2.right * size.x * 0.5f, center + Vector2.right * size.x * 0.5f, size.y, tint);
    }

    protected override void OnDestroy()
    {
        if (player != null) player.PunchResolved -= ShowPunch;
        base.OnDestroy();
    }
}
