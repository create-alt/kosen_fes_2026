using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(PlayerController))]
public sealed class PlayerDamageFlash : MonoBehaviour
{
    [SerializeField] private Image[] damageOverlays;
    [SerializeField] private Color flashColor = new Color(1f, 0f, 0f, 0.45f);
    [SerializeField, Min(0.01f)] private float flashDuration = 0.25f;

    private PlayerController player;
    private float remaining;

    private void Awake()
    {
        player = GetComponent<PlayerController>();
        SetAlpha(0f);
    }

    private void OnEnable()
    {
        player.Damaged += Flash;
        player.HealthChanged += ClearAfterReset;
    }

    private void OnDisable()
    {
        if (player != null) player.Damaged -= Flash;
        if (player != null) player.HealthChanged -= ClearAfterReset;
        remaining = 0f;
        SetAlpha(0f);
    }

    private void Update()
    {
        if (remaining <= 0f) return;
        remaining = Mathf.Max(0f, remaining - Time.unscaledDeltaTime);
        SetAlpha(flashColor.a * remaining / Mathf.Max(0.01f, flashDuration));
    }

    private void Flash()
    {
        remaining = Mathf.Max(0.01f, flashDuration);
        SetAlpha(flashColor.a);
    }

    private void ClearAfterReset()
    {
        if (player.CurrentHealth < player.MaxHealth) return;
        remaining = 0f;
        SetAlpha(0f);
    }

    private void SetAlpha(float alpha)
    {
        if (damageOverlays == null) return;
        foreach (Image overlay in damageOverlays)
        {
            if (overlay == null) continue;
            Color color = flashColor;
            color.a = alpha;
            overlay.color = color;
            overlay.raycastTarget = false;
        }
    }
}
