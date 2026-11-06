using UnityEngine;

public class PunchBotContoroller : MonoBehaviour
{
    [SerializeField] private GameObject missilePrefab;
    [SerializeField] private Transform missileSpawnPoint;
    [SerializeField] private Vector3 spawnAreaSize = new Vector3(1f, 0.5f, 1f);
    [SerializeField] private Transform player;
    [SerializeField] private PunchBotMotion motion;
    [SerializeField] private bool autoFire = true;
    [SerializeField, Min(0f)] private float firstAttackDelay = 3f;
    [SerializeField, Min(0.01f)] private float attackInterval = 3f;
    [SerializeField, Min(0f)] private float rangeReactionTime = 0.2f;
    [SerializeField, Min(0f)] private float punchInterval = 0.12f;
    [SerializeField, Min(0f)] private float punchRecovery = 0.65f;
    [SerializeField, Range(1, 3)] private int minimumComboPunches = 1;
    [SerializeField, Range(1, 3)] private int maximumComboPunches = 3;
    [SerializeField, Min(0.01f)] private float missileSpeed = 5f;
    [SerializeField, Min(0.01f)] private float riseDistance = 10f;
    [SerializeField, Range(0f, 1.5f)] private float punchHeight = 0.75f;
    [SerializeField, Min(0f)] private float punchHitRadius = 0.12f;

    private float attackRemaining;
    private bool attacking;
    private bool punching;
    private PlayerController trackedPlayer;
    private Vector3 punchPosition;
    private int punchesRemaining;
    private int consecutivePunchCombos;
    private bool nextPunchIsJab = true;
    private bool previousCloseRange;
    private bool punchPredictionActive;
    private bool punchWindingUp;
    private bool closePressure;
    private bool punchResolved;
    private float punchRetreatLimit;
    private float punchWindupRemaining;
    private int queuedMissiles;
    private double nextMissileTime;
    private int battleVersion;
    private CombatDifficulty activeDifficulty;

    public static event System.Action<PunchBotContoroller> PunchPredictionStarted;
    public static event System.Action<PunchBotContoroller> PunchPredictionEnded;
    public bool IsPunchPredicting => punchPredictionActive && attacking && punching && CanAttack();
    public Vector3 PunchPredictionPosition => punchPosition;
    public Vector3 PunchPredictionBottom => new Vector3(punchPosition.x, trackedPlayer != null ? trackedPlayer.GetStandingPosition(0f).y : punchPosition.y, punchPosition.z);
    public Vector3 PunchIncomingDirection => transform.position - punchPosition;
    public Vector3 PlayerForwardDirection => trackedPlayer != null ? trackedPlayer.TrackingForwardDirection : Vector3.forward;

    private void Awake()
    {
        if (motion == null) motion = GetComponentInChildren<PunchBotMotion>();
        if (player != null) trackedPlayer = player.GetComponentInParent<PlayerController>();
        battleVersion = trackedPlayer != null ? trackedPlayer.BattleVersion : 0;
    }

    private void OnEnable()
    {
        queuedMissiles = 0;
        nextMissileTime = 0d;
        punchWindingUp = false;
        attackRemaining = firstAttackDelay;
        punchesRemaining = 0;
        nextPunchIsJab = true;
        previousCloseRange = false;
        consecutivePunchCombos = 0;

        if (motion == null) return;
        motion.MissileLaunch += SpawnMissile;
        motion.PunchImpact += HitWithPunch;
        motion.AttackEnded += EndAttack;
        motion.HitReacted += InterruptWindup;
    }

    private void OnDisable()
    {
        if (motion != null) motion.PrioritizePunch = false;
        queuedMissiles = 0;
        punchWindingUp = false;
        EndPunchPrediction();
        if (motion != null)
        {
            if (attacking) motion.StopMissileAttack();
            motion.MissileLaunch -= SpawnMissile;
            motion.PunchImpact -= HitWithPunch;
            motion.AttackEnded -= EndAttack;
            motion.HitReacted -= InterruptWindup;
        }

        attacking = false;
        punching = false;
        punchesRemaining = 0;
        if (motion != null) motion.SetBoxingGuard(false);
    }

    private void Update()
    {
        if (!CanAttack())
        {
            CancelCurrentAttack();
            queuedMissiles = 0;
            EndPunchPrediction();
            punchesRemaining = 0;
            if (motion != null) motion.SetBoxingGuard(false);
            return;
        }
        if (battleVersion != trackedPlayer.BattleVersion)
        {
            battleVersion = trackedPlayer.BattleVersion;
            consecutivePunchCombos = 0;
            CancelCurrentAttack();
            queuedMissiles = 0;
            nextMissileTime = 0d;
            attackRemaining = Mathf.Max(2f, firstAttackDelay);
        }
        if (queuedMissiles > 0 && Time.timeAsDouble >= nextMissileTime)
        {
            SpawnMissileNow();
            queuedMissiles--;
            nextMissileTime = Time.timeAsDouble + activeDifficulty.BurstSpacing;
        }

        bool closeRange = trackedPlayer.IsInEnemyPunchRange;
        motion.SetBoxingGuard(closeRange);
        if (closeRange != previousCloseRange && !punching)
        {
            previousCloseRange = closeRange;
            punchesRemaining = 0;
            nextPunchIsJab = true;
            if (!attacking && queuedMissiles == 0) attackRemaining = Mathf.Max(0f, rangeReactionTime);
        }

        if (punchWindingUp)
        {
            AdvancePunchWindup(Time.deltaTime);
            return;
        }
        if (!autoFire || attacking || queuedMissiles > 0) return;

        attackRemaining -= Time.deltaTime;
        if (attackRemaining > 0f) return;

        bool continuePunchCombo = punchesRemaining > 0;
        bool choosePunch = closeRange && (continuePunchCombo || (consecutivePunchCombos == 0 && Random.value < 0.55f));
        if (choosePunch) FirePunch();
        else FireMissile();
        if (!attacking) attackRemaining = closeRange ? Mathf.Max(0.01f, punchRecovery) : Mathf.Max(0.01f, attackInterval);
    }

    public void FireMissile()
    {
        if (!CanAttack() || attacking || motion.IsAttacking) return;
        if (missilePrefab == null || missileSpawnPoint == null) return;

        activeDifficulty = CombatDifficulty.ForHealth(motion.CurrentHealth, motion.MaxHealth);
        int missileCount = Random.Range(1, activeDifficulty.MaximumBurst + 1);
        punchesRemaining = 0;
        punching = false;
        attacking = motion.PlayMissileAttack(missileCount);
        if (attacking) consecutivePunchCombos = 0;
    }

    public void FirePunch()
    {
        if (!CanAttack() || attacking || motion.IsAttacking || !trackedPlayer.IsInEnemyPunchRange) return;
        closePressure = trackedPlayer.IsTooClose;
        if (punchesRemaining <= 0)
        {
            consecutivePunchCombos++;
            activeDifficulty = CombatDifficulty.ForHealth(motion.CurrentHealth, motion.MaxHealth);
            int minimum = Mathf.Clamp(minimumComboPunches, 1, 3);
            int maximum = Mathf.Min(Mathf.Clamp(maximumComboPunches, minimum, 3), activeDifficulty.MaximumBurst);
            if (closePressure)
            {
                maximum = Mathf.Min(Mathf.Clamp(maximumComboPunches, 1, 3), activeDifficulty.MaximumBurst + 1);
                minimum = Mathf.Min(2, maximum);
            }
            minimum = Mathf.Min(minimum, maximum);
            punchesRemaining = Random.Range(minimum, maximum + 1);
            nextPunchIsJab = true;
        }

        punchPosition = trackedPlayer.GetStandingPosition(punchHeight);
        punchRetreatLimit = trackedPlayer.ForwardOffset - 0.12f;
        motion.PrioritizePunch = true;
        punchResolved = false;
        punching = true;
        attacking = true;
        punchWindingUp = true;
        punchWindupRemaining = closePressure ? activeDifficulty.PressureTelegraph : Mathf.Max(1.5f, activeDifficulty.Telegraph);
        punchPredictionActive = true;
        PunchPredictionStarted?.Invoke(this);
    }

    private void AdvancePunchWindup(float delta)
    {
        if (!punchWindingUp) return;
        punchWindupRemaining -= delta;
        if (punchWindupRemaining > 0f) return;
        punchWindingUp = false;
        if (!CanAttack() || !motion.PlayPunchAttack(nextPunchIsJab))
        {
            CancelCurrentAttack();
            return;
        }
        punchesRemaining--;
        nextPunchIsJab = !nextPunchIsJab;
    }

    private void InterruptWindup()
    {
        if (punching || queuedMissiles > 0) return;
        queuedMissiles = 0;
        if (punchWindingUp) CancelCurrentAttack();
    }

    private void CancelCurrentAttack()
    {
        if (motion != null) motion.PrioritizePunch = false;
        punchWindingUp = false;
        EndPunchPrediction();
        if (motion != null && motion.IsAttacking) motion.StopMissileAttack();
        attacking = false;
        punching = false;
        punchesRemaining = 0;
        attackRemaining = motion != null ? CombatDifficulty.ForHealth(motion.CurrentHealth, motion.MaxHealth).Recovery : 1.8f;
    }

    private void HitWithPunch()
    {
        EndPunchPrediction();
        if (!attacking || !punching || !CanAttack() || punchResolved) return;
        punchResolved = true;
        bool hit = trackedPlayer.ForwardOffset >= punchRetreatLimit && trackedPlayer.TryReceiveLowAttack(punchPosition, punchPosition, punchHitRadius);
        if (!hit) trackedPlayer.ReportDodge();
    }

    private void SpawnMissile()
    {
        if (!attacking || punching || !CanAttack()) return;
        if (missilePrefab == null || missileSpawnPoint == null) return;

        queuedMissiles++;
    }

    private void SpawnMissileNow()
    {
        if (!CanAttack() || missilePrefab == null || missileSpawnPoint == null) return;
        Quaternion rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
        GameObject missile = Instantiate(missilePrefab, GetRandomSpawnPosition(), rotation);

        if (!missile.TryGetComponent(out StraightMissile movement))
        {
            movement = missile.AddComponent<StraightMissile>();
        }

        movement.Initialize(missileSpeed, riseDistance, Vector3.up, rotation, player, directIncoming: true);
        movement.ConfigureDifficulty(activeDifficulty, transform);
    }

    private void EndAttack()
    {
        if (motion != null) motion.PrioritizePunch = false;
        punchWindingUp = false;
        EndPunchPrediction();
        bool wasPunching = punching;
        attacking = false;
        punching = false;

        if (wasPunching)
        {
            bool continueCombo = motion != null && !motion.LastAttackInterrupted && trackedPlayer != null && trackedPlayer.IsInEnemyPunchRange && punchesRemaining > 0;
            float spacing = closePressure ? activeDifficulty.PressureSpacing : activeDifficulty.BurstSpacing;
            float recovery = closePressure ? activeDifficulty.PressureRecovery : Mathf.Max(punchRecovery, activeDifficulty.Recovery);
            attackRemaining = continueCombo ? Mathf.Max(punchInterval, spacing) : recovery;
            if (!continueCombo) punchesRemaining = 0;
        }
        else
        {
            punchesRemaining = 0;
            attackRemaining = Mathf.Max(activeDifficulty.Recovery, attackInterval);
        }
    }

    private Vector3 GetRandomSpawnPosition()
    {
        Vector3 half = Vector3.Max(spawnAreaSize, Vector3.zero) * 0.5f;
        Vector3 offset = new Vector3(Random.Range(-half.x, half.x), Random.Range(-half.y, half.y), Random.Range(-half.z, half.z));
        return missileSpawnPoint.TransformPoint(offset);
    }

    private void EndPunchPrediction()
    {
        if (!punchPredictionActive) return;
        punchPredictionActive = false;
        PunchPredictionEnded?.Invoke(this);
    }

    private void OnDrawGizmosSelected()
    {
        if (missileSpawnPoint == null) return;

        Matrix4x4 previousMatrix = Gizmos.matrix;
        Gizmos.matrix = missileSpawnPoint.localToWorldMatrix;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(Vector3.zero, Vector3.Max(spawnAreaSize, Vector3.zero));
        Gizmos.matrix = previousMatrix;
    }

    public void SuspendForFinisher()
    {
        queuedMissiles = 0;
        CancelCurrentAttack();
    }

    private bool CanAttack()
    {
        return isActiveAndEnabled && player != null && trackedPlayer != null && trackedPlayer.isActiveAndEnabled && trackedPlayer.GameplayEnabled && !trackedPlayer.IsFinisherRush && motion != null && motion.isActiveAndEnabled && !motion.IsDead;
    }
}
