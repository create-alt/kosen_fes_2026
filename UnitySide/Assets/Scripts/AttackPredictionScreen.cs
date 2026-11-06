using UnityEngine;

public static class AttackPredictionScreen
{
    public static void SetLowerBand(RectTransform line, Camera camera, Vector3 top, Vector3 bottom, float projectionDistance)
    {
        Transform view = camera.transform;
        float depth = Mathf.Max(projectionDistance, camera.nearClipPlane + 0.1f);
        Vector3 topReference = view.position + Vector3.ProjectOnPlane(top - view.position, view.forward) + view.forward * depth;
        Vector3 bottomReference = view.position + Vector3.ProjectOnPlane(bottom - view.position, view.forward) + view.forward * depth;
        float topY = camera.WorldToViewportPoint(topReference).y;
        float bottomY = camera.WorldToViewportPoint(bottomReference).y;
        float lower = Mathf.Clamp(Mathf.Min(topY, bottomY), 0.05f, 0.83f);
        float upper = Mathf.Clamp(Mathf.Max(topY, bottomY), lower + 0.12f, 0.95f);
        Rect rect = camera.rect;
        line.anchorMin = new Vector2(rect.x + rect.width * 0.05f, rect.y + lower * rect.height);
        line.anchorMax = new Vector2(rect.x + rect.width * 0.95f, rect.y + upper * rect.height);
        line.sizeDelta = Vector2.zero;
    }

    public static bool FacesSource(Camera camera, Vector3 incomingDirection)
    {
        if (camera == null || !camera.isActiveAndEnabled || camera.targetDisplay > 2) return false;
        Vector3 incoming = Vector3.ProjectOnPlane(incomingDirection, Vector3.up).normalized;
        if (incoming.sqrMagnitude < 0.0001f) return false;
        PlayerController player = camera.GetComponentInParent<PlayerController>();
        Camera[] cameras = player != null ? player.GetComponentsInChildren<Camera>(true) : Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
        Camera best = null;
        float bestAlignment = 0.25f;
        foreach (Camera candidate in cameras)
        {
            if (!candidate.isActiveAndEnabled || candidate.targetDisplay > 2) continue;
            Vector3 facing = Vector3.ProjectOnPlane(candidate.transform.forward, Vector3.up).normalized;
            float alignment = Vector3.Dot(facing, incoming);
            if (alignment > bestAlignment + 0.001f || (best != null && Mathf.Abs(alignment - bestAlignment) <= 0.001f && candidate.targetDisplay < best.targetDisplay))
            {
                best = candidate;
                bestAlignment = alignment;
            }
        }
        return best == camera;
    }
}
