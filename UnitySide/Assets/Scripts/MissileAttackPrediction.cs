using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Camera))]
public sealed class MissileAttackPrediction : MonoBehaviour
{
    [SerializeField] private Image predictionLineTemplate;
    [SerializeField] private Color lineColor = new Color(1f, 0.15f, 0.1f, 0.85f);
    [SerializeField, Min(1f)] private float lineWidth = 3f;
    [SerializeField, Min(0.1f)] private float projectionDistance = 2f;

    private sealed class Prediction
    {
        public StraightMissile Missile;
        public PunchBotContoroller PunchBot;
        public Vector3 Position;
        public Vector3 ForwardDirection;
        public Image Line;
    }

    private readonly List<Prediction> predictions = new List<Prediction>();
    private Camera targetCamera;

    private void Awake()
    {
        targetCamera = GetComponent<Camera>();
    }

    private void OnEnable()
    {
        if (targetCamera == null) targetCamera = GetComponent<Camera>();
        StraightMissile.PredictionStarted += ShowPrediction;
        StraightMissile.PredictionEnded += HidePrediction;
        PunchBotContoroller.PunchPredictionStarted += ShowPunchPrediction;
        PunchBotContoroller.PunchPredictionEnded += HidePunchPrediction;
        foreach (StraightMissile missile in FindObjectsByType<StraightMissile>(FindObjectsSortMode.None))
        {
            if (missile.IsPredicting) ShowPrediction(missile, missile.PredictionPosition);
        }
        foreach (PunchBotContoroller punchBot in FindObjectsByType<PunchBotContoroller>(FindObjectsSortMode.None))
        {
            if (punchBot.IsPunchPredicting) ShowPunchPrediction(punchBot);
        }
    }

    private void OnDisable()
    {
        StraightMissile.PredictionStarted -= ShowPrediction;
        StraightMissile.PredictionEnded -= HidePrediction;
        PunchBotContoroller.PunchPredictionStarted -= ShowPunchPrediction;
        PunchBotContoroller.PunchPredictionEnded -= HidePunchPrediction;

        for (int index = predictions.Count - 1; index >= 0; index--) RemovePrediction(index);
    }

    private void LateUpdate()
    {
        for (int index = predictions.Count - 1; index >= 0; index--)
        {
            Prediction prediction = predictions[index];
            bool active = prediction.Missile != null ? prediction.Missile.IsPredicting : prediction.PunchBot != null && prediction.PunchBot.IsPunchPredicting;
            if (!active || prediction.Line == null)
            {
                RemovePrediction(index);
                continue;
            }

            UpdateLine(prediction);
        }
    }

    private void ShowPrediction(StraightMissile missile, Vector3 position)
    {
        if (predictionLineTemplate == null || targetCamera == null) return;
        if (targetCamera.targetDisplay != 0 && !AttackPredictionScreen.FacesSource(targetCamera, missile.IncomingDirection)) return;
        foreach (Prediction existing in predictions)
        {
            if (existing.Missile == missile) return;
        }

        Prediction prediction = new Prediction { Missile = missile, Position = missile.PredictionPosition, ForwardDirection = missile.PlayerForwardDirection, Line = CreateLine("MissilePredictionLine") };
        predictions.Add(prediction);
        UpdateLine(prediction);
    }

    private Image CreateLine(string objectName)
    {
        Image line = Instantiate(predictionLineTemplate, predictionLineTemplate.transform.parent);
        line.name = objectName;
        line.sprite = null;
        line.material = null;
        line.type = Image.Type.Simple;
        line.color = lineColor;
        line.raycastTarget = false;
        line.enabled = true;
        line.transform.SetAsLastSibling();
        return line;
    }

    private void ShowPunchPrediction(PunchBotContoroller punchBot)
    {
        if (predictionLineTemplate == null || targetCamera == null || !punchBot.IsPunchPredicting) return;
        if (!AttackPredictionScreen.FacesSource(targetCamera, punchBot.PunchIncomingDirection)) return;
        foreach (Prediction existing in predictions)
        {
            if (existing.PunchBot == punchBot) return;
        }
        Prediction prediction = new Prediction { PunchBot = punchBot, Position = punchBot.PunchPredictionPosition, ForwardDirection = punchBot.PlayerForwardDirection, Line = CreateLine("PunchPredictionLine") };
        predictions.Add(prediction);
        UpdateLine(prediction);
    }

    private void UpdateLine(Prediction prediction)
    {
        Transform cameraTransform = targetCamera.transform;
        Vector3 relative = prediction.Position - cameraTransform.position;
        float depth = Mathf.Max(projectionDistance, targetCamera.nearClipPlane + 0.1f);
        Vector3 reference = cameraTransform.position + Vector3.ProjectOnPlane(relative, cameraTransform.forward) + cameraTransform.forward * depth;
        Vector3 viewport = targetCamera.WorldToViewportPoint(reference);
        Rect cameraRect = targetCamera.rect;
        bool vertical = (prediction.Missile != null && targetCamera.targetDisplay == 0) || Mathf.Abs(Vector3.Dot(cameraTransform.right, prediction.ForwardDirection)) > 0.5f;
        float x = cameraRect.x + Mathf.Clamp(viewport.x, 0.05f, 0.95f) * cameraRect.width;
        float y = cameraRect.y + Mathf.Clamp(viewport.y, 0.05f, 0.95f) * cameraRect.height;
        RectTransform line = prediction.Line.rectTransform;
        line.anchorMin = vertical ? new Vector2(x, cameraRect.y + cameraRect.height * 0.05f) : new Vector2(cameraRect.x + cameraRect.width * 0.05f, y);
        line.anchorMax = vertical ? new Vector2(x, cameraRect.y + cameraRect.height * 0.95f) : new Vector2(cameraRect.x + cameraRect.width * 0.95f, y);
        line.pivot = new Vector2(0.5f, 0.5f);
        line.anchoredPosition = Vector2.zero;
        float width = Mathf.Max(prediction.PunchBot != null ? 70f : 27f, lineWidth);
        line.sizeDelta = vertical ? new Vector2(width, 0f) : new Vector2(0f, width);
        line.localRotation = Quaternion.identity;
        line.localScale = Vector3.one;
        prediction.Line.gameObject.SetActive(targetCamera.isActiveAndEnabled);
    }

    private void HidePunchPrediction(PunchBotContoroller punchBot)
    {
        for (int index = predictions.Count - 1; index >= 0; index--)
        {
            if (predictions[index].PunchBot == punchBot) RemovePrediction(index);
        }
    }

    private void HidePrediction(StraightMissile missile)
    {
        for (int index = predictions.Count - 1; index >= 0; index--)
        {
            if (predictions[index].Missile == missile) RemovePrediction(index);
        }
    }

    private void RemovePrediction(int index)
    {
        Image line = predictions[index].Line;
        if (line != null)
        {
            line.gameObject.SetActive(false);
            Destroy(line.gameObject);
        }

        predictions.RemoveAt(index);
    }
}
