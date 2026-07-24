#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class OSPlayerBalanceDataTests
{
    [Test]
    public void AssetMenu_UsesExpectedPlayerBalancePath()
    {
        CreateAssetMenuAttribute attribute = (CreateAssetMenuAttribute)System.Attribute.GetCustomAttribute(
            typeof(OSPlayerBalanceData),
            typeof(CreateAssetMenuAttribute));

        Assert.That(attribute, Is.Not.Null);
        Assert.That(attribute.fileName, Is.EqualTo("PlayerBalance"));
        Assert.That(attribute.menuName, Is.EqualTo("OUROBOROS/Data/Player Balance"));
    }

    [Test]
    public void DefaultConfiguration_IsAcceptedAndExposesExpectedValues()
    {
        OSPlayerBalanceData data = ScriptableObject.CreateInstance<OSPlayerBalanceData>();

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.Accepted));
        Assert.That(data.Hp, Is.EqualTo(100f));
        Assert.That(data.MoveSpeed, Is.EqualTo(5.5f));
        Assert.That(data.HeadDamage, Is.EqualTo(10f));
        Assert.That(data.HeadShotsPerSecond, Is.EqualTo(2f));
        Assert.That(data.HeadAttackInterval, Is.EqualTo(0.5f));
        Assert.That(data.HeadRange, Is.EqualTo(6f));
        Assert.That(data.InvulnerabilityDuration, Is.EqualTo(0.6f));

        Object.DestroyImmediate(data);
    }

    [TestCase("hp", 0f)]
    [TestCase("hp", -1f)]
    [TestCase("moveSpeed", 0f)]
    [TestCase("moveSpeed", -1f)]
    [TestCase("headDamage", 0f)]
    [TestCase("headDamage", -1f)]
    [TestCase("headShotsPerSecond", 0f)]
    [TestCase("headShotsPerSecond", -1f)]
    [TestCase("headRange", 0f)]
    [TestCase("headRange", -1f)]
    [TestCase("invulnerabilityDuration", 0f)]
    [TestCase("invulnerabilityDuration", -1f)]
    public void InvalidNonPositiveValues_AreRejected(string fieldName, float value)
    {
        OSPlayerBalanceData data = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        SetSerializedFloat(data, fieldName, value);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        Object.DestroyImmediate(data);
    }

    [TestCase("hp")]
    [TestCase("moveSpeed")]
    [TestCase("headDamage")]
    [TestCase("headShotsPerSecond")]
    [TestCase("headRange")]
    [TestCase("invulnerabilityDuration")]
    public void InvalidNaNAndInfinityValues_AreRejected(string fieldName)
    {
        OSPlayerBalanceData data = ScriptableObject.CreateInstance<OSPlayerBalanceData>();

        SetSerializedFloat(data, fieldName, float.NaN);
        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        SetSerializedFloat(data, fieldName, float.PositiveInfinity);
        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        SetSerializedFloat(data, fieldName, float.NegativeInfinity);
        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        Object.DestroyImmediate(data);
    }

    [Test]
    public void Snapshot_IsValueCopyAndDoesNotMutateSourceAsset()
    {
        OSPlayerBalanceData data = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        OSPlayerBalanceSnapshot snapshot = data.CreateSnapshot();

        SetSerializedFloat(data, "hp", 25f);
        SetSerializedFloat(data, "moveSpeed", 2f);
        OSPlayerBalanceSnapshot modifiedSnapshot = new OSPlayerBalanceSnapshot(
            999f,
            snapshot.MoveSpeed,
            snapshot.HeadDamage,
            snapshot.HeadShotsPerSecond,
            snapshot.HeadRange,
            snapshot.InvulnerabilityDuration);

        Assert.That(snapshot.Hp, Is.EqualTo(100f));
        Assert.That(snapshot.MoveSpeed, Is.EqualTo(5.5f));
        Assert.That(snapshot.HeadDamage, Is.EqualTo(10f));
        Assert.That(snapshot.HeadShotsPerSecond, Is.EqualTo(2f));
        Assert.That(snapshot.HeadAttackInterval, Is.EqualTo(0.5f));
        Assert.That(snapshot.HeadRange, Is.EqualTo(6f));
        Assert.That(snapshot.InvulnerabilityDuration, Is.EqualTo(0.6f));
        Assert.That(modifiedSnapshot.Hp, Is.EqualTo(999f));
        Assert.That(data.Hp, Is.EqualTo(25f));
        Assert.That(data.MoveSpeed, Is.EqualTo(2f));

        Object.DestroyImmediate(data);
    }

    private static void SetSerializedFloat(OSPlayerBalanceData data, string fieldName, float value)
    {
        SerializedObject serializedObject = new SerializedObject(data);
        SerializedProperty property = serializedObject.FindProperty(fieldName);
        Assert.That(property, Is.Not.Null, $"Missing serialized field: {fieldName}");
        property.floatValue = value;
        serializedObject.ApplyModifiedPropertiesWithoutUndo();
    }
}
#endif
