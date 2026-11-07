using UnityEngine;
using UnityEngine.Serialization;

[RequireComponent(typeof(DilatantEnemy))]
public sealed class DilatantLiquidAttack : MonoBehaviour
{
    [SerializeField] private PlayerController player;
    [SerializeField] private Transform emissionPoint;
    [SerializeField] private Material liquidMaterial;
    [SerializeField] private Vector3 emissionOffset = new Vector3(0f, 0.65f, 0.35f);
    [SerializeField, Min(0f)] private float firstAttackDelay = 2f;
    [SerializeField, Min(0.05f)] private float chargeDuration = 0.9f;
    [SerializeField, Min(0.05f)] private float flightDuration = 0.8f;
    [SerializeField, Min(0.05f)] private float recoveryDuration = 1.4f;
    [SerializeField, Min(0.01f)] private float hitRadius = 0.14f;
    [SerializeField, Range(-1f, 1f)] private float nearAttackStartOffset = 0f;
    [SerializeField, Range(-1f, 1f)] private float farRangeOffset = -0.25f;
    [FormerlySerializedAs("farHighJetChance"), SerializeField, Range(0f, 1f)] private float farWaveChance = 0.8f;
    [FormerlySerializedAs("middleHighJetChance"), SerializeField, Range(0f, 1f)] private float middleWaveChance = 0.7f;
    [FormerlySerializedAs("sideAttackDistance"), SerializeField, Min(0.5f)] private float frontAttackDistance = 3f;
    [SerializeField, Min(0.05f)] private float liquidPunchDuration = 0.45f;
    [SerializeField, Min(0.01f)] private float liquidPunchImpactTime = 0.18f;
    [SerializeField, Min(0.1f)] private float liquidPunchReach = 0.9f;
    [SerializeField, Min(0f)] private float punchStepDistance = 0.35f;
    [SerializeField] private bool showPrediction = true;
    [SerializeField] private bool externalControl;
    private bool trainingAttack;

    private enum Phase { Waiting, Charging, Flying, Splash }
    public enum AttackPattern { FrontShot, UpperWave, LiquidPunch }

    public static event System.Action<DilatantLiquidAttack> PredictionStarted;
    public static event System.Action<DilatantLiquidAttack> PredictionEnded;
    public bool IsBusy => phase != Phase.Waiting;
    public bool IsPredicting => phase == Phase.Charging && showPrediction && isActiveAndEnabled;
    public Vector3 PredictionPosition { get; private set; }
    public Vector3 PredictionBottom => new Vector3(PredictionPosition.x, player != null ? player.GetStandingPosition(0f).y : PredictionPosition.y, PredictionPosition.z);
    public AttackPattern CurrentPattern => pattern;
    public Vector3 IncomingDirection => launchPosition - PredictionPosition;
    public Vector3 PlayerForwardDirection => player != null ? player.TrackingForwardDirection : Vector3.forward;
    public float ChargeProgress => Mathf.Clamp01(elapsed / activeChargeDuration);
    public Color PredictionColor => GetPredictionColor(pattern);
    public static Color GetPredictionColor(AttackPattern pattern) => pattern == AttackPattern.FrontShot ? new Color(1f, 0.65f, 0.05f) : pattern == AttackPattern.LiquidPunch ? new Color(0.15f, 0.85f, 1f) : new Color(1f, 0.15f, 0.15f);

    private DilatantEnemy enemy;
    private Phase phase;
    private float elapsed;
    private float waitDuration;
    private AttackPattern pattern;
    private float simulatedFlightTime;
    private Vector3 launchPosition;
    private Vector3 aimPosition;
    private Vector3 previousPosition;
    private Vector3 splashPosition;
    private GameObject effect;
    private Transform[] drops;
    private Material runtimeMaterial;
    private bool predictionAnnounced;
    private bool punchImpactApplied;
    private bool lastAttackWasPunch;
    private float activeChargeDuration = 1.6f;
    private float activeFlightDuration = 1.3f;
    private float activeRecoveryDuration = 1.8f;
    private float waveLifetime;
    private float waveHalfWidth;
    private float frontShotLifetime;
    private bool attackHit;
    private bool dodgeReported;
    private float punchRetreatLimit;
    private int battleVersion;
    private bool punchPoseActive;
    private bool rangedPoseActive;
    private Animator poseAnimator;
    private Vector3 bodyRestScale;
    private static readonly int IdleState = Animator.StringToHash("Base Layer.Idle");
    private Transform punchBody;
    private Transform punchArm;
    private Transform guardArm;
    private Vector3 bodyRestPosition;
    private Quaternion bodyRestRotation;
    private Vector3 armRestPosition;
    private Quaternion armRestRotation;
    private Quaternion guardRestRotation;
    private static readonly Vector3 FistLocalPosition = new Vector3(0.043f, -0.57f, 0.043f);
    private MaterialPropertyBlock properties;
    private static readonly Color LiquidColor = new Color(0.12f, 0.8f, 0.88f, 1f);

    private void Awake()
    {
        enemy = GetComponent<DilatantEnemy>();
        poseAnimator = GetComponent<Animator>();
    }

    private void OnEnable()
    {
        CachePunchRig();
        battleVersion = player != null ? player.BattleVersion : 0;
        phase = Phase.Waiting;
        elapsed = 0f;
        lastAttackWasPunch = false;
        waitDuration = firstAttackDelay;
    }

    private void Update()
    {
        if (enemy == null || enemy.IsDead || player == null || !player.isActiveAndEnabled || !player.GameplayEnabled || player.IsFinisherRush)
        {
            CancelAttack();
            return;
        }
        if (battleVersion != player.BattleVersion)
        {
            battleVersion = player.BattleVersion;
            lastAttackWasPunch = false;
            CancelAttack();
            waitDuration = Mathf.Max(2f, firstAttackDelay);
            return;
        }

        AdvanceAttack(Time.deltaTime);
    }

    private void LateUpdate()
    {
        if (trainingAttack || enemy == null || enemy.IsDead) return;
        if (!enemy.PrioritizePunch && poseAnimator != null && poseAnimator.GetCurrentAnimatorStateInfo(0).fullPathHash != IdleState) return;
        if (pattern == AttackPattern.LiquidPunch)
        {
            if (!punchPoseActive) return;
            if (phase == Phase.Charging) ApplyPunchPose(0f, ChargeProgress);
            else if (phase == Phase.Flying) ApplyPunchPose(PunchExtension(), 1f);
        }
        else if (phase == Phase.Charging || phase == Phase.Flying)
        {
            ApplyRangedPose();
        }
    }

    private void ApplyRangedPose()
    {
        CachePunchRig();
        if (punchBody == null || punchArm == null || guardArm == null) return;
        rangedPoseActive = true;
        float load = phase == Phase.Charging ? Mathf.SmoothStep(0f, 1f, ChargeProgress) : 1f - Mathf.SmoothStep(0f, 1f, elapsed / 0.3f);
        float thrust = 0f;
        if (phase == Phase.Flying)
        {
            thrust = elapsed < 0.12f ? Mathf.SmoothStep(0f, 1f, elapsed / 0.12f) : 1f - Mathf.SmoothStep(0f, 1f, (elapsed - 0.12f) / 0.43f);
        }
        punchBody.localPosition = bodyRestPosition + new Vector3(0f, -0.07f * load + 0.03f * thrust, -0.1f * load + 0.2f * thrust);
        punchBody.localScale = Vector3.Scale(bodyRestScale, new Vector3(1f + 0.1f * load - 0.05f * thrust, 1f - 0.1f * load + 0.08f * thrust, 1f + 0.1f * load + 0.12f * thrust));
        if (pattern == AttackPattern.UpperWave)
        {
            punchBody.localRotation = bodyRestRotation * Quaternion.Euler(5f * load - 10f * thrust, 0f, 0f);
            punchArm.localPosition = armRestPosition + Vector3.forward * (-0.09f * load + 0.16f * thrust);
            punchArm.localRotation = armRestRotation * Quaternion.Euler(-65f * load * (1f - thrust) - 95f * thrust, -12f * load, -20f * load);
            guardArm.localRotation = guardRestRotation * Quaternion.Euler(-65f * load * (1f - thrust) - 95f * thrust, 12f * load, 20f * load);
        }
        else
        {
            punchBody.localRotation = bodyRestRotation * Quaternion.Euler(-5f * thrust, 18f * load - 24f * thrust, -5f * load);
            punchArm.localPosition = armRestPosition + Vector3.forward * (-0.08f * load + 0.16f * thrust);
            punchArm.localRotation = armRestRotation * Quaternion.Euler(-50f * load * (1f - thrust) - 95f * thrust, -15f * thrust, -10f * load);
            guardArm.localRotation = guardRestRotation * Quaternion.Euler(-55f * load, 0f, 12f * load);
        }
    }

    private void RestoreRangedPose()
    {
        if (!rangedPoseActive) return;
        rangedPoseActive = false;
        if (punchBody == null || punchArm == null) return;
        punchBody.localPosition = bodyRestPosition;
        punchBody.localRotation = bodyRestRotation;
        punchBody.localScale = bodyRestScale;
        punchArm.localPosition = armRestPosition;
        punchArm.localRotation = armRestRotation;
        if (guardArm != null) guardArm.localRotation = guardRestRotation;
    }

    private float WorldPunchReach => (liquidPunchReach + punchStepDistance) * transform.TransformVector(Vector3.forward).magnitude;

    private bool InPunchReach()
    {
        return player.ForwardOffset >= nearAttackStartOffset && Vector3.ProjectOnPlane(player.GetStandingPosition(0.65f) - transform.position, Vector3.up).magnitude <= WorldPunchReach + AttackRadius;
    }

    private void AdvanceAttack(float delta)
    {
        elapsed += Mathf.Max(0f, delta);
        switch (phase)
        {
            case Phase.Waiting:
                if (!externalControl && elapsed >= waitDuration) BeginCharge();
                break;
            case Phase.Charging:
                DrawCharge(ChargeProgress);
                if (elapsed >= activeChargeDuration) Launch();
                break;
            case Phase.Flying:
                Fly();
                break;
            case Phase.Splash:
                DrawSplash(Mathf.Clamp01(elapsed / 0.35f));
                if (elapsed >= 0.35f) CancelAttack();
                break;
        }
    }

    public void ConfigureExternalControl(PlayerController targetPlayer)
    {
        player = targetPlayer;
        externalControl = true;
    }

    internal void ConfigureTraining(PlayerController targetPlayer, Material material)
    {
        ConfigureExternalControl(targetPlayer);
        trainingAttack = true;
        liquidMaterial = material;
        battleVersion = targetPlayer.BattleVersion;
    }

    internal bool TryStartTrainingAttack(AttackPattern attackPattern)
    {
        return trainingAttack && TryStartExternalAttack(attackPattern, 1.6f, 1.3f);
    }

    public bool TryStartWave(float telegraph, float flightTime)
    {
        return TryStartExternalAttack(AttackPattern.UpperWave, telegraph, flightTime);
    }

    private bool TryStartExternalAttack(AttackPattern attackPattern, float telegraph, float flightTime)
    {
        if (!externalControl || !isActiveAndEnabled || IsBusy || enemy == null || enemy.IsDead || player == null || !player.GameplayEnabled || player.IsDead || player.IsFinisherRush) return false;
        BeginCharge(attackPattern, telegraph, flightTime);
        return true;
    }

    private void BeginCharge(AttackPattern? forcedPattern = null, float telegraph = 0f, float flightTime = 0f)
    {
        EnsureEffect();
        phase = Phase.Charging;
        elapsed = 0f;
        CombatDifficulty difficulty = CombatDifficulty.ForHealth(enemy.CurrentHealth, enemy.MaxHealth);
        activeChargeDuration = Mathf.Max(chargeDuration, difficulty.Telegraph);
        activeFlightDuration = Mathf.Max(flightDuration, difficulty.FlightTime);
        activeRecoveryDuration = Mathf.Max(recoveryDuration, difficulty.Recovery);
        if (forcedPattern.HasValue)
        {
            pattern = forcedPattern.Value;
            activeChargeDuration = Mathf.Max(0.9f, telegraph);
            activeFlightDuration = Mathf.Max(0.8f, flightTime);
        }
        else SelectPattern();
        enemy.PrioritizePunch = pattern == AttackPattern.LiquidPunch;
        punchRetreatLimit = player.ForwardOffset - 0.12f;
        dodgeReported = false;
        if (!trainingAttack && player.IsTooClose && pattern == AttackPattern.LiquidPunch)
        {
            activeChargeDuration = difficulty.PressureTelegraph;
            activeRecoveryDuration = difficulty.PressureRecovery;
        }
        else if (pattern == AttackPattern.LiquidPunch) activeChargeDuration = Mathf.Max(1.5f, activeChargeDuration);
        LockTrajectory();
        attackHit = false;
        SetColor(LiquidColor);
        effect.SetActive(true);
        if (showPrediction)
        {
            predictionAnnounced = true;
            PredictionStarted?.Invoke(this);
        }
        DrawCharge(0f);
    }

    private void SelectPattern()
    {
        if (InPunchReach())
        {
            float punchChance = player.IsTooClose ? 0.6f : 0.45f;
            if (!lastAttackWasPunch && Random.value < punchChance) pattern = AttackPattern.LiquidPunch;
            else pattern = Random.value < 0.5f ? AttackPattern.FrontShot : AttackPattern.UpperWave;
        }
        else
        {
            float chance = player.ForwardOffset <= Mathf.Min(farRangeOffset, nearAttackStartOffset) ? farWaveChance : middleWaveChance;
            pattern = Random.value < chance ? AttackPattern.UpperWave : AttackPattern.FrontShot;
        }
        lastAttackWasPunch = pattern == AttackPattern.LiquidPunch;
    }

    private void LockTrajectory()
    {
        if (pattern == AttackPattern.LiquidPunch)
        {
            if (trainingAttack)
            {
                aimPosition = player.GetStandingPosition(0.75f);
                launchPosition = new Vector3(transform.position.x, aimPosition.y, transform.position.z);
            }
            else
            {
                CachePunchRig();
                launchPosition = PunchFistPosition(0f, 1f);
                aimPosition = PunchFistPosition(1f, 1f);
            }
            PredictionPosition = aimPosition;
            return;
        }

        launchPosition = EmissionPosition();
        float targetHeight = pattern == AttackPattern.UpperWave ? 0.85f : 0.5f;
        aimPosition = player.GetStandingPosition(targetHeight);
        PredictionPosition = aimPosition;
        launchPosition.y = aimPosition.y;
        if (pattern == AttackPattern.UpperWave)
        {
            aimPosition += player.TrackingRightDirection * (player.DepthOffset * player.TrackingLateralRange);
            PredictionPosition = aimPosition;
            float forwardDistance = Mathf.Max(3f, Vector3.Dot(launchPosition - aimPosition, player.TrackingForwardDirection));
            launchPosition = aimPosition + player.TrackingForwardDirection * forwardDistance;
            waveHalfWidth = player.TrackingLateralRange + Mathf.Max(0.5f, player.StandingBodyHeight * 0.25f);
            float aimDistance = Mathf.Max(0.01f, Vector3.Distance(launchPosition, aimPosition));
            float rearDistance = Vector3.Distance(launchPosition, player.GetRearmostStandingPosition(targetHeight));
            float clearance = Mathf.Max(0.5f, player.StandingBodyHeight);
            waveLifetime = activeFlightDuration * Mathf.Max(1.08f, (rearDistance + clearance) / aimDistance);
        }
        if (pattern == AttackPattern.FrontShot)
        {
            launchPosition = PredictionPosition + player.TrackingForwardDirection * Mathf.Max(3f, frontAttackDistance);
            float clearance = Mathf.Max(0.5f, player.StandingBodyHeight);
            float aimDistance = Mathf.Max(0.01f, Vector3.Distance(launchPosition, aimPosition));
            float rearDistance = Vector3.Dot(launchPosition - player.GetRearmostStandingPosition(targetHeight), player.TrackingForwardDirection);
            frontShotLifetime = activeFlightDuration * (Mathf.Max(aimDistance, rearDistance) + clearance) / aimDistance;
        }
    }

    private float AttackRadius
    {
        get
        {
            float radius = Mathf.Min(hitRadius, player != null && player.StandingBodyHeight > 0f ? player.StandingBodyHeight * 0.06f : 0.06f);
            return pattern == AttackPattern.LiquidPunch ? radius : radius * 1.5f;
        }
    }

    private Vector3 EmissionPosition()
    {
        return emissionPoint != null ? emissionPoint.position : transform.TransformPoint(emissionOffset);
    }

    private void DrawCharge(float progress)
    {
        if (pattern == AttackPattern.LiquidPunch)
        {
            punchPoseActive = true;
            ApplyPunchPose(0f, progress);
            return;
        }

        Vector3 center = launchPosition;
        float size = Mathf.Lerp(0.04f, hitRadius * 2f, progress);
        for (int index = 0; index < drops.Length; index++)
        {
            float angle = elapsed * 5f + index * Mathf.PI * 2f / drops.Length;
            Vector3 orbit = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * size * 0.3f;
            drops[index].position = center + orbit;
            drops[index].rotation = Quaternion.identity;
            drops[index].localScale = new Vector3(size, size * (1f + Mathf.Sin(angle) * 0.15f), size);
        }
    }

    public void SuspendForFinisher()
    {
        CancelAttack();
    }

    private void Launch()
    {
        BattleAudio.Play(pattern == AttackPattern.LiquidPunch ? FightSound.EnemyPunch : FightSound.Liquid);
        previousPosition = launchPosition;
        simulatedFlightTime = 0f;
        punchImpactApplied = false;
        EndPrediction();
        phase = Phase.Flying;
        elapsed = 0f;
    }

    private Vector3 FlightPosition(float progress)
    {
        return Vector3.LerpUnclamped(launchPosition, aimPosition, progress);
    }

    private void Fly()
    {
        if (pattern == AttackPattern.LiquidPunch)
        {
            SwingLiquidPunch();
            return;
        }

        float duration = activeFlightDuration;
        float lifetime = pattern == AttackPattern.UpperWave ? waveLifetime : frontShotLifetime;
        float endTime = Mathf.Min(elapsed, lifetime);
        int steps = Mathf.Clamp(Mathf.CeilToInt((endTime - simulatedFlightTime) / 0.03f), 1, 128);
        float startTime = simulatedFlightTime;
        for (int step = 1; step <= steps; step++)
        {
            float time = Mathf.Lerp(startTime, endTime, step / (float)steps);
            Vector3 nextPosition = FlightPosition(time / duration);
            if (TryHit(previousPosition, nextPosition, out Vector3 hitPosition))
            {
                BeginSplash(hitPosition);
                return;
            }
            if (!attackHit && !dodgeReported && player.HasAttackPassed(nextPosition, aimPosition - launchPosition, AttackRadius))
            {
                dodgeReported = true;
                player.ReportDodge();
            }
            previousPosition = nextPosition;
        }
        simulatedFlightTime = endTime;
        float progress = endTime / duration;
        Vector3 direction = aimPosition - launchPosition;
        Quaternion rotation = direction.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(direction) : Quaternion.identity;
        for (int index = 0; index < drops.Length; index++)
        {
            if (pattern == AttackPattern.UpperWave)
            {
                float across = Mathf.Lerp(-waveHalfWidth, waveHalfWidth, index / (float)(drops.Length - 1));
                drops[index].position = FlightPosition(progress) + player.TrackingRightDirection * across + Vector3.up * (0.02f * Mathf.Sin(elapsed * 12f + index));
                drops[index].rotation = Quaternion.LookRotation(-player.TrackingForwardDirection, Vector3.up);
                drops[index].localScale = new Vector3(waveHalfWidth * 2f / (drops.Length - 1) * 1.4f, AttackRadius * 2.5f, 0.35f);
            }
            else
            {
                float tailProgress = Mathf.Max(0f, progress - index * 0.025f);
                float size = AttackRadius * 2f * (1f - index * 0.09f);
                drops[index].position = FlightPosition(tailProgress);
                drops[index].rotation = rotation;
                drops[index].localScale = new Vector3(size, size * (1f + 0.12f * Mathf.Sin(elapsed * 18f + index)), size * 1.4f);
            }
        }

        if (elapsed >= lifetime) CancelAttack();
    }

    private void SwingLiquidPunch()
    {
        float duration = Mathf.Max(0.05f, liquidPunchDuration);
        float impactTime = Mathf.Clamp(liquidPunchImpactTime, 0.01f, duration - 0.01f);
        punchPoseActive = true;
        ApplyPunchPose(PunchExtension(), 1f);
        if (!punchImpactApplied && elapsed >= impactTime)
        {
            punchImpactApplied = true;
            if (player.ForwardOffset >= punchRetreatLimit && (trainingAttack || InPunchReach()) && TryHitWithPunch())
            {
                attackHit = true;
                SetColor(new Color(0.85f, 1f, 1f));
            }
            else if (!dodgeReported)
            {
                dodgeReported = true;
                player.ReportDodge();
            }
        }
        if (elapsed >= duration) CancelAttack();
    }

    private bool TryHitWithPunch()
    {
        if (trainingAttack) return player.TryReceiveLowAttack(aimPosition, aimPosition, AttackRadius);
        Vector3 previous = PunchFistPosition(0f, 1f);
        for (int index = 1; index <= 12; index++)
        {
            Vector3 next = PunchFistPosition(index / 12f, 1f);
            if (player.TryReceiveLowAttack(previous, next, AttackRadius)) return true;
            previous = next;
        }
        return false;
    }

    private float PunchExtension()
    {
        float duration = Mathf.Max(0.05f, liquidPunchDuration);
        float impactTime = Mathf.Clamp(liquidPunchImpactTime, 0.01f, duration - 0.01f);
        if (elapsed <= impactTime) return 1f - Mathf.Pow(1f - Mathf.Clamp01(elapsed / impactTime), 3f);
        return 1f - Mathf.SmoothStep(0f, 1f, (elapsed - impactTime) / (duration - impactTime));
    }

    private void CachePunchRig()
    {
        if (punchArm != null) return;
        punchBody = transform.Find("Visual/Body");
        punchArm = transform.Find("Visual/Body/ArmR");
        guardArm = transform.Find("Visual/Body/ArmL");
        if (punchBody == null || punchArm == null) return;
        bodyRestPosition = punchBody.localPosition;
        bodyRestScale = punchBody.localScale;
        bodyRestRotation = punchBody.localRotation;
        armRestPosition = punchArm.localPosition;
        armRestRotation = punchArm.localRotation;
        if (guardArm != null) guardRestRotation = guardArm.localRotation;
    }

    private Vector3 PunchBodyPosition(float extension) => bodyRestPosition + Vector3.forward * ((0.08f + punchStepDistance) * extension);
    private Quaternion PunchBodyRotation(float extension, float charge) => bodyRestRotation * Quaternion.Euler(0f, Mathf.Lerp(14f * charge, -16f, extension), Mathf.Lerp(-3f * charge, 2f, extension));
    private Vector3 PunchArmPosition(float extension, float charge) => armRestPosition + new Vector3(-0.12f * extension, 0.03f * extension, Mathf.Lerp(-0.12f * charge, 0.2f, extension));
    private Quaternion PunchArmRotation(float extension) => armRestRotation * Quaternion.Euler(Mathf.Lerp(-55f, -95f, extension), Mathf.Lerp(0f, -30f, extension), -12f);

    private Vector3 PunchFistPosition(float extension, float charge)
    {
        if (trainingAttack) return Vector3.Lerp(launchPosition, aimPosition, extension);
        if (punchBody == null || punchArm == null)
        {
            return transform.TransformPoint(new Vector3(Mathf.Lerp(0.3f, 0.12f, extension), 0.8f, Mathf.Lerp(0.15f - charge * 0.08f, liquidPunchReach * 0.8f + punchStepDistance, extension)));
        }
        Matrix4x4 bodyMatrix = punchBody.parent.localToWorldMatrix * Matrix4x4.TRS(PunchBodyPosition(extension), PunchBodyRotation(extension, charge), punchBody.localScale);
        Matrix4x4 armMatrix = bodyMatrix * Matrix4x4.TRS(PunchArmPosition(extension, charge), PunchArmRotation(extension), punchArm.localScale);
        return armMatrix.MultiplyPoint3x4(FistLocalPosition);
    }

    private void ApplyPunchPose(float extension, float charge)
    {
        if (punchBody != null && punchArm != null)
        {
            punchBody.localPosition = PunchBodyPosition(extension);
            punchBody.localRotation = PunchBodyRotation(extension, charge);
            punchArm.localPosition = PunchArmPosition(extension, charge);
            punchArm.localRotation = PunchArmRotation(extension);
            if (guardArm != null) guardArm.localRotation = guardRestRotation * Quaternion.Euler(-65f, 0f, 12f);
        }
        Vector3 fist = PunchFistPosition(extension, charge);
        float scale = transform.TransformVector(Vector3.up).magnitude;
        for (int index = 0; index < drops.Length; index++)
        {
            float angle = (index - 1) * Mathf.PI * 2f / (drops.Length - 1);
            Vector3 offset = index == 0 ? Vector3.zero : new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0.1f) * (0.065f * scale);
            drops[index].position = fist + transform.TransformDirection(offset);
            drops[index].rotation = transform.rotation;
            float size = index == 0 ? 0.24f : 0.09f;
            drops[index].localScale = new Vector3(size, size, size * 0.85f);
        }
    }

    private void RestorePunchPose()
    {
        if (!punchPoseActive) return;
        punchPoseActive = false;
        if (punchBody != null && punchArm != null)
        {
            punchBody.localPosition = bodyRestPosition;
            punchBody.localRotation = bodyRestRotation;
            punchArm.localPosition = armRestPosition;
            punchArm.localRotation = armRestRotation;
            if (guardArm != null) guardArm.localRotation = guardRestRotation;
        }
    }

    private bool TryHit(Vector3 start, Vector3 end, out Vector3 hitPosition)
    {
        if (pattern == AttackPattern.UpperWave)
        {
            float offset = Vector3.Dot(player.GetStandingPosition(0.85f) - start, player.TrackingRightDirection);
            if (Mathf.Abs(offset) > waveHalfWidth + AttackRadius)
            {
                hitPosition = start;
                return false;
            }
            Vector3 shift = player.TrackingRightDirection * offset;
            start += shift;
            end += shift;
        }
        bool hit = player.TryReceiveAttack(start, end, AttackRadius, out hitPosition);
        if (hit) attackHit = true;
        return hit;
    }

    private void BeginSplash(Vector3 position)
    {
        splashPosition = position;
        phase = Phase.Splash;
        elapsed = 0f;
        DrawSplash(0f);
    }

    private void DrawSplash(float progress)
    {
        for (int index = 0; index < drops.Length; index++)
        {
            float angle = index * Mathf.PI * 2f / drops.Length;
            Vector3 direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), -0.3f);
            drops[index].position = splashPosition + direction * progress * 0.65f + Vector3.down * progress * progress * 0.4f;
            float size = hitRadius * (1f - progress);
            drops[index].localScale = new Vector3(size, size * 1.7f, size);
        }
    }

    private void EnsureEffect()
    {
        if (effect != null) return;
        Material material = liquidMaterial;
        if (material == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            runtimeMaterial = new Material(shader);
            if (runtimeMaterial.HasProperty("_Smoothness")) runtimeMaterial.SetFloat("_Smoothness", 0.9f);
            material = runtimeMaterial;
        }

        properties = new MaterialPropertyBlock();
        effect = new GameObject("DilatantLiquidEffect");
        effect.transform.SetParent(transform, false);
        drops = new Transform[7];
        for (int index = 0; index < drops.Length; index++)
        {
            GameObject drop = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            drop.name = "LiquidDrop" + index;
            drop.transform.SetParent(effect.transform, false);
            Collider collider = drop.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            drop.GetComponent<Renderer>().sharedMaterial = material;
            drops[index] = drop.transform;
        }
    }

    private void SetColor(Color color)
    {
        properties.SetColor("_BaseColor", color);
        properties.SetColor("_Color", color);
        foreach (Transform drop in drops) drop.GetComponent<Renderer>().SetPropertyBlock(properties);
    }

    private void CancelAttack()
    {
        if (enemy != null) enemy.PrioritizePunch = false;
        RestorePunchPose();
        RestoreRangedPose();
        EndPrediction();
        if (effect != null) effect.SetActive(false);
        phase = Phase.Waiting;
        elapsed = 0f;
        attackHit = false;
        waitDuration = activeRecoveryDuration;
    }

    private void OnDisable()
    {
        CancelAttack();
    }

    private void EndPrediction()
    {
        if (!predictionAnnounced) return;
        predictionAnnounced = false;
        PredictionEnded?.Invoke(this);
    }

    private void OnDestroy()
    {
        if (runtimeMaterial != null) Destroy(runtimeMaterial);
    }
}

