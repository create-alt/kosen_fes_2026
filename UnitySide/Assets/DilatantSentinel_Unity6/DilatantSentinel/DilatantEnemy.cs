using UnityEngine;

[RequireComponent(typeof(Animator))]
public sealed class DilatantEnemy : MonoBehaviour
{
    [SerializeField, Min(0.01f)] private float maxHealth = 100f;
    [SerializeField, Min(0.05f)] private float comboResetInterval = 0.8f;

    private static readonly int IdleState = Animator.StringToHash("Base Layer.Idle");
    private static readonly int WeakHitState = Animator.StringToHash("Base Layer.WeakHit");
    private static readonly int StrongBlockState = Animator.StringToHash("Base Layer.StrongBlock");
    private static readonly int DeathState = Animator.StringToHash("Base Layer.Death");
    private Animator animator;
    private DilatantDamageModel damageModel;
    private double lastPunchTime = double.NegativeInfinity;
    private bool attackInputHeld;
    private int rapidPunchCount;

    public float CurrentHealth => damageModel == null ? maxHealth : damageModel.Health;
    public float MaxHealth => maxHealth;
    public bool IsDead => damageModel != null && damageModel.IsDead;
    public int DifficultyStage => CombatDifficulty.ForHealth(CurrentHealth, MaxHealth).Stage;
    public int RapidPunchCount => Time.timeAsDouble - lastPunchTime > comboResetInterval ? 0 : rapidPunchCount;

    private void Awake()
    {
        EnsureInitialized();
    }

    public void ReceivePhysicalAttack(float damage, float impactStrength)
    {
        if (!isActiveAndEnabled || IsDead || float.IsNaN(damage) || float.IsInfinity(damage) || damage <= 0f) return;
        if (float.IsNaN(impactStrength) || float.IsInfinity(impactStrength) || impactStrength < 0f) return;
        EnsureInitialized();
        double now = Time.timeAsDouble;
        if (now - lastPunchTime > comboResetInterval) rapidPunchCount = 0;
        rapidPunchCount = Mathf.Min(4, rapidPunchCount + 1);
        lastPunchTime = now;
        DilatantReaction reaction = damageModel.Apply(damage, rapidPunchCount > 3 ? 2f : 0f);

        switch (reaction)
        {
            case DilatantReaction.WeakHit:
                animator.Play(WeakHitState, 0, 0f);
                break;
            case DilatantReaction.StrongBlock:
                animator.Play(StrongBlockState, 0, 0f);
                break;
            case DilatantReaction.Death:
                animator.Play(DeathState, 0, 0f);
                break;
        }
    }

    public void ReceiveWeakAttack()
    {
        ReceivePunch();
    }

    public void ReceiveStrongAttack()
    {
        ReceivePunch();
    }

    public void ReceiveAttackInput(float value)
    {
        if (!isActiveAndEnabled || (value != 0f && value != 1f)) return;
        bool pressed = value == 1f && !attackInputHeld;
        attackInputHeld = value == 1f;
        if (pressed) ReceivePunch();
    }

    public void ReceivePunch()
    {
        ReceivePhysicalAttack(PlayerController.PunchDamage, 0f);
    }

    public void ResetEnemy()
    {
        EnsureInitialized();
        damageModel.Reset();
        ResetCombo();
        animator.Play(IdleState, 0, 0f);
    }

    public void ReceiveRushPunch(float damage, bool finishing)
    {
        if (IsDead || !isActiveAndEnabled) return;
        EnsureInitialized();
        damageModel.Apply(finishing ? CurrentHealth : Mathf.Min(damage, Mathf.Max(0f, CurrentHealth - 1f)), 0f);
        animator.Play(IsDead ? DeathState : WeakHitState, 0, 0f);
        ResetCombo();
    }

    public void SetMaxHealth(int value)
    {
        maxHealth = FightSettings.ClampHealth(value);
        damageModel = null;
        ResetEnemy();
    }

    private void EnsureInitialized()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (damageModel == null) damageModel = new DilatantDamageModel(maxHealth, 1f, 1f);
    }

    private void OnDisable()
    {
        ResetCombo();
    }

    private void ResetCombo()
    {
        rapidPunchCount = 0;
        lastPunchTime = double.NegativeInfinity;
        attackInputHeld = false;
    }
}
