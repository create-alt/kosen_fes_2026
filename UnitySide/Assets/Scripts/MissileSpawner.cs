using UnityEngine;

public class MissileSpawner : MonoBehaviour
{
    [SerializeField] private GameObject missilePrefab;
    [SerializeField] private Vector3 areaSize = new Vector3(4f, 2f, 4f);
    [SerializeField, Min(0.01f)] private float speed = 5f;
    [SerializeField, Min(0.01f)] private float travelDistance = 10f;
    [SerializeField, Min(0.01f)] private float spawnSpeed = 3f;

    private void OnEnable()
    {
        InvokeRepeating(nameof(Spawn), 3f, spawnSpeed);
    }

    private void OnDisable()
    {
        CancelInvoke(nameof(Spawn));
    }

    private void Spawn()
    {
        if (missilePrefab == null) return;

        Vector3 half = areaSize * 0.5f;
        Vector3 localPosition = new Vector3(Random.Range(-half.x, half.x), Random.Range(-half.y, half.y), Random.Range(-half.z, half.z));
        Vector3 spawnPosition = transform.TransformPoint(localPosition);

        GameObject missile = Instantiate(missilePrefab, spawnPosition, transform.rotation);

        if (!missile.TryGetComponent(out StraightMissile movement))
        {
            movement = missile.AddComponent<StraightMissile>();
        }

        movement.Initialize(speed, travelDistance, transform.up, Quaternion.Euler(-90f, 0f, 0f));
    }

    private void OnDrawGizmosSelected()
    {
        Matrix4x4 previousMatrix = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(Vector3.zero, areaSize);
        Gizmos.matrix = previousMatrix;
    }
}