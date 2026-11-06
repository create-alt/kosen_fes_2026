using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(50)]
[RequireComponent(typeof(DilatantEnemy), typeof(DilatantLiquidAttack))]
public sealed class HybridBossAttack : MonoBehaviour
{
    [SerializeField] private PlayerController player;
    [SerializeField] private GameObject missilePrefab;
    [SerializeField, Min(1f)] private float firstAttackDelay = 2.4f;
    private readonly List<StraightMissile> missiles = new List<StraightMissile>();
    private DilatantEnemy enemy;
    private DilatantLiquidAttack wave;
    private CombatDifficulty difficulty;
    private bool engaged;
    private bool cycleActive;
    private int battleVersion;
    private int cycleIndex;
    private int shotsRemaining;
    private int wavesRemaining;
    private float nextCycle;
    private float nextShot;
    private float nextWave;
    private float warningDuration;
    private float shotSpacing;
    private Material accentMaterial;
    private Transform firingArm;
    private Quaternion armRestRotation;
    private float lastShotTime = float.NegativeInfinity;
    private bool shotPoseActive;
    private Transform meleeArm;
    private Vector3 meleeArmRestPosition;
    private Quaternion meleeArmRestRotation;
    private bool meleeActive;
    private bool meleePredictionVisible;
    private bool meleeImpactApplied;
    private bool meleePoseActive;
    private Vector3 meleePosition;
    private float meleeRetreatLimit;
    private float nextMelee;
    private float meleeStarted;
    private float meleeSwingAt;
    private const float MeleeImpactTime = 0.18f;
    private const float MeleeDuration = 0.45f;

    public static event System.Action<HybridBossAttack> MeleePredictionStarted;
    public static event System.Action<HybridBossAttack> MeleePredictionEnded;
    public bool IsMeleePredicting => meleeActive && meleePredictionVisible && isActiveAndEnabled && player != null && player.GameplayEnabled && !player.IsFinisherRush && enemy != null && !enemy.IsDead;
    public Vector3 MeleePredictionPosition => meleePosition;
    public Vector3 MeleePredictionBottom => new Vector3(meleePosition.x, player != null ? player.GetStandingPosition(0f).y : meleePosition.y, meleePosition.z);
    public Vector3 MeleeIncomingDirection => transform.position - meleePosition;
    public Vector3 PlayerForwardDirection => player != null ? player.TrackingForwardDirection : Vector3.forward;

    private void Awake()
    {
        enemy = GetComponent<DilatantEnemy>();
        wave = GetComponent<DilatantLiquidAttack>();
        if (player == null) player = FindFirstObjectByType<PlayerController>();
        wave.ConfigureExternalControl(player);
        firingArm = transform.Find("Visual/Body/ArmR");
        if (firingArm != null) armRestRotation = firingArm.localRotation;
        meleeArm = transform.Find("Visual/Body/ArmL");
        if (meleeArm != null)
        {
            meleeArmRestPosition = meleeArm.localPosition;
            meleeArmRestRotation = meleeArm.localRotation;
        }
        PrepareAppearance();
        if (missilePrefab == null) Debug.LogError("HYBRID OVERDRIVEのMissile Prefabを設定してください。", this);
    }

    private void Update()
    {
        bool canFight = player != null && player.isActiveAndEnabled && player.GameplayEnabled && !player.IsDead && !player.IsFinisherRush && enemy != null && !enemy.IsDead && missilePrefab != null;
        if (!canFight)
        {
            if (engaged || cycleActive || meleeActive) StopAttacks();
            engaged = false;
            return;
        }
        if (!engaged || battleVersion != player.BattleVersion)
        {
            StopAttacks();
            engaged = true;
            battleVersion = player.BattleVersion;
            cycleIndex = 0;
            nextCycle = Time.time + firstAttackDelay;
            nextMelee = nextCycle;
        }
        missiles.RemoveAll(missile => missile == null);
        UpdateMelee();
        if (!player.GameplayEnabled || player.IsDead)
        {
            StopAttacks();
            engaged = false;
            return;
        }
        if (!cycleActive)
        {
            if (Time.time >= nextCycle) BeginCycle();
            return;
        }

        if (wavesRemaining > 0 && Time.time >= nextWave && !wave.IsBusy)
        {
            if (wave.TryStartWave(warningDuration + 0.15f, Mathf.Max(0.8f, difficulty.FlightTime * 0.85f)))
            {
                wavesRemaining--;
                nextWave = Time.time + 1.5f;
            }
        }
        if (shotsRemaining > 0 && Time.time >= nextShot)
        {
            FireShot();
            shotsRemaining--;
            nextShot = Time.time + shotSpacing;
        }
        if (shotsRemaining == 0 && wavesRemaining == 0 && !wave.IsBusy)
        {
            cycleActive = false;
            nextCycle = Time.time + Mathf.Max(0.8f, difficulty.Recovery * 0.65f);
        }
    }

    private void BeginCycle()
    {
        difficulty = CombatDifficulty.ForHealth(enemy.CurrentHealth, enemy.MaxHealth);
        warningDuration = Mathf.Max(0.9f, difficulty.Telegraph * 0.85f);
        shotSpacing = difficulty.Stage == 1 ? 0.75f : difficulty.Stage == 2 ? 0.65f : 0.55f;
        shotsRemaining = difficulty.Stage + 2;
        wavesRemaining = difficulty.Stage == 3 ? 2 : 1;
        if (difficulty.Stage == 1)
        {
            if (cycleIndex % 2 == 0) shotsRemaining = 0;
            else wavesRemaining = 0;
        }
        cycleIndex++;
        nextWave = Time.time;
        nextShot = Time.time + (wavesRemaining > 0 ? 0.65f : 0f);
        cycleActive = true;
    }

    private void FireShot()
    {
        GameObject projectile = Instantiate(missilePrefab, transform.position + Vector3.up, Quaternion.identity);
        if (!projectile.TryGetComponent(out StraightMissile missile)) missile = projectile.AddComponent<StraightMissile>();
        float speed = Mathf.Min(6f, difficulty.MissileSpeed + 0.8f);
        missile.Initialize(speed, 1f, Vector3.forward, Quaternion.identity, player.transform, directIncoming: true);
        missile.ConfigureBossFlight(warningDuration, speed, transform);
        missiles.Add(missile);
        lastShotTime = Time.time;
    }

    private void LateUpdate()
    {
        UpdateShotPose();
        ApplyMeleePose();
    }

    private void UpdateShotPose()
    {
        if (firingArm == null) return;
        if (wave.IsBusy || !engaged || enemy.IsDead || Time.time - lastShotTime > 0.4f)
        {
            if (shotPoseActive && !wave.IsBusy) firingArm.localRotation = armRestRotation;
            shotPoseActive = false;
            return;
        }
        float age = Time.time - lastShotTime;
        float thrust = age < 0.08f ? age / 0.08f : 1f - Mathf.SmoothStep(0f, 1f, (age - 0.08f) / 0.32f);
        firingArm.localRotation = armRestRotation * Quaternion.Euler(-80f * thrust, -12f * thrust, 0f);
        shotPoseActive = true;
    }

    private void UpdateMelee()
    {
        if (!meleeActive)
        {
            if (Time.time < nextMelee || !player.IsInEnemyPunchRange) return;
            CombatDifficulty profile = CombatDifficulty.ForHealth(enemy.CurrentHealth, enemy.MaxHealth);
            meleePosition = player.GetStandingPosition(0.75f);
            meleeRetreatLimit = player.ForwardOffset - 0.12f;
            meleeStarted = Time.time;
            meleeSwingAt = Time.time + Mathf.Max(1f, profile.Telegraph * 0.85f);
            meleeImpactApplied = false;
            meleeActive = true;
            meleePredictionVisible = true;
            enemy.PrioritizePunch = true;
            MeleePredictionStarted?.Invoke(this);
        }
        enemy.PrioritizePunch = true;
        if (Time.time < meleeSwingAt) return;
        if (meleePredictionVisible)
        {
            EndMeleePrediction();
            BattleAudio.Play(FightSound.EnemyPunch);
        }
        if (!meleeImpactApplied && Time.time >= meleeSwingAt + MeleeImpactTime)
        {
            meleeImpactApplied = true;
            float radius = Mathf.Max(0.14f, player.StandingBodyHeight * 0.09f);
            bool hit = player.ForwardOffset >= meleeRetreatLimit && player.TryReceiveLowAttack(meleePosition, meleePosition, radius);
            if (!hit) player.ReportDodge();
        }
        if (Time.time >= meleeSwingAt + MeleeDuration)
        {
            meleeActive = false;
            enemy.PrioritizePunch = false;
            CombatDifficulty profile = CombatDifficulty.ForHealth(enemy.CurrentHealth, enemy.MaxHealth);
            nextMelee = Time.time + Mathf.Max(1f, profile.Recovery * 0.85f);
        }
    }

    private void ApplyMeleePose()
    {
        if (meleeArm == null) return;
        if (!meleeActive)
        {
            RestoreMeleePose();
            return;
        }
        float charge = Mathf.Clamp01((Time.time - meleeStarted) / Mathf.Max(0.01f, meleeSwingAt - meleeStarted));
        float swing = Time.time - meleeSwingAt;
        float extension = swing < 0f ? 0f : swing <= MeleeImpactTime ? Mathf.SmoothStep(0f, 1f, swing / MeleeImpactTime) : 1f - Mathf.SmoothStep(0f, 1f, (swing - MeleeImpactTime) / (MeleeDuration - MeleeImpactTime));
        meleeArm.localPosition = meleeArmRestPosition + new Vector3(0.08f * extension, 0f, -0.08f * charge + 0.2f * extension);
        meleeArm.localRotation = meleeArmRestRotation * Quaternion.Euler(Mathf.Lerp(-45f * charge, -100f, extension), 25f * extension, 12f * charge);
        meleePoseActive = true;
    }

    private void RestoreMeleePose()
    {
        if (!meleePoseActive || meleeArm == null) return;
        if (wave == null || !wave.IsBusy)
        {
            meleeArm.localPosition = meleeArmRestPosition;
            meleeArm.localRotation = meleeArmRestRotation;
        }
        meleePoseActive = false;
    }

    private void EndMeleePrediction()
    {
        if (!meleePredictionVisible) return;
        meleePredictionVisible = false;
        MeleePredictionEnded?.Invoke(this);
    }

    private void StopAttacks()
    {
        EndMeleePrediction();
        meleeActive = false;
        meleeImpactApplied = false;
        if (enemy != null) enemy.PrioritizePunch = false;
        foreach (StraightMissile missile in missiles)
        {
            if (missile != null) missile.CancelForPlayer(player);
        }
        missiles.Clear();
        if (wave != null) wave.SuspendForFinisher();
        RestoreMeleePose();
        shotsRemaining = 0;
        wavesRemaining = 0;
        cycleActive = false;
        lastShotTime = float.NegativeInfinity;
        if (shotPoseActive && firingArm != null) firingArm.localRotation = armRestRotation;
        shotPoseActive = false;
    }

    private void PrepareAppearance()
    {
        Transform body = transform.Find("Visual/Body");
        if (body == null) return;
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        foreach (Renderer renderer in body.GetComponentsInChildren<Renderer>(true))
        {
            Color color = renderer.name.Contains("Arm") ? new Color(0.95f, 0.6f, 0.12f) : new Color(0.42f, 0.09f, 0.62f);
            renderer.GetPropertyBlock(block);
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            renderer.SetPropertyBlock(block);
        }
        Material template = Resources.Load<Material>("FightArena/ArenaUnlit");
        if (template == null) return;
        accentMaterial = new Material(template);
        accentMaterial.SetColor("_BaseColor", new Color(1f, 0.65f, 0.12f));
        Transform crown = new GameObject("OverdriveCrown").transform;
        crown.SetParent(body, false);
        for (int index = 0; index < 3; index++)
        {
            GameObject fin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fin.name = "CrownFin";
            fin.transform.SetParent(crown, false);
            fin.transform.localPosition = new Vector3((index - 1) * 0.28f, 0.7f, 0f);
            fin.transform.localScale = new Vector3(0.12f, index == 1 ? 0.5f : 0.35f, 0.18f);
            Collider collider = fin.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            fin.GetComponent<Renderer>().sharedMaterial = accentMaterial;
        }
    }

    private void OnDisable()
    {
        StopAttacks();
        engaged = false;
    }

    private void OnDestroy()
    {
        if (accentMaterial != null) Destroy(accentMaterial);
    }
}
