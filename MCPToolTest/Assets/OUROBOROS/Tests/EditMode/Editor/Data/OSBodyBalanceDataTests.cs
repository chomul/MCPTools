#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class OSBodyBalanceDataTests
{
    [Test]
    public void AssetMenu_UsesExpectedBodyBalancePath()
    {
        CreateAssetMenuAttribute attribute = (CreateAssetMenuAttribute)Attribute.GetCustomAttribute(
            typeof(OSBodyBalanceData),
            typeof(CreateAssetMenuAttribute));

        Assert.That(attribute, Is.Not.Null);
        Assert.That(attribute.fileName, Is.EqualTo("BodyBalance"));
        Assert.That(attribute.menuName, Is.EqualTo("OUROBOROS/Data/Body Balance"));
    }

    [Test]
    public void DefaultConfiguration_IsAcceptedAndExposesExpectedValues()
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.Accepted));
        Assert.That(data.SegmentFollowSpacing, Is.EqualTo(0.45f));
        Assert.That(data.BodyFragmentsPerSegment, Is.EqualTo(12));
        Assert.That(data.TechnicalSegmentLimit, Is.EqualTo(64));
        Assert.That(data.CutProtectionDuration, Is.EqualTo(0.35f));
        Assert.That(data.HeadDamageMultiplierPerSegment, Is.EqualTo(0.04f));
        Assert.That(data.AuxiliaryProjectileInterval, Is.EqualTo(5));
        Assert.That(data.MinimumExplosionSegments, Is.EqualTo(4));
        Assert.That(data.ExplosionConsumptionRatio, Is.EqualTo(0.30f));
        Assert.That(data.ExplosionDamagePerSegment, Is.EqualTo(35f));
        Assert.That(data.ExplosionRadiusPerSegment, Is.EqualTo(1.8f));
        Assert.That(data.ExplosionTelegraphDuration, Is.EqualTo(0.25f));
        Assert.That(data.ExplosionHeadInvulnerabilityDuration, Is.EqualTo(0.4f));

        Assert.That(data.Shield.Radius, Is.EqualTo(1.5f));
        Assert.That(data.Shield.Charges, Is.EqualTo(1));
        Assert.That(data.Shield.RechargeDuration, Is.EqualTo(6f));
        Assert.That(data.Attack.Range, Is.EqualTo(6f));
        Assert.That(data.Attack.Cooldown, Is.EqualTo(1f));
        Assert.That(data.Attack.Damage, Is.EqualTo(6f));
        Assert.That(data.Laser.Range, Is.EqualTo(7f));
        Assert.That(data.Laser.Cooldown, Is.EqualTo(2.5f));
        Assert.That(data.Laser.Damage, Is.EqualTo(12f));
        Assert.That(data.Laser.TelegraphDuration, Is.EqualTo(0.2f));
        Assert.That(data.Laser.Width, Is.EqualTo(0.35f));
        Assert.That(data.Laser.Length, Is.EqualTo(7f));
        Assert.That(data.Control.Range, Is.EqualTo(6f));
        Assert.That(data.Control.Cooldown, Is.EqualTo(4f));
        Assert.That(data.Control.ProjectileDamage, Is.EqualTo(0f));
        Assert.That(data.Control.NormalLockDuration, Is.EqualTo(1f));
        Assert.That(data.Control.EliteBossLockDuration, Is.EqualTo(0.5f));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void PreviousMaxTwentyAndFiftyPercentExplosion_AreNotP0Values()
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();

        Assert.That(data.TechnicalSegmentLimit, Is.EqualTo(64));
        Assert.That(data.TechnicalSegmentLimit, Is.Not.EqualTo(20));
        Assert.That(data.ExplosionConsumptionRatio, Is.EqualTo(0.30f));
        Assert.That(data.ExplosionConsumptionRatio, Is.Not.EqualTo(0.50f));
        Assert.That(FindInstanceField("maxBodySegments"), Is.Null);
        Assert.That(FindInstanceField("maximumBodySegments"), Is.Null);
        Assert.That(FindInstanceField("bodyHardCap"), Is.Null);

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void ExplosionConsumption_UsesThirtyPercentCeilExamples()
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();

        Assert.That(data.CanExplode(3), Is.False);
        Assert.That(data.CalculateExplosionConsumedSegments(3), Is.EqualTo(0));
        Assert.That(data.CalculateExplosionConsumedSegments(4), Is.EqualTo(2));
        Assert.That(data.CalculateExplosionConsumedSegments(5), Is.EqualTo(2));
        Assert.That(data.CalculateExplosionConsumedSegments(10), Is.EqualTo(3));
        Assert.That(data.CalculateExplosionConsumedSegments(64), Is.EqualTo(20));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void RoleBalances_AreSerializedAndNonNull()
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();

        Assert.That(data.Shield, Is.Not.Null);
        Assert.That(data.Attack, Is.Not.Null);
        Assert.That(data.Laser, Is.Not.Null);
        Assert.That(data.Control, Is.Not.Null);
        Assert.That(FindInstanceField("shield"), Is.Not.Null);
        Assert.That(FindInstanceField("attack"), Is.Not.Null);
        Assert.That(FindInstanceField("laser"), Is.Not.Null);
        Assert.That(FindInstanceField("control"), Is.Not.Null);

        UnityEngine.Object.DestroyImmediate(data);
    }

    [TestCase("segmentFollowSpacing", 0f)]
    [TestCase("segmentFollowSpacing", -1f)]
    [TestCase("cutProtectionDuration", 0f)]
    [TestCase("headDamageMultiplierPerSegment", 0f)]
    [TestCase("explosionDamagePerSegment", -1f)]
    [TestCase("explosionRadiusPerSegment", 0f)]
    [TestCase("explosionTelegraphDuration", float.PositiveInfinity)]
    [TestCase("explosionHeadInvulnerabilityDuration", float.NaN)]
    public void InvalidTopLevelFloatValues_AreRejected(string fieldName, float value)
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        SetPrivateField(data, fieldName, value);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [TestCase("bodyFragmentsPerSegment", 0)]
    [TestCase("technicalSegmentLimit", 0)]
    [TestCase("auxiliaryProjectileInterval", 0)]
    [TestCase("minimumExplosionSegments", 0)]
    public void InvalidTopLevelIntegerValues_AreRejected(string fieldName, int value)
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        SetPrivateField(data, fieldName, value);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [TestCase(0f)]
    [TestCase(-0.1f)]
    [TestCase(1.1f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void InvalidExplosionRatio_IsRejected(float value)
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        SetPrivateField(data, "explosionConsumptionRatio", value);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void TechnicalLimitBelowFragmentRequirement_IsRejected()
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        SetPrivateField(data, "technicalSegmentLimit", 4);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [TestCase("shield")]
    [TestCase("attack")]
    [TestCase("laser")]
    [TestCase("control")]
    public void NullRoleBalances_AreRejected(string fieldName)
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        SetPrivateField(data, fieldName, null);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void InvalidRoleValues_AreRejected()
    {
        AssertRoleValueRejected(data => SetPrivateField(data.Shield, "radius", 0f));
        AssertRoleValueRejected(data => SetPrivateField(data.Shield, "charges", 0));
        AssertRoleValueRejected(data => SetPrivateField(data.Attack, "damage", 0f));
        AssertRoleValueRejected(data => SetPrivateField(data.Laser, "width", float.NaN));
        AssertRoleValueRejected(data => SetPrivateField(data.Control, "projectileDamage", -1f));
        AssertRoleValueRejected(data => SetPrivateField(data.Control, "normalLockDuration", float.PositiveInfinity));
    }

    [Test]
    public void Snapshot_IsValueCopyAndDoesNotMutateSourceAsset()
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        OSBodyBalanceSnapshot snapshot = data.CreateSnapshot();

        SetPrivateField(data, "bodyFragmentsPerSegment", 99);
        SetPrivateField(data.Attack, "damage", 99f);
        OSBodyBalanceSnapshot modifiedSnapshot = new OSBodyBalanceSnapshot(
            snapshot.SegmentFollowSpacing,
            777,
            snapshot.TechnicalSegmentLimit,
            snapshot.CutProtectionDuration,
            snapshot.HeadDamageMultiplierPerSegment,
            snapshot.AuxiliaryProjectileInterval,
            snapshot.Shield,
            snapshot.Attack,
            snapshot.Laser,
            snapshot.Control,
            snapshot.MinimumExplosionSegments,
            snapshot.ExplosionConsumptionRatio,
            snapshot.ExplosionDamagePerSegment,
            snapshot.ExplosionRadiusPerSegment,
            snapshot.ExplosionTelegraphDuration,
            snapshot.ExplosionHeadInvulnerabilityDuration);

        Assert.That(snapshot.BodyFragmentsPerSegment, Is.EqualTo(12));
        Assert.That(snapshot.Attack.Damage, Is.EqualTo(6f));
        Assert.That(modifiedSnapshot.BodyFragmentsPerSegment, Is.EqualTo(777));
        Assert.That(data.BodyFragmentsPerSegment, Is.EqualTo(99));
        Assert.That(data.Attack.Damage, Is.EqualTo(99f));

        UnityEngine.Object.DestroyImmediate(data);
    }

    private static void AssertRoleValueRejected(Action<OSBodyBalanceData> mutate)
    {
        OSBodyBalanceData data = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        mutate(data);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    private static FieldInfo FindInstanceField(string fieldName)
    {
        return typeof(OSBodyBalanceData).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field: {fieldName}");
        field.SetValue(target, value);
    }
}
#endif
