using System;
using UnityEngine;

public class StraightMissile : MonoBehaviour
{
    [SerializeField, Min(0f)] private float waitTime = 1f;
    [SerializeField, Min(0.01f)] private float acceleration = 5f;
    [SerializeField] private float minHeight = -0.5f;
    [SerializeField] private float maxHeight = 0.5f;
    [SerializeField, Min(0.01f)] private float sideDistance = 5f;
    [SerializeField, Min(0.01f)] private float predictionTime = 1f;
    [SerializeField, Min(0f)] private float hitRadius = 0.06f;
    [SerializeField] private GameObject player;

    private float maxSpeed;
    private float currentSpeed;
    private float travelDistance;
    private float traveled;
    private float waitRemaining;
    private Vector3 direction;
    private Transform target;
    private PlayerController targetPlayer;
    private bool initialized;
    private bool flyingHorizontally;
    private bool showingPrediction;
    private float minimumPredictionTime = 1.6f;
    private Transform attackOwner;
    private int battleVersion;

    public static event Action<StraightMissile, Vector3> PredictionStarted;
    public static event Action<StraightMissile> PredictionEnded;
    public bool IsPredicting => showingPrediction && isActiveAndEnabled;
    public Vector3 PredictionPosition { get; private set; }
    public Vector3 IncomingDirection { get; private set; }
    public Vector3 PlayerForwardDirection { get; private set; } = Vector3.forward;

    public void Initialize(float moveSpeed, float maxDistance, Vector3 moveDirection, Quaternion rotation, Transform playerTarget = null)
    {
        EndPrediction();
        maxSpeed = Mathf.Max(0.01f, moveSpeed);
        target = playerTarget != null ? playerTarget : (player != null ? player.transform : null);
        targetPlayer = target != null ? target.GetComponentInParent<PlayerController>() : null;
        battleVersion = targetPlayer != null ? targetPlayer.BattleVersion : 0;
        flyingHorizontally = false;
        BeginFlight(moveDirection, maxDistance, rotation);
        initialized = true;
    }

    public void CancelForPlayer(PlayerController owner)
    {
        if (targetPlayer != owner) return;
        initialized = false;
        EndPrediction();
        Destroy(gameObject);
    }

    private void Update()
    {
        if (!initialized) return;
        PunchBotMotion ownerMotion = attackOwner != null ? attackOwner.GetComponentInChildren<PunchBotMotion>() : null;
        if (targetPlayer != null && (!targetPlayer.GameplayEnabled || targetPlayer.IsFinisherRush || targetPlayer.IsDead || battleVersion != targetPlayer.BattleVersion || (attackOwner != null && (!attackOwner.gameObject.activeInHierarchy || (ownerMotion != null && ownerMotion.IsDead)))))
        {
            initialized = false;
            EndPrediction();
            Destroy(gameObject);
            return;
        }

        if (waitRemaining > 0f)
        {
            waitRemaining -= Time.deltaTime;
            if (waitRemaining <= 0f)
            {
                if (flyingHorizontally) BattleAudio.Play(FightSound.Missile, targetPlayer != null ? Vector3.Dot(targetPlayer.transform.right, IncomingDirection.normalized) : 0f);
                EndPrediction();
            }
            return;
        }

        currentSpeed = Mathf.MoveTowards(currentSpeed, maxSpeed, Mathf.Max(0.01f, acceleration) * Time.deltaTime);

        float remaining = travelDistance - traveled;
        float distance = Mathf.Min(currentSpeed * Time.deltaTime, remaining);

        Vector3 previousPosition = transform.position;
        transform.position += direction * distance;
        traveled += distance;

        if (flyingHorizontally && targetPlayer != null && targetPlayer.TryReceiveAttack(previousPosition, transform.position, hitRadius))
        {
            initialized = false;
            EndPrediction();
            Destroy(gameObject);
            return;
        }

        if (traveled < travelDistance) return;

        if (flyingHorizontally || target == null)
        {
            initialized = false;
            Destroy(gameObject);
            return;
        }

        BeginHorizontalFlight();
    }

    private void BeginHorizontalFlight()
    {
        int side = UnityEngine.Random.value < 0.5f ? -1 : 1;
        float height = UnityEngine.Random.Range(Mathf.Min(minHeight, maxHeight), Mathf.Max(minHeight, maxHeight));
        float horizontalDistance = Mathf.Max(0.01f, sideDistance);
        Vector3 targetPosition = target.position;
        float attackY = targetPlayer != null ? targetPlayer.GetStandingPosition(UnityEngine.Random.Range(0.2f, 0.35f)).y : targetPosition.y + height;
        Vector3 moveDirection = side < 0 ? Vector3.right : Vector3.left;

        transform.position = new Vector3(targetPosition.x + side * horizontalDistance, attackY, targetPosition.z);
        PredictionPosition = new Vector3(targetPosition.x, attackY, targetPosition.z);
        IncomingDirection = transform.position - PredictionPosition;
        PlayerForwardDirection = targetPlayer != null ? targetPlayer.TrackingForwardDirection : Vector3.forward;
        flyingHorizontally = true;
        BeginFlight(moveDirection, horizontalDistance * 2f, Quaternion.LookRotation(moveDirection, Vector3.up));
        waitRemaining = Mathf.Max(minimumPredictionTime, predictionTime);
        showingPrediction = true;
        PredictionStarted?.Invoke(this, transform.position);
    }

    public void ConfigureDifficulty(CombatDifficulty difficulty, Transform owner)
    {
        minimumPredictionTime = difficulty.Telegraph;
        maxSpeed = Mathf.Min(maxSpeed, difficulty.MissileSpeed);
        attackOwner = owner;
        if (targetPlayer != null && targetPlayer.StandingBodyHeight > 0f) hitRadius = Mathf.Min(hitRadius, targetPlayer.StandingBodyHeight * 0.06f);
    }

    private void BeginFlight(Vector3 moveDirection, float maxDistance, Quaternion rotation)
    {
        direction = moveDirection.normalized;
        travelDistance = Mathf.Max(0.01f, maxDistance);
        currentSpeed = 0f;
        traveled = 0f;
        waitRemaining = Mathf.Max(0f, waitTime);
        transform.rotation = rotation;
    }

    private void OnDisable()
    {
        EndPrediction();
    }

    private void EndPrediction()
    {
        if (!showingPrediction) return;
        showingPrediction = false;
        PredictionEnded?.Invoke(this);
    }
}
