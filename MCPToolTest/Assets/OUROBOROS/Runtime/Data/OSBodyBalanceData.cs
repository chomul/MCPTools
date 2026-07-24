using System;
using UnityEngine;

[CreateAssetMenu(fileName = "BodyBalance", menuName = "OUROBOROS/Data/Body Balance")]
public sealed class OSBodyBalanceData : ScriptableObject
{
    [Header("Chain")]
    [SerializeField] private float segmentFollowSpacing = 0.45f;
    [SerializeField] private int bodyFragmentsPerSegment = 12;
    [SerializeField] private int technicalSegmentLimit = 64;
    [SerializeField] private float cutProtectionDuration = 0.35f;

    [Header("Shared Firepower")]
    [SerializeField] private float headDamageMultiplierPerSegment = 0.04f;
    [SerializeField] private int auxiliaryProjectileInterval = 5;

    [Header("Roles")]
    [SerializeField] private OSShieldRoleBalance shield = new OSShieldRoleBalance();
    [SerializeField] private OSAttackRoleBalance attack = new OSAttackRoleBalance();
    [SerializeField] private OSLaserRoleBalance laser = new OSLaserRoleBalance();
    [SerializeField] private OSControlRoleBalance control = new OSControlRoleBalance();

    [Header("Explosion")]
    [SerializeField] private int minimumExplosionSegments = 4;
    [SerializeField] private float explosionConsumptionRatio = 0.30f;
    [SerializeField] private float explosionDamagePerSegment = 35f;
    [SerializeField] private float explosionRadiusPerSegment = 1.8f;
    [SerializeField] private float explosionTelegraphDuration = 0.25f;
    [SerializeField] private float explosionHeadInvulnerabilityDuration = 0.4f;

    public float SegmentFollowSpacing => segmentFollowSpacing;
    public int BodyFragmentsPerSegment => bodyFragmentsPerSegment;
    public int TechnicalSegmentLimit => technicalSegmentLimit;
    public float CutProtectionDuration => cutProtectionDuration;
    public float HeadDamageMultiplierPerSegment => headDamageMultiplierPerSegment;
    public int AuxiliaryProjectileInterval => auxiliaryProjectileInterval;
    public OSShieldRoleBalance Shield => shield;
    public OSAttackRoleBalance Attack => attack;
    public OSLaserRoleBalance Laser => laser;
    public OSControlRoleBalance Control => control;
    public int MinimumExplosionSegments => minimumExplosionSegments;
    public float ExplosionConsumptionRatio => explosionConsumptionRatio;
    public float ExplosionDamagePerSegment => explosionDamagePerSegment;
    public float ExplosionRadiusPerSegment => explosionRadiusPerSegment;
    public float ExplosionTelegraphDuration => explosionTelegraphDuration;
    public float ExplosionHeadInvulnerabilityDuration => explosionHeadInvulnerabilityDuration;

    public bool CanExplode(int activeSegmentCount)
    {
        return activeSegmentCount >= minimumExplosionSegments;
    }

    public int CalculateExplosionConsumedSegments(int activeSegmentCount)
    {
        if (!CanExplode(activeSegmentCount))
        {
            return 0;
        }

        return Mathf.Max(1, Mathf.CeilToInt(activeSegmentCount * explosionConsumptionRatio));
    }

    public OSConfigurationValidationResult ValidateConfiguration()
    {
        if (!IsPositiveFinite(segmentFollowSpacing) ||
            !IsPositive(bodyFragmentsPerSegment) ||
            !IsPositive(technicalSegmentLimit) ||
            technicalSegmentLimit < bodyFragmentsPerSegment ||
            !IsPositiveFinite(cutProtectionDuration) ||
            !IsPositiveFinite(headDamageMultiplierPerSegment) ||
            !IsPositive(auxiliaryProjectileInterval) ||
            shield == null ||
            attack == null ||
            laser == null ||
            control == null ||
            !shield.IsValid() ||
            !attack.IsValid() ||
            !laser.IsValid() ||
            !control.IsValid() ||
            !IsPositive(minimumExplosionSegments) ||
            !IsRatio(explosionConsumptionRatio) ||
            !IsPositiveFinite(explosionDamagePerSegment) ||
            !IsPositiveFinite(explosionRadiusPerSegment) ||
            !IsPositiveFinite(explosionTelegraphDuration) ||
            !IsPositiveFinite(explosionHeadInvulnerabilityDuration))
        {
            return OSConfigurationValidationResult.ConfigurationError;
        }

        return OSConfigurationValidationResult.Accepted;
    }

    public OSBodyBalanceSnapshot CreateSnapshot()
    {
        return new OSBodyBalanceSnapshot(
            segmentFollowSpacing,
            bodyFragmentsPerSegment,
            technicalSegmentLimit,
            cutProtectionDuration,
            headDamageMultiplierPerSegment,
            auxiliaryProjectileInterval,
            shield.CreateSnapshot(),
            attack.CreateSnapshot(),
            laser.CreateSnapshot(),
            control.CreateSnapshot(),
            minimumExplosionSegments,
            explosionConsumptionRatio,
            explosionDamagePerSegment,
            explosionRadiusPerSegment,
            explosionTelegraphDuration,
            explosionHeadInvulnerabilityDuration);
    }

    private void OnValidate()
    {
        ValidateConfiguration();
    }

    internal static bool IsPositive(int value)
    {
        return value > 0;
    }

    internal static bool IsPositiveFinite(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal static bool IsNonNegativeFinite(float value)
    {
        return value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal static bool IsRatio(float value)
    {
        return value > 0f && value <= 1f && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

[Serializable]
public sealed class OSShieldRoleBalance
{
    [SerializeField] private float radius = 1.5f;
    [SerializeField] private int charges = 1;
    [SerializeField] private float rechargeDuration = 6f;

    public float Radius => radius;
    public int Charges => charges;
    public float RechargeDuration => rechargeDuration;

    public bool IsValid()
    {
        return OSBodyBalanceData.IsPositiveFinite(radius) &&
            OSBodyBalanceData.IsPositive(charges) &&
            OSBodyBalanceData.IsPositiveFinite(rechargeDuration);
    }

    public OSShieldRoleBalanceSnapshot CreateSnapshot()
    {
        return new OSShieldRoleBalanceSnapshot(radius, charges, rechargeDuration);
    }
}

[Serializable]
public sealed class OSAttackRoleBalance
{
    [SerializeField] private float range = 6f;
    [SerializeField] private float cooldown = 1f;
    [SerializeField] private float damage = 6f;

    public float Range => range;
    public float Cooldown => cooldown;
    public float Damage => damage;

    public bool IsValid()
    {
        return OSBodyBalanceData.IsPositiveFinite(range) &&
            OSBodyBalanceData.IsPositiveFinite(cooldown) &&
            OSBodyBalanceData.IsPositiveFinite(damage);
    }

    public OSAttackRoleBalanceSnapshot CreateSnapshot()
    {
        return new OSAttackRoleBalanceSnapshot(range, cooldown, damage);
    }
}

[Serializable]
public sealed class OSLaserRoleBalance
{
    [SerializeField] private float range = 7f;
    [SerializeField] private float cooldown = 2.5f;
    [SerializeField] private float damage = 12f;
    [SerializeField] private float telegraphDuration = 0.2f;
    [SerializeField] private float width = 0.35f;
    [SerializeField] private float length = 7f;

    public float Range => range;
    public float Cooldown => cooldown;
    public float Damage => damage;
    public float TelegraphDuration => telegraphDuration;
    public float Width => width;
    public float Length => length;

    public bool IsValid()
    {
        return OSBodyBalanceData.IsPositiveFinite(range) &&
            OSBodyBalanceData.IsPositiveFinite(cooldown) &&
            OSBodyBalanceData.IsPositiveFinite(damage) &&
            OSBodyBalanceData.IsPositiveFinite(telegraphDuration) &&
            OSBodyBalanceData.IsPositiveFinite(width) &&
            OSBodyBalanceData.IsPositiveFinite(length);
    }

    public OSLaserRoleBalanceSnapshot CreateSnapshot()
    {
        return new OSLaserRoleBalanceSnapshot(range, cooldown, damage, telegraphDuration, width, length);
    }
}

[Serializable]
public sealed class OSControlRoleBalance
{
    [SerializeField] private float range = 6f;
    [SerializeField] private float cooldown = 4f;
    [SerializeField] private float projectileDamage = 0f;
    [SerializeField] private float normalLockDuration = 1f;
    [SerializeField] private float eliteBossLockDuration = 0.5f;

    public float Range => range;
    public float Cooldown => cooldown;
    public float ProjectileDamage => projectileDamage;
    public float NormalLockDuration => normalLockDuration;
    public float EliteBossLockDuration => eliteBossLockDuration;

    public bool IsValid()
    {
        return OSBodyBalanceData.IsPositiveFinite(range) &&
            OSBodyBalanceData.IsPositiveFinite(cooldown) &&
            OSBodyBalanceData.IsNonNegativeFinite(projectileDamage) &&
            OSBodyBalanceData.IsPositiveFinite(normalLockDuration) &&
            OSBodyBalanceData.IsPositiveFinite(eliteBossLockDuration);
    }

    public OSControlRoleBalanceSnapshot CreateSnapshot()
    {
        return new OSControlRoleBalanceSnapshot(
            range,
            cooldown,
            projectileDamage,
            normalLockDuration,
            eliteBossLockDuration);
    }
}

[Serializable]
public readonly struct OSBodyBalanceSnapshot
{
    public OSBodyBalanceSnapshot(
        float segmentFollowSpacing,
        int bodyFragmentsPerSegment,
        int technicalSegmentLimit,
        float cutProtectionDuration,
        float headDamageMultiplierPerSegment,
        int auxiliaryProjectileInterval,
        OSShieldRoleBalanceSnapshot shield,
        OSAttackRoleBalanceSnapshot attack,
        OSLaserRoleBalanceSnapshot laser,
        OSControlRoleBalanceSnapshot control,
        int minimumExplosionSegments,
        float explosionConsumptionRatio,
        float explosionDamagePerSegment,
        float explosionRadiusPerSegment,
        float explosionTelegraphDuration,
        float explosionHeadInvulnerabilityDuration)
    {
        SegmentFollowSpacing = segmentFollowSpacing;
        BodyFragmentsPerSegment = bodyFragmentsPerSegment;
        TechnicalSegmentLimit = technicalSegmentLimit;
        CutProtectionDuration = cutProtectionDuration;
        HeadDamageMultiplierPerSegment = headDamageMultiplierPerSegment;
        AuxiliaryProjectileInterval = auxiliaryProjectileInterval;
        Shield = shield;
        Attack = attack;
        Laser = laser;
        Control = control;
        MinimumExplosionSegments = minimumExplosionSegments;
        ExplosionConsumptionRatio = explosionConsumptionRatio;
        ExplosionDamagePerSegment = explosionDamagePerSegment;
        ExplosionRadiusPerSegment = explosionRadiusPerSegment;
        ExplosionTelegraphDuration = explosionTelegraphDuration;
        ExplosionHeadInvulnerabilityDuration = explosionHeadInvulnerabilityDuration;
    }

    public float SegmentFollowSpacing { get; }
    public int BodyFragmentsPerSegment { get; }
    public int TechnicalSegmentLimit { get; }
    public float CutProtectionDuration { get; }
    public float HeadDamageMultiplierPerSegment { get; }
    public int AuxiliaryProjectileInterval { get; }
    public OSShieldRoleBalanceSnapshot Shield { get; }
    public OSAttackRoleBalanceSnapshot Attack { get; }
    public OSLaserRoleBalanceSnapshot Laser { get; }
    public OSControlRoleBalanceSnapshot Control { get; }
    public int MinimumExplosionSegments { get; }
    public float ExplosionConsumptionRatio { get; }
    public float ExplosionDamagePerSegment { get; }
    public float ExplosionRadiusPerSegment { get; }
    public float ExplosionTelegraphDuration { get; }
    public float ExplosionHeadInvulnerabilityDuration { get; }
}

[Serializable]
public readonly struct OSShieldRoleBalanceSnapshot
{
    public OSShieldRoleBalanceSnapshot(float radius, int charges, float rechargeDuration)
    {
        Radius = radius;
        Charges = charges;
        RechargeDuration = rechargeDuration;
    }

    public float Radius { get; }
    public int Charges { get; }
    public float RechargeDuration { get; }
}

[Serializable]
public readonly struct OSAttackRoleBalanceSnapshot
{
    public OSAttackRoleBalanceSnapshot(float range, float cooldown, float damage)
    {
        Range = range;
        Cooldown = cooldown;
        Damage = damage;
    }

    public float Range { get; }
    public float Cooldown { get; }
    public float Damage { get; }
}

[Serializable]
public readonly struct OSLaserRoleBalanceSnapshot
{
    public OSLaserRoleBalanceSnapshot(
        float range,
        float cooldown,
        float damage,
        float telegraphDuration,
        float width,
        float length)
    {
        Range = range;
        Cooldown = cooldown;
        Damage = damage;
        TelegraphDuration = telegraphDuration;
        Width = width;
        Length = length;
    }

    public float Range { get; }
    public float Cooldown { get; }
    public float Damage { get; }
    public float TelegraphDuration { get; }
    public float Width { get; }
    public float Length { get; }
}

[Serializable]
public readonly struct OSControlRoleBalanceSnapshot
{
    public OSControlRoleBalanceSnapshot(
        float range,
        float cooldown,
        float projectileDamage,
        float normalLockDuration,
        float eliteBossLockDuration)
    {
        Range = range;
        Cooldown = cooldown;
        ProjectileDamage = projectileDamage;
        NormalLockDuration = normalLockDuration;
        EliteBossLockDuration = eliteBossLockDuration;
    }

    public float Range { get; }
    public float Cooldown { get; }
    public float ProjectileDamage { get; }
    public float NormalLockDuration { get; }
    public float EliteBossLockDuration { get; }
}
