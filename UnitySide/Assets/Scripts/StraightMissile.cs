using System;
using UnityEngine;
using UnityEngine.Serialization;

public class StraightMissile : MonoBehaviour
{
    [SerializeField, Min(0f)] private float waitTime = 1f;
    [SerializeField, Min(0.01f)] private float acceleration = 5f;
    [FormerlySerializedAs("sideDistance"), SerializeField, Min(0.01f)] private float incomingDistance = 5f;
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
    private bool flyingIncoming;
    private bool showingPrediction;
    private float minimumPredictionTime = 1.6f;
    private Transform attackOwner;
    private int battleVersion;
    private bool threatenedAtLock;
    private bool dodgeReported;

    public static event Action<StraightMissile, Vector3> PredictionStarted;
    public static event Action<StraightMissile> PredictionEnded;
    public bool IsPredicting => showingPrediction && isActiveAndEnabled;
    private float AttackRadius => Mathf.Max(hitRadius * 1.5f, targetPlayer != null && targetPlayer.StandingBodyHeight > 0f ? targetPlayer.StandingBodyHeight * 0.1f : 0.12f);
    public Vector3 PredictionPosition { get; private set; }
    public Vector3 IncomingDirection { get; private set; }
    public Vector3 PlayerForwardDirection { get; private set; } = Vector3.forward;

    public void Initialize(float moveSpeed, float maxDistance, Vector3 moveDirection, Quaternion rotation, Transform playerTarget = null, bool directIncoming = false)
    {
        EndPrediction();
        maxSpeed = Mathf.Max(0.01f, moveSpeed);
        target = playerTarget != null ? playerTarget : (player != null ? player.transform : null);
        targetPlayer = target != null ? target.GetComponentInParent<PlayerController>() : null;
        battleVersion = targetPlayer != null ? targetPlayer.BattleVersion : 0;
        flyingIncoming = false;
        threatenedAtLock = false;
        dodgeReported = false;
        BeginFlight(moveDirection, maxDistance, rotation);
        initialized = true;
        if (directIncoming && target != null) BeginIncomingFlight();
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

        if (flyingIncoming && targetPlayer != null && targetPlayer.TryReceiveLateralAttack(previousPosition, transform.position, AttackRadius))
        {
            initialized = false;
            EndPrediction();
            Destroy(gameObject);
            return;
        }

        if (flyingIncoming && threatenedAtLock && !dodgeReported && targetPlayer != null && targetPlayer.HasAttackPassed(transform.position, direction, AttackRadius))
        {
            dodgeReported = true;
            targetPlayer.ReportDodge();
        }

        if (traveled < travelDistance) return;

        if (flyingIncoming || target == null)
        {
            initialized = false;
            Destroy(gameObject);
            return;
        }

        BeginIncomingFlight();
    }

    private void BeginIncomingFlight()
    {
        Vector3 targetPosition = target.position;
        float attackY = targetPlayer != null ? targetPlayer.GetStandingPosition(UnityEngine.Random.Range(0.4f, 0.65f)).y : targetPosition.y;
        Vector3 forward = targetPlayer != null ? targetPlayer.TrackingForwardDirection : Vector3.forward;
        PredictionPosition = new Vector3(targetPosition.x, attackY, targetPosition.z);
        transform.position = PredictionPosition + forward * Mathf.Max(3f, incomingDistance);
        Vector3 moveDirection = -forward;
        float aimDistance = Vector3.Distance(transform.position, PredictionPosition);
        float rearDistance = targetPlayer != null ? Vector3.Dot(transform.position - targetPlayer.GetRearmostStandingPosition(0.5f), forward) : aimDistance;
        float clearance = targetPlayer != null ? Mathf.Max(0.5f, targetPlayer.StandingBodyHeight) : 1f;
        threatenedAtLock = targetPlayer != null && targetPlayer.WouldReceiveLateralAttack(transform.position, PredictionPosition + moveDirection * clearance, AttackRadius);
        IncomingDirection = transform.position - PredictionPosition;
        PlayerForwardDirection = targetPlayer != null ? targetPlayer.TrackingForwardDirection : Vector3.forward;
        flyingIncoming = true;
        BeginFlight(moveDirection, Mathf.Max(aimDistance, rearDistance) + clearance, Quaternion.LookRotation(moveDirection, Vector3.up));
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
