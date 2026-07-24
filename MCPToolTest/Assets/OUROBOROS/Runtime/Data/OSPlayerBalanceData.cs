using System;
using UnityEngine;

[CreateAssetMenu(fileName = "PlayerBalance", menuName = "OUROBOROS/Data/Player Balance")]
public sealed class OSPlayerBalanceData : ScriptableObject
{
    [Header("Vitals")]
    [SerializeField] private float hp = 100f;
    [SerializeField] private float invulnerabilityDuration = 0.6f;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 5.5f;

    [Header("Head Attack")]
    [SerializeField] private float headDamage = 10f;
    [SerializeField] private float headShotsPerSecond = 2f;
    [SerializeField] private float headRange = 6f;

    public float Hp => hp;
    public float MoveSpeed => moveSpeed;
    public float HeadDamage => headDamage;
    public float HeadShotsPerSecond => headShotsPerSecond;
    public float HeadAttackInterval => 1f / headShotsPerSecond;
    public float HeadRange => headRange;
    public float InvulnerabilityDuration => invulnerabilityDuration;

    public OSConfigurationValidationResult ValidateConfiguration()
    {
        if (!IsPositiveFinite(hp) ||
            !IsPositiveFinite(moveSpeed) ||
            !IsPositiveFinite(headDamage) ||
            !IsPositiveFinite(headShotsPerSecond) ||
            !IsPositiveFinite(headRange) ||
            !IsPositiveFinite(invulnerabilityDuration))
        {
            return OSConfigurationValidationResult.ConfigurationError;
        }

        return OSConfigurationValidationResult.Accepted;
    }

    public OSPlayerBalanceSnapshot CreateSnapshot()
    {
        return new OSPlayerBalanceSnapshot(
            hp,
            moveSpeed,
            headDamage,
            headShotsPerSecond,
            headRange,
            invulnerabilityDuration);
    }

    private void OnValidate()
    {
        ValidateConfiguration();
    }

    private static bool IsPositiveFinite(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

public enum OSConfigurationValidationResult
{
    Accepted,
    ConfigurationError
}

[Serializable]
public readonly struct OSPlayerBalanceSnapshot
{
    public OSPlayerBalanceSnapshot(
        float hp,
        float moveSpeed,
        float headDamage,
        float headShotsPerSecond,
        float headRange,
        float invulnerabilityDuration)
    {
        Hp = hp;
        MoveSpeed = moveSpeed;
        HeadDamage = headDamage;
        HeadShotsPerSecond = headShotsPerSecond;
        HeadRange = headRange;
        InvulnerabilityDuration = invulnerabilityDuration;
    }

    public float Hp { get; }
    public float MoveSpeed { get; }
    public float HeadDamage { get; }
    public float HeadShotsPerSecond { get; }
    public float HeadAttackInterval => 1f / HeadShotsPerSecond;
    public float HeadRange { get; }
    public float InvulnerabilityDuration { get; }
}
