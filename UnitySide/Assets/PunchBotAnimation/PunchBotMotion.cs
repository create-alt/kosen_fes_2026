using System;
using UnityEngine;

[RequireComponent(typeof(Animator))]
public sealed class PunchBotMotion : MonoBehaviour
{
    [SerializeField, Min(1f)] private float maxHealth = 100f;
    public float CurrentHealth { get; private set; } = 100f;
    public float MaxHealth => maxHealth;
    public int DifficultyStage => CombatDifficulty.ForHealth(CurrentHealth, maxHealth).Stage;
    public event Action HitReacted;
    private static readonly int IdleState = Animator.StringToHash("Base Layer.Idle");
    private static readonly int HitState = Animator.StringToHash("Base Layer.Hit");
    private static readonly int DeathState = Animator.StringToHash("Base Layer.Death");
    private static readonly int PunchAttackState = Animator.StringToHash("Base Layer.PunchAttack");
    private static readonly int JabAttackState = Animator.StringToHash("Base Layer.JabAttack");
    private static readonly int BoxingGuardState = Animator.StringToHash("Base Layer.BoxingGuard");
    private static readonly int[] MissileAttackStates =
    {
        Animator.StringToHash("Base Layer.MissileAttack1"),
        Animator.StringToHash("Base Layer.MissileAttack2"),
        Animator.StringToHash("Base Layer.MissileAttack3")
    };

    private Animator animator;
    private bool punching;
    private bool boxingGuard;

    public bool IsDead { get; private set; }
    public bool IsAttacking { get; private set; }
    public bool LastAttackInterrupted { get; private set; }
    public event Action MissileLaunch;
    public event Action PunchImpact;
    public event Action AttackEnded;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        CurrentHealth = Mathf.Max(1f, maxHealth);
    }

    private void OnDisable()
    {
        EndMissileAttack(true);
    }

    public bool PlayMissileAttack(int missileCount)
    {
        if (!isActiveAndEnabled || IsDead || IsAttacking) return false;
        if (missileCount < 1 || missileCount > MissileAttackStates.Length) return false;

        Animator targetAnimator = GetAnimator();
        int state = MissileAttackStates[missileCount - 1];
        if (!targetAnimator.isActiveAndEnabled || !targetAnimator.HasState(0, state)) return false;

        IsAttacking = true;
        LastAttackInterrupted = false;
        punching = false;
        targetAnimator.Play(state, 0, 0f);
        return true;
    }

    public void OnMissileLaunch()
    {
        if (!isActiveAndEnabled || IsDead || !IsAttacking || punching) return;
        MissileLaunch?.Invoke();
    }

    public bool PlayPunchAttack(bool useJab = false)
    {
        if (!isActiveAndEnabled || IsDead || IsAttacking) return false;
        Animator targetAnimator = GetAnimator();
        int state = useJab ? JabAttackState : PunchAttackState;
        if (!targetAnimator.isActiveAndEnabled || !targetAnimator.HasState(0, state)) return false;
        IsAttacking = true;
        LastAttackInterrupted = false;
        BattleAudio.Play(FightSound.EnemyPunch);
        punching = true;
        targetAnimator.Play(state, 0, 0f);
        return true;
    }

    public void SetBoxingGuard(bool enabled)
    {
        boxingGuard = enabled;
        if (!isActiveAndEnabled || IsDead || IsAttacking) return;
        Animator targetAnimator = GetAnimator();
        if (!targetAnimator.isActiveAndEnabled || !targetAnimator.HasState(0, BoxingGuardState)) return;

        int currentState = targetAnimator.GetCurrentAnimatorStateInfo(0).fullPathHash;
        if (enabled && currentState == IdleState) targetAnimator.Play(BoxingGuardState, 0, 0f);
        if (!enabled && currentState == BoxingGuardState) targetAnimator.Play(IdleState, 0, 0f);
    }

    public void OnPunchImpact()
    {
        if (!isActiveAndEnabled || IsDead || !IsAttacking || !punching) return;
        PunchImpact?.Invoke();
    }

    public void OnMissileAttackFinished()
    {
        if (!IsAttacking) return;
        EndMissileAttack();
        if (boxingGuard && !IsDead && GetAnimator().HasState(0, BoxingGuardState)) GetAnimator().Play(BoxingGuardState, 0, 0f);
    }

    public void StopMissileAttack()
    {
        if (!IsAttacking) return;
        EndMissileAttack(true);
        if (!IsDead) GetAnimator().Play(IdleState, 0, 0f);
    }

    public void PlayHit()
    {
        if (IsDead) return;
        HitReacted?.Invoke();
        EndMissileAttack(true);
        GetAnimator().Play(HitState, 0, 0f);
    }

    public void PlayDeath()
    {
        if (IsDead) return;
        CurrentHealth = 0f;
        IsDead = true;
        EndMissileAttack(true);
        GetAnimator().Play(DeathState, 0, 0f);
    }

    public void ResetPose()
    {
        EndMissileAttack(true);
        IsDead = false;
        CurrentHealth = Mathf.Max(1f, maxHealth);
        boxingGuard = false;
        GetAnimator().Play(IdleState, 0, 0f);
    }

    public void ReceiveDamage(float damage)
    {
        if (IsDead || !isActiveAndEnabled || float.IsNaN(damage) || float.IsInfinity(damage) || damage <= 0f) return;
        CurrentHealth = Mathf.Max(0f, CurrentHealth - damage);
        if (CurrentHealth <= 0f) PlayDeath();
        else PlayHit();
    }

    public void ReceiveRushPunch(float damage, bool finishing)
    {
        if (IsDead || !isActiveAndEnabled) return;
        if (finishing) PlayDeath();
        else
        {
            CurrentHealth = Mathf.Max(1f, CurrentHealth - damage);
            PlayHit();
        }
    }

    public void SetMaxHealth(int value)
    {
        maxHealth = FightSettings.ClampHealth(value);
        ResetPose();
    }

    private void EndMissileAttack(bool interrupted = false)
    {
        if (!IsAttacking) return;
        IsAttacking = false;
        LastAttackInterrupted = interrupted;
        punching = false;
        AttackEnded?.Invoke();
    }

    private Animator GetAnimator()
    {
        if (animator == null) animator = GetComponent<Animator>();
        return animator;
    }
}
