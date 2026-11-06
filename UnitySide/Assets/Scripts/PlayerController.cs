using System;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    [SerializeField] private PunchBotMotion target;
    [SerializeField] private BoxCollider bodyCollider;
    [SerializeField] private bool enableKeyboardTest = true;
    [SerializeField, Range(0, 65535)] private int trackingUdpPort = 5005;
    [SerializeField, Min(0f)] private float udpPositionSmoothTime = 0.04f;
    [SerializeField, Min(0f)] private float udpHeightSmoothTime = 0.025f;
    private UdpTrackingInput udpTracking;
    private bool hasUdpTarget;
    private float udpForwardTarget;
    private float udpHeadTarget;
    [SerializeField] private bool keepCamerasFixed = true;
    [SerializeField, Min(0f)] private float testMoveSpeed = 1f;
    [SerializeField, Min(0f)] private float testBodySpeed = 1f;
    [SerializeField, Min(0.01f)] private float minimumBodyHeight = 0.1f;
    [SerializeField, Range(-1f, 1f)] private float punchMinimumOffset = -0.25f;
    [SerializeField, Range(-1f, 1f)] private float enemyPunchOffset = 0.5f;
    [SerializeField, Min(0f)] private float enemySurfacePadding = 0.02f;
    [SerializeField, Range(0.1f, 1f)] private float maximumForwardTravelRatio = 0.85f;

    [SerializeField, Min(1f)] private float maxHealth = 100f;
    [SerializeField, Min(0f)] private float damageInvulnerability = 0.6f;
    public const float PunchDamage = 10f;
    [SerializeField, Min(1f)] private float rushDuration = 5f;
    [SerializeField, Min(1)] private int rushGoal = 8;
    public bool IsFinisherRush { get; private set; }
    public bool RushSucceeded { get; private set; }
    public bool RushAttempted { get; private set; }
    public float RushRemaining { get; private set; }
    public int RushHits { get; private set; }
    public int RushGoal => rushGoal;
    private double invulnerableUntil;
    private Vector3 originPosition;
    private Vector3 forwardDirection;
    private float forwardDistance;
    private Vector3 bodyCenterLocal;
    private Vector3 bodyScale;
    private float originalBodyHeight;
    private Matrix4x4 standingBodyLocalToWorld;
    private bool initialized;
    private bool gameplayEnabled = true;
    private Transform selectedOpponent;
    private DilatantEnemy dilatantTarget;
    private FixedCameraPose[] fixedCameraPoses;

    private struct FixedCameraPose
    {
        public Transform Camera;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    public float ForwardOffset { get; private set; }
    public float UpperBodyOffset { get; private set; } = 1f;
    public float LowerBodyOffset { get; private set; }
    public bool GameplayEnabled => gameplayEnabled;
    public bool IsTooFar => ForwardOffset < punchMinimumOffset;
    public bool IsInEnemyPunchRange => ForwardOffset >= Mathf.Max(punchMinimumOffset, enemyPunchOffset);
    public bool IsTooClose => IsInEnemyPunchRange && ForwardOffset >= 0.75f;
    private float TrackingTravel => forwardDistance * (ForwardOffset > 0f ? maximumForwardTravelRatio : 1f);
    public bool IsInMissileRange => !IsInEnemyPunchRange;
    public float AttackInput { get; private set; }
    public Vector3 TrackingForwardDirection => forwardDirection;
    public event Action<PunchResult, Vector3> PunchResolved;
    public event Action MenuPunchPressed;
    public event Action Damaged;
    public event Action HealthChanged;
    public float CurrentHealth { get; private set; } = 100f;
    public float MaxHealth => maxHealth;
    public bool IsDead => CurrentHealth <= 0f;
    public int BattleVersion { get; private set; }
    public float StandingBodyHeight => originalBodyHeight;
    public bool KeyboardTestEnabled => enableKeyboardTest;
    public float OpponentHealth => dilatantTarget != null ? dilatantTarget.CurrentHealth : target != null ? target.CurrentHealth : 0f;
    public float OpponentMaxHealth => dilatantTarget != null ? dilatantTarget.MaxHealth : target != null ? target.MaxHealth : 0f;
    public int OpponentStage => CombatDifficulty.ForHealth(OpponentHealth, OpponentMaxHealth).Stage;

    private void Awake()
    {
        ResetHealth();
        CaptureOrigin();
    }

    private void OnEnable()
    {
        if (trackingUdpPort == 0) return;
        try
        {
            udpTracking = new UdpTrackingInput(trackingUdpPort);
        }
        catch (SocketException exception)
        {
            Debug.LogWarning($"UDPポート {trackingUdpPort} を開けませんでした: {exception.Message}", this);
        }
    }

    private void OnDisable()
    {
        udpTracking?.Dispose();
        udpTracking = null;
        hasUdpTarget = false;
        AttackInput = 0f;
    }

    private void Update()
    {
        AdvanceRush(Time.deltaTime);
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.rKey.wasPressedThisFrame && (gameplayEnabled || IsDead)) ResetBattle();
        try
        {
            if (udpTracking != null && udpTracking.Read(this))
            {
                AdvanceUdpTracking(Time.deltaTime);
                return;
            }
        }
        catch (SocketException exception)
        {
            Debug.LogWarning($"UDPの受信を停止しました: {exception.Message}", this);
            udpTracking.Dispose();
            udpTracking = null;
        }
        hasUdpTarget = false;
        if (keyboard == null || !enableKeyboardTest) return;
        if (keyboard.spaceKey.wasPressedThisFrame) SetAttackInput(1f);
        if (keyboard.spaceKey.wasReleasedThisFrame) SetAttackInput(0f);
        if (!gameplayEnabled) return;

        if (keyboard.homeKey.wasPressedThisFrame)
        {
            ResetTrackingOffsets();
            return;
        }

        float delta = Time.deltaTime;
        float forward = ForwardOffset + Axis(keyboard.wKey.isPressed, keyboard.sKey.isPressed) * testMoveSpeed * delta;
        float bodyMovement = Axis(keyboard.upArrowKey.isPressed, keyboard.downArrowKey.isPressed) * testBodySpeed * delta;
        bodyMovement = Mathf.Clamp(bodyMovement, -LowerBodyOffset, 1.5f - UpperBodyOffset);
        float upper = UpperBodyOffset + bodyMovement;
        float lower = LowerBodyOffset + bodyMovement;

        if (originalBodyHeight > 0f)
        {
            upper += Axis(keyboard.uKey.isPressed, keyboard.jKey.isPressed) * testBodySpeed * delta;
            upper = Mathf.Clamp(upper, lower + MinimumNormalizedHeight(), 1.5f);
            lower += Axis(keyboard.iKey.isPressed, keyboard.kKey.isPressed) * testBodySpeed * delta;
            lower = Mathf.Clamp(lower, 0f, upper - MinimumNormalizedHeight());
        }

        SetTrackingOffsets(forward, upper, lower);
    }

    private void LateUpdate()
    {
        RestoreCameraPoses();
    }

    internal void ApplyUdpTracking(float forward, float head, bool punched)
    {
        if (gameplayEnabled)
        {
            udpForwardTarget = forward * 2f - 1f;
            udpHeadTarget = Mathf.Max(head * 1.5f, MinimumNormalizedHeight());
            hasUdpTarget = true;
        }
        SetAttackInput(0f);
        if (!punched) return;
        SetAttackInput(1f);
        SetAttackInput(0f);
    }

    private void AdvanceUdpTracking(float delta)
    {
        if (!gameplayEnabled || !hasUdpTarget) return;
        float forwardBlend = udpPositionSmoothTime > 0f ? 1f - Mathf.Exp(-Mathf.Max(0f, delta) / udpPositionSmoothTime) : 1f;
        float headBlend = udpHeightSmoothTime > 0f ? 1f - Mathf.Exp(-Mathf.Max(0f, delta) / udpHeightSmoothTime) : 1f;
        float forward = Mathf.Lerp(ForwardOffset, udpForwardTarget, forwardBlend);
        float head = Mathf.Lerp(UpperBodyOffset, udpHeadTarget, headBlend);
        SetTrackingOffsets(forward, head, 0f);
    }

    public bool SetTrackingOffsets(float forwardOffset, float upperPosition, float lowerPosition)
    {
        if (!gameplayEnabled) return false;
        if (!IsFinite(forwardOffset) || !IsFinite(upperPosition) || !IsFinite(lowerPosition)) return false;
        if (!initialized) CaptureOrigin();

        forwardOffset = Mathf.Clamp(forwardOffset, -1f, 1f);
        upperPosition = Mathf.Clamp(upperPosition, 0f, 1.5f);
        lowerPosition = Mathf.Clamp(lowerPosition, 0f, 1.5f);
        if (upperPosition - lowerPosition < MinimumNormalizedHeight() - 0.000001f) return false;

        float height = originalBodyHeight * (upperPosition - lowerPosition);
        float centerMovement = originalBodyHeight * ((upperPosition + lowerPosition) * 0.5f - 0.5f);

        ForwardOffset = forwardOffset;
        UpperBodyOffset = upperPosition;
        LowerBodyOffset = lowerPosition;
        transform.position = originPosition + forwardDirection * (forwardOffset * TrackingTravel) + Vector3.up * centerMovement;

        if (bodyCollider != null && originalBodyHeight > 0f)
        {
            Transform body = bodyCollider.transform;
            body.localScale = new Vector3(bodyScale.x, bodyScale.y * height / originalBodyHeight, bodyScale.z);
            body.position = transform.TransformPoint(bodyCenterLocal) - body.TransformVector(bodyCollider.center);
        }

        RestoreCameraPoses();
        return true;
    }

    public void ResetTrackingOffsets()
    {
        hasUdpTarget = false;
        SetTrackingOffsets(0f, 1f, 0f);
    }

    public void SetGameplayEnabled(bool enabled)
    {
        gameplayEnabled = enabled;
        if (!enabled)
        {
            hasUdpTarget = false;
            AttackInput = 0f;
        }
    }

    public void SetKeyboardTestEnabled(bool enabled)
    {
        enableKeyboardTest = enabled;
        AttackInput = 0f;
    }

    public bool SetAttackInput(float value)
    {
        if (!isActiveAndEnabled || !IsFinite(value)) return false;
        if (value != 0f && value != 1f) return false;
        bool pressed = value == 1f && AttackInput == 0f;
        AttackInput = value;
        if (pressed)
        {
            if (!gameplayEnabled || IsDead || OpponentHealth <= 0f) MenuPunchPressed?.Invoke();
            else Punch();
            AttackInput = value;
        }
        return true;
    }

    public void SetOpponent(Transform opponent)
    {
        if (!initialized) CaptureOrigin();
        selectedOpponent = opponent;
        target = opponent != null ? opponent.GetComponentInChildren<PunchBotMotion>(true) : null;
        dilatantTarget = opponent != null ? opponent.GetComponentInChildren<DilatantEnemy>(true) : null;
        ResetHealth();
        AttackInput = 0f;
        UpdateForwardReference();

        bool wasEnabled = gameplayEnabled;
        gameplayEnabled = true;
        ResetTrackingOffsets();
        gameplayEnabled = wasEnabled;
    }

    private void CaptureOrigin()
    {
        if (initialized) return;

        originPosition = transform.position;
        CaptureCameraPoses();

        if (bodyCollider != null)
        {
            Transform body = bodyCollider.transform;
            if (body == transform || !body.IsChildOf(transform))
            {
                Debug.LogError("Body ColliderにはPlayerの子オブジェクトのBoxColliderを設定してください。", this);
                bodyCollider = null;
            }
            else
            {
                bodyScale = body.localScale;
                bodyCenterLocal = transform.InverseTransformPoint(body.TransformPoint(bodyCollider.center));
                originalBodyHeight = bodyCollider.size.y * body.TransformVector(Vector3.up).magnitude;
                standingBodyLocalToWorld = body.localToWorldMatrix;
            }
        }

        initialized = true;
        UpdateForwardReference();
    }

    private void Punch()
    {
        if (!gameplayEnabled || IsDead || OpponentHealth <= 0f) return;
        if (IsFinisherRush)
        {
            RegisterRushPunch();
            return;
        }
        BattleAudio.Play(FightSound.PlayerPunch);
        if (IsTooFar)
        {
            ReportPunch(PunchResult.Miss);
            return;
        }
        if (!RushAttempted && OpponentHealth <= PunchDamage)
        {
            BeginRush();
            RegisterRushPunch();
            return;
        }
        float previousHealth = OpponentHealth;
        if (dilatantTarget != null && dilatantTarget.isActiveAndEnabled) dilatantTarget.ReceivePunch();
        else if (target != null && target.isActiveAndEnabled) target.ReceiveDamage(PunchDamage);
        bool hit = OpponentHealth < previousHealth;
        BattleAudio.Play(hit ? FightSound.EnemyHit : FightSound.Block);
        ReportPunch(hit ? PunchResult.Hit : PunchResult.Block);
        if (!RushAttempted && OpponentHealth > 0f && OpponentHealth <= Mathf.Min(OpponentMaxHealth * 0.2f, PunchDamage * rushGoal)) BeginRush();
    }

    private void ReportPunch(PunchResult result)
    {
        Transform opponent = selectedOpponent != null ? selectedOpponent : target != null ? target.transform : dilatantTarget != null ? dilatantTarget.transform : null;
        Vector3 position = opponent != null ? opponent.position + Vector3.up * StandingBodyHeight * 0.65f : GetStandingPosition(0.65f);
        PunchResolved?.Invoke(result, position);
    }

    private void BeginRush()
    {
        IsFinisherRush = true;
        RushAttempted = true;
        RushRemaining = rushDuration;
        RushHits = 0;
        Transform opponent = selectedOpponent != null ? selectedOpponent : target != null ? target.transform : dilatantTarget.transform;
        foreach (PunchBotContoroller controller in opponent.GetComponentsInChildren<PunchBotContoroller>()) controller.SuspendForFinisher();
        foreach (DilatantLiquidAttack controller in opponent.GetComponentsInChildren<DilatantLiquidAttack>()) controller.SuspendForFinisher();
        foreach (StraightMissile missile in FindObjectsByType<StraightMissile>(FindObjectsSortMode.None)) missile.CancelForPlayer(this);
        BattleAudio.Play(FightSound.RushStart);
    }

    private void RegisterRushPunch()
    {
        RushHits++;
        bool finishing = RushHits >= rushGoal;
        if (dilatantTarget != null) dilatantTarget.ReceiveRushPunch(PunchDamage, finishing);
        else if (target != null) target.ReceiveRushPunch(PunchDamage, finishing);
        BattleAudio.Play(FightSound.RushHit);
        ReportPunch(PunchResult.Hit);
        if (!finishing) return;
        IsFinisherRush = false;
        RushSucceeded = true;
        BattleAudio.Play(FightSound.RushSuccess);
    }

    private void AdvanceRush(float delta)
    {
        if (!IsFinisherRush) return;
        if (!gameplayEnabled || OpponentHealth <= 0f)
        {
            IsFinisherRush = false;
            return;
        }
        RushRemaining = Mathf.Max(0f, RushRemaining - delta);
        if (RushRemaining <= 0f) IsFinisherRush = false;
    }

    public Vector3 GetStandingPosition(float normalizedHeight)
    {
        if (!initialized) CaptureOrigin();
        Vector3 center = originPosition + transform.TransformVector(bodyCenterLocal);
        return center + forwardDirection * (ForwardOffset * TrackingTravel) + Vector3.up * (originalBodyHeight * (normalizedHeight - 0.5f));
    }

    public Vector3 GetRearmostStandingPosition(float normalizedHeight)
    {
        Vector3 current = GetStandingPosition(normalizedHeight);
        return current - forwardDirection * (ForwardOffset * TrackingTravel + forwardDistance);
    }

    public bool TryReceiveAttack(Vector3 start, Vector3 end, float radius)
    {
        return TryReceiveAttack(start, end, radius, out _);
    }

    public bool TryReceiveAttack(Vector3 start, Vector3 end, float radius, out Vector3 hitPosition)
    {
        if (!IntersectsBody(start, end, radius, out hitPosition)) return false;
        TakeDamage();
        return true;
    }

    private bool IntersectsBody(Vector3 start, Vector3 end, float radius, out Vector3 hitPosition)
    {
        hitPosition = start;
        if (!gameplayEnabled || !isActiveAndEnabled || bodyCollider == null || !bodyCollider.enabled || !bodyCollider.gameObject.activeInHierarchy) return false;

        Transform body = bodyCollider.transform;
        Vector3 scale = body.lossyScale;
        float padding = Mathf.Max(0f, radius);
        Vector3 localPadding = new Vector3(padding / Mathf.Max(0.0001f, Mathf.Abs(scale.x)), padding / Mathf.Max(0.0001f, Mathf.Abs(scale.y)), padding / Mathf.Max(0.0001f, Mathf.Abs(scale.z)));
        Bounds bounds = new Bounds(bodyCollider.center, bodyCollider.size + localPadding * 2f);
        Vector3 localStart = body.InverseTransformPoint(start);
        Vector3 segment = body.InverseTransformPoint(end) - localStart;
        bool hit = bounds.Contains(localStart);
        if (!hit && segment.sqrMagnitude > 0.00000001f)
        {
            hit = bounds.IntersectRay(new Ray(localStart, segment.normalized), out float distance) && distance <= segment.magnitude;
            if (hit) hitPosition = body.TransformPoint(localStart + segment.normalized * distance);
        }

        return hit;
    }

    public void TakeDamage()
    {
        if (IsFinisherRush || !gameplayEnabled || !isActiveAndEnabled || IsDead || Time.timeAsDouble < invulnerableUntil) return;
        CurrentHealth = Mathf.Max(0f, CurrentHealth - 10f);
        invulnerableUntil = Time.timeAsDouble + damageInvulnerability;
        if (IsDead) SetGameplayEnabled(false);
        BattleAudio.Play(FightSound.PlayerHit);
        Damaged?.Invoke();
        HealthChanged?.Invoke();
    }

    public void ResetHealth()
    {
        IsFinisherRush = false;
        RushAttempted = false;
        RushSucceeded = false;
        RushHits = 0;
        RushRemaining = 0f;
        hasUdpTarget = false;
        BattleVersion++;
        CurrentHealth = Mathf.Max(1f, maxHealth);
        invulnerableUntil = double.NegativeInfinity;
        AttackInput = 0f;
        HealthChanged?.Invoke();
    }

    public void SetMaxHealth(int value)
    {
        maxHealth = FightSettings.ClampHealth(value);
        ResetHealth();
    }

    public void ResetBattle()
    {
        if (selectedOpponent == null && target == null && dilatantTarget == null) return;
        ResetHealth();
        SetGameplayEnabled(true);
        BattleAudio.StopAll();
        BattleAudio.Play(FightSound.Gong);
        ResetOpponent();
        ResetTrackingOffsets();
    }

    private float MinimumNormalizedHeight()
    {
        return originalBodyHeight > 0f ? Mathf.Clamp(minimumBodyHeight / originalBodyHeight, 0.0001f, 1f) : 0.01f;
    }

    private void ResetOpponent()
    {
        if (gameplayEnabled && dilatantTarget != null && dilatantTarget.isActiveAndEnabled)
        {
            dilatantTarget.ResetEnemy();
            return;
        }

        if (!gameplayEnabled || target == null || !target.isActiveAndEnabled) return;
        target.ResetPose();
    }

    private void UpdateForwardReference()
    {
        forwardDirection = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        if (forwardDirection.sqrMagnitude < 0.0001f) forwardDirection = Vector3.forward;
        forwardDistance = 0f;
        Transform opponent = selectedOpponent != null ? selectedOpponent : (target != null ? target.transform : null);
        if (opponent == null) return;

        Vector3 toEnemy = Vector3.ProjectOnPlane(opponent.position - originPosition, Vector3.up);
        forwardDistance = toEnemy.magnitude;
        if (forwardDistance > 0.0001f) forwardDirection = toEnemy / forwardDistance;
        float enemySurface = float.PositiveInfinity;
        foreach (Renderer renderer in opponent.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer.enabled || !IsActiveBelowRoot(renderer.transform, opponent)) continue;
            Bounds bounds;
            if (renderer is SkinnedMeshRenderer skinned) bounds = skinned.localBounds;
            else if (renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null) bounds = filter.sharedMesh.bounds;
            else continue;
            ProjectBounds(bounds, renderer.localToWorldMatrix, out float minimum, out _);
            enemySurface = Mathf.Min(enemySurface, minimum);
        }
        if (float.IsPositiveInfinity(enemySurface))
        {
            foreach (Collider collider in opponent.GetComponentsInChildren<Collider>(true))
            {
                if (!collider.enabled || collider.isTrigger || !IsActiveBelowRoot(collider.transform, opponent)) continue;
                if (collider is BoxCollider box)
                {
                    ProjectBounds(new Bounds(box.center, box.size), box.transform.localToWorldMatrix, out float minimum, out _);
                    enemySurface = Mathf.Min(enemySurface, minimum);
                }
                else if (collider.bounds.size.sqrMagnitude > 0f)
                {
                    ProjectBounds(collider.bounds, Matrix4x4.identity, out float minimum, out _);
                    enemySurface = Mathf.Min(enemySurface, minimum);
                }
            }
        }
        if (float.IsPositiveInfinity(enemySurface)) enemySurface = forwardDistance;
        float playerFront = 0f;
        if (bodyCollider != null) ProjectBounds(new Bounds(bodyCollider.center, bodyCollider.size), standingBodyLocalToWorld, out _, out playerFront);
        forwardDistance = Mathf.Max(0f, enemySurface - playerFront - enemySurfacePadding);
    }

    private void ProjectBounds(Bounds bounds, Matrix4x4 matrix, out float minimum, out float maximum)
    {
        minimum = float.PositiveInfinity;
        maximum = float.NegativeInfinity;
        for (int index = 0; index < 8; index++)
        {
            Vector3 corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((index & 1) == 0 ? -1f : 1f, (index & 2) == 0 ? -1f : 1f, (index & 4) == 0 ? -1f : 1f));
            float projection = Vector3.Dot(matrix.MultiplyPoint3x4(corner) - originPosition, forwardDirection);
            minimum = Mathf.Min(minimum, projection);
            maximum = Mathf.Max(maximum, projection);
        }
    }

    private static bool IsActiveBelowRoot(Transform child, Transform root)
    {
        for (Transform current = child; current != root && current != null; current = current.parent)
        {
            if (!current.gameObject.activeSelf) return false;
        }
        return true;
    }

    private void CaptureCameraPoses()
    {
        Camera[] cameras = GetComponentsInChildren<Camera>(true);
        fixedCameraPoses = new FixedCameraPose[cameras.Length];
        for (int index = 0; index < cameras.Length; index++)
        {
            Transform cameraTransform = cameras[index].transform;
            fixedCameraPoses[index] = new FixedCameraPose { Camera = cameraTransform, Position = cameraTransform.position, Rotation = cameraTransform.rotation };
        }
    }

    private void RestoreCameraPoses()
    {
        if (!keepCamerasFixed || fixedCameraPoses == null) return;
        foreach (FixedCameraPose pose in fixedCameraPoses)
        {
            if (pose.Camera != null) pose.Camera.SetPositionAndRotation(pose.Position, pose.Rotation);
        }
    }

    private static float Axis(bool positive, bool negative)
    {
        return (positive ? 1f : 0f) - (negative ? 1f : 0f);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
