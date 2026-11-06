using UnityEngine;

public readonly struct CombatDifficulty
{
    public int Stage { get; }
    public float Telegraph { get; }
    public float FlightTime { get; }
    public float Recovery { get; }
    public float BurstSpacing { get; }
    public int MaximumBurst { get; }
    public float MissileSpeed { get; }
    public float PressureTelegraph => Mathf.Max(0.75f, Telegraph * 0.55f);
    public float PressureRecovery => Mathf.Max(0.45f, Recovery * 0.4f);
    public float PressureSpacing => BurstSpacing * 0.4f;

    private CombatDifficulty(int stage)
    {
        Stage = stage;
        Telegraph = stage == 1 ? 1.6f : stage == 2 ? 1.35f : 1.1f;
        FlightTime = stage == 1 ? 1.3f : stage == 2 ? 1.1f : 0.9f;
        Recovery = stage == 1 ? 1.8f : stage == 2 ? 1.5f : 1.2f;
        BurstSpacing = stage == 1 ? 1.1f : stage == 2 ? 1f : 0.9f;
        MaximumBurst = stage;
        MissileSpeed = stage == 1 ? 3.5f : stage == 2 ? 4f : 4.5f;
        float tempo = FightSettings.Difficulty == 0 ? 1.3f : FightSettings.Difficulty == 2 ? 0.9f : 1f;
        Telegraph = Mathf.Max(1.1f, Telegraph * tempo);
        FlightTime = Mathf.Max(0.9f, FlightTime * tempo);
        Recovery = Mathf.Max(1.2f, Recovery * tempo);
        BurstSpacing = Mathf.Max(0.9f, BurstSpacing * tempo);
        MaximumBurst = FightSettings.Difficulty == 0 ? 1 : FightSettings.Difficulty == 2 ? Mathf.Min(3, stage + 1) : stage;
        MissileSpeed = Mathf.Min(4.5f, MissileSpeed / tempo);
    }

    public static CombatDifficulty ForHealth(float health, float maximum)
    {
        float ratio = health / Mathf.Max(1f, maximum);
        return new CombatDifficulty(ratio > 0.66f ? 1 : ratio > 0.33f ? 2 : 3);
    }
}
