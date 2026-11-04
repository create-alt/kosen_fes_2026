using UnityEngine;

public static class AttackPredictionScreen
{
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
