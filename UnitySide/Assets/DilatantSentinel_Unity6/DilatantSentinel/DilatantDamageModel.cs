using System;

public enum DilatantReaction
{
    None,
    WeakHit,
    StrongBlock,
    Death
}

public sealed class DilatantDamageModel
{
    private readonly float maxHealth;
    private readonly float hardeningThreshold;
    private readonly float weakDamageMultiplier;

    public float Health { get; private set; }
    public bool IsDead => Health <= 0f;

    public DilatantDamageModel(float health, float threshold, float multiplier)
    {
        if (!IsFinite(health) || !IsFinite(threshold) || !IsFinite(multiplier)) throw new ArgumentException("Damage settings must be finite.");
        maxHealth = Math.Max(0.01f, health);
        hardeningThreshold = Math.Max(0.01f, threshold);
        weakDamageMultiplier = Math.Max(0.01f, multiplier);
        Health = maxHealth;
    }

    public DilatantReaction Apply(float damage, float impactStrength)
    {
        if (IsDead || !IsFinite(damage) || !IsFinite(impactStrength) || damage <= 0f || impactStrength < 0f) return DilatantReaction.None;
        if (impactStrength > hardeningThreshold) return DilatantReaction.StrongBlock;

        Health = Math.Max(0f, Health - damage * weakDamageMultiplier);
        return IsDead ? DilatantReaction.Death : DilatantReaction.WeakHit;
    }

    public void Reset()
    {
        Health = maxHealth;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
