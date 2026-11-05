using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Camera))]
public sealed class LiquidAttackPrediction : MonoBehaviour
{
    [SerializeField] private Image predictionLineTemplate;
    [SerializeField, Min(1f)] private float lineWidth = 6f;
    [SerializeField, Min(0.1f)] private float projectionDistance = 2f;

    private sealed class Warning
    {
        public DilatantLiquidAttack Attack;
        public Image Line;
    }

    private readonly List<Warning> warnings = new List<Warning>();
    private Camera targetCamera;

    private void Awake()
    {
        targetCamera = GetComponent<Camera>();
    }

    private void OnEnable()
    {
        if (targetCamera == null) targetCamera = GetComponent<Camera>();
        DilatantLiquidAttack.PredictionStarted += Show;
        DilatantLiquidAttack.PredictionEnded += Hide;
        foreach (DilatantLiquidAttack attack in FindObjectsByType<DilatantLiquidAttack>(FindObjectsSortMode.None))
        {
            if (attack.IsPredicting) Show(attack);
        }
    }

    private void LateUpdate()
    {
        for (int index = warnings.Count - 1; index >= 0; index--)
        {
            Warning warning = warnings[index];
            if (warning.Attack == null || !warning.Attack.IsPredicting || warning.Line == null)
            {
                Remove(index);
                continue;
            }
            UpdateWarning(warning);
        }
    }

    private void Show(DilatantLiquidAttack attack)
    {
        if (predictionLineTemplate == null || targetCamera == null || !attack.IsPredicting) return;
        if (!AttackPredictionScreen.FacesSource(targetCamera, attack.IncomingDirection)) return;
        foreach (Warning existing in warnings)
        {
            if (existing.Attack == attack) return;
        }
        Image line = Instantiate(predictionLineTemplate, predictionLineTemplate.transform.parent);
        line.name = "LiquidPredictionLine";
        line.sprite = null;
        line.material = null;
        line.type = Image.Type.Simple;
        line.raycastTarget = false;
        line.enabled = true;
        line.transform.SetAsLastSibling();
        Warning warning = new Warning { Attack = attack, Line = line };
        warnings.Add(warning);
        UpdateWarning(warning);
    }

    private void UpdateWarning(Warning warning)
    {
        Transform cameraTransform = targetCamera.transform;
        Vector3 relative = warning.Attack.PredictionPosition - cameraTransform.position;
        float depth = Mathf.Max(projectionDistance, targetCamera.nearClipPlane + 0.1f);
        Vector3 reference = cameraTransform.position + Vector3.ProjectOnPlane(relative, cameraTransform.forward) + cameraTransform.forward * depth;
        Vector3 viewport = targetCamera.WorldToViewportPoint(reference);
        Rect cameraRect = targetCamera.rect;
        bool sideView = Mathf.Abs(Vector3.Dot(cameraTransform.right, warning.Attack.PlayerForwardDirection)) > 0.5f;
        bool vertical = sideView && warning.Attack.CurrentPattern != DilatantLiquidAttack.AttackPattern.HighJet;
        float x = cameraRect.x + Mathf.Clamp(viewport.x, 0.05f, 0.95f) * cameraRect.width;
        float y = cameraRect.y + Mathf.Clamp(viewport.y, 0.05f, 0.95f) * cameraRect.height;
        RectTransform line = warning.Line.rectTransform;
        line.anchorMin = vertical ? new Vector2(x, cameraRect.y + cameraRect.height * 0.05f) : new Vector2(cameraRect.x + cameraRect.width * 0.05f, y);
        line.anchorMax = vertical ? new Vector2(x, cameraRect.y + cameraRect.height * 0.95f) : new Vector2(cameraRect.x + cameraRect.width * 0.95f, y);
        line.pivot = new Vector2(0.5f, 0.5f);
        line.anchoredPosition = Vector2.zero;
        line.sizeDelta = vertical ? new Vector2(lineWidth, 0f) : new Vector2(0f, lineWidth);
        line.localRotation = Quaternion.identity;
        line.localScale = Vector3.one;
        Color color = warning.Attack.PredictionColor;
        color.a = 0.9f;
        warning.Line.color = color;
        warning.Line.gameObject.SetActive(targetCamera.isActiveAndEnabled);
    }

    private void Hide(DilatantLiquidAttack attack)
    {
        for (int index = warnings.Count - 1; index >= 0; index--)
        {
            if (warnings[index].Attack == attack) Remove(index);
        }
    }

    private void Remove(int index)
    {
        Image line = warnings[index].Line;
        if (line != null)
        {
            line.gameObject.SetActive(false);
            Destroy(line.gameObject);
        }
        warnings.RemoveAt(index);
    }

    private void OnDisable()
    {
        DilatantLiquidAttack.PredictionStarted -= Show;
        DilatantLiquidAttack.PredictionEnded -= Hide;
        for (int index = warnings.Count - 1; index >= 0; index--) Remove(index);
    }
}
