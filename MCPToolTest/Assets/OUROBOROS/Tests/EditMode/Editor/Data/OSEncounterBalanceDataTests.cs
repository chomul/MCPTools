#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class OSEncounterBalanceDataTests
{
    [Test]
    public void AssetMenu_UsesExpectedEncounterBalancePath()
    {
        CreateAssetMenuAttribute attribute = (CreateAssetMenuAttribute)Attribute.GetCustomAttribute(
            typeof(OSEncounterBalanceData),
            typeof(CreateAssetMenuAttribute));

        Assert.That(attribute, Is.Not.Null);
        Assert.That(attribute.fileName, Is.EqualTo("EncounterBalance"));
        Assert.That(attribute.menuName, Is.EqualTo("OUROBOROS/Data/Encounter Balance"));
    }

    [Test]
    public void DefaultConfiguration_IsAcceptedAndExposesExpectedPoolLimitsAndKeys()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.Accepted));
        Assert.That(data.ActiveEnemyLimit, Is.EqualTo(180));
        Assert.That(data.ActiveProjectileLimit, Is.EqualTo(120));
        Assert.That(data.HeadProjectilePrefabKey, Is.EqualTo("projectile_head_basic"));
        Assert.That(data.BodyProjectilePrefabKey, Is.EqualTo("projectile_body_basic"));
        Assert.That(data.ControlProjectilePrefabKey, Is.EqualTo("projectile_control"));
        Assert.That(data.ExperiencePickupPrefabKey, Is.EqualTo("pickup_experience"));
        Assert.That(data.BodyFragmentPickupPrefabKey, Is.EqualTo("pickup_body_fragment"));
        Assert.That(data.HealPickupPrefabKey, Is.EqualTo("pickup_heal"));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void DefaultEnemyPrototypes_ContainFourNormalsEliteAndBoss()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();

        int normalCount = 0;
        int eliteCount = 0;
        int bossCount = 0;

        Assert.That(data.EnemyPrototypeCount, Is.EqualTo(6));
        for (int i = 0; i < data.EnemyPrototypeCount; i++)
        {
            OSEnemyPrototype prototype = data.GetEnemyPrototypeAt(i);
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype.Id, Is.Not.Empty);
            Assert.That(prototype.PrefabKey, Is.Not.Empty);
            Assert.That(prototype.MaxHp, Is.GreaterThan(0f));

            if (prototype.Class == OSEnemyClass.Normal)
            {
                normalCount++;
            }
            else if (prototype.Class == OSEnemyClass.Elite)
            {
                eliteCount++;
            }
            else if (prototype.Class == OSEnemyClass.Boss)
            {
                bossCount++;
                Assert.That(prototype.Id, Is.EqualTo("boss_swarm_core"));
                Assert.That(prototype.MaxHp, Is.EqualTo(6000f));
            }
        }

        Assert.That(normalCount, Is.EqualTo(4));
        Assert.That(eliteCount, Is.EqualTo(1));
        Assert.That(bossCount, Is.EqualTo(1));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void DefaultWaves_ContainEarlyPressureAndRequiredMilestones()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();

        Assert.That(data.WaveCount, Is.EqualTo(16));
        AssertWave(data, 0f, OSEncounterWaveKind.SpawnGroup, "enemy_chaser");
        AssertWave(data, 20f, OSEncounterWaveKind.SpawnGroup, "enemy_chaser");
        AssertWave(data, 40f, OSEncounterWaveKind.SpawnGroup, "enemy_charger");
        AssertWave(data, 60f, OSEncounterWaveKind.SpawnGroup, "enemy_chaser");
        AssertWave(data, 80f, OSEncounterWaveKind.SpawnGroup, "enemy_charger");
        AssertWave(data, 100f, OSEncounterWaveKind.SpawnGroup, "enemy_shooter");
        AssertWave(data, 120f, OSEncounterWaveKind.SpawnGroup, "enemy_splitter");
        AssertWave(data, 150f, OSEncounterWaveKind.SpawnGroup, "enemy_charger");
        AssertWave(data, 180f, OSEncounterWaveKind.SpawnElite, "enemy_elite");
        AssertWave(data, 240f, OSEncounterWaveKind.SpawnGroup, "enemy_shooter");
        AssertWave(data, 300f, OSEncounterWaveKind.SpawnGroup, "enemy_splitter");
        AssertWave(data, 360f, OSEncounterWaveKind.SpawnElite, "enemy_elite");
        AssertWave(data, 420f, OSEncounterWaveKind.SpawnGroup, "enemy_charger");
        AssertWave(data, 480f, OSEncounterWaveKind.SpawnGroup, "enemy_splitter");
        AssertWave(data, 540f, OSEncounterWaveKind.BossWarning, "boss_swarm_core");
        AssertWave(data, 600f, OSEncounterWaveKind.SpawnBoss, "boss_swarm_core");

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void DefaultWaves_DoNotLeaveFirstThreeMinutesWithoutPressure()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        float previousStartTime = 0f;

        for (int i = 0; i < data.WaveCount; i++)
        {
            OSEncounterWave wave = data.GetWaveAt(i);
            if (wave.StartTimeSeconds > 180f)
            {
                break;
            }

            Assert.That(wave.StartTimeSeconds - previousStartTime, Is.LessThanOrEqualTo(40f));
            previousStartTime = wave.StartTimeSeconds;
        }

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void PoolCapacity_UsesEnemyAndProjectileLimits()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();

        Assert.That(data.HasPoolCapacity(OSPoolCategory.Enemy, 179), Is.True);
        Assert.That(data.HasPoolCapacity(OSPoolCategory.Enemy, 180), Is.False);
        Assert.That(data.HasPoolCapacity(OSPoolCategory.Projectile, 119), Is.True);
        Assert.That(data.HasPoolCapacity(OSPoolCategory.Projectile, 120), Is.False);

        UnityEngine.Object.DestroyImmediate(data);
    }

    [TestCase("activeEnemyLimit", 0)]
    [TestCase("activeProjectileLimit", 0)]
    public void InvalidPoolLimits_AreRejected(string fieldName, int value)
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        SetPrivateField(data, fieldName, value);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [TestCase("headProjectilePrefabKey")]
    [TestCase("bodyProjectilePrefabKey")]
    [TestCase("controlProjectilePrefabKey")]
    [TestCase("experiencePickupPrefabKey")]
    [TestCase("bodyFragmentPickupPrefabKey")]
    [TestCase("healPickupPrefabKey")]
    public void MissingGlobalPrefabKeys_AreRejected(string fieldName)
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        SetPrivateField(data, fieldName, string.Empty);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void DuplicateEnemyIds_AreRejected()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        OSEnemyPrototype[] enemies = GetEnemyArray(data);
        enemies[1] = new OSEnemyPrototype(
            enemies[0].Id,
            OSEnemyClass.Normal,
            "enemy_duplicate",
            10f,
            1f,
            1f,
            1,
            1,
            0f);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void MissingEnemyPrefabKeys_AreRejected()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        OSEnemyPrototype[] enemies = GetEnemyArray(data);
        enemies[0] = new OSEnemyPrototype(
            "enemy_chaser",
            OSEnemyClass.Normal,
            string.Empty,
            24f,
            2.2f,
            8f,
            1,
            1,
            0.05f);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void InvalidEnemyNumbers_AreRejected()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        OSEnemyPrototype[] enemies = GetEnemyArray(data);
        enemies[0] = new OSEnemyPrototype(
            "enemy_chaser",
            OSEnemyClass.Normal,
            "enemy_chaser",
            -1f,
            2.2f,
            8f,
            1,
            1,
            0.05f);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void MissingWaveEnemyReferences_AreRejected()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        OSEncounterWave[] waves = GetWaveArray(data);
        waves[0] = new OSEncounterWave(
            "wave_missing",
            OSEncounterWaveKind.SpawnGroup,
            0f,
            "enemy_missing",
            1,
            1f);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void DuplicateWaveIds_AreRejected()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        OSEncounterWave[] waves = GetWaveArray(data);
        waves[1] = new OSEncounterWave(
            waves[0].Id,
            OSEncounterWaveKind.SpawnGroup,
            60f,
            "enemy_chaser",
            1,
            1f);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [TestCase(-1f, OSEncounterWaveKind.SpawnGroup, 1, 1f)]
    [TestCase(0f, OSEncounterWaveKind.SpawnGroup, 0, 1f)]
    [TestCase(0f, OSEncounterWaveKind.SpawnGroup, 1, float.NaN)]
    [TestCase(540f, OSEncounterWaveKind.BossWarning, 1, 0f)]
    public void InvalidWaveNumbers_AreRejected(
        float startTime,
        OSEncounterWaveKind kind,
        int spawnCount,
        float spawnInterval)
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        OSEncounterWave[] waves = GetWaveArray(data);
        waves[0] = new OSEncounterWave(
            "wave_invalid",
            kind,
            startTime,
            kind == OSEncounterWaveKind.BossWarning ? "boss_swarm_core" : "enemy_chaser",
            spawnCount,
            spawnInterval);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void MissingRequiredMilestoneWaves_AreRejected()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        OSEncounterWave[] waves = GetWaveArray(data);
        waves[8] = new OSEncounterWave(
            "wave_08_not_elite",
            OSEncounterWaveKind.SpawnGroup,
            180f,
            "enemy_chaser",
            1,
            1f);

        Assert.That(data.ValidateConfiguration(), Is.EqualTo(OSConfigurationValidationResult.ConfigurationError));

        UnityEngine.Object.DestroyImmediate(data);
    }

    [Test]
    public void Snapshot_IsValueCopyAndDoesNotMutateSourceAsset()
    {
        OSEncounterBalanceData data = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        OSEncounterBalanceSnapshot snapshot = data.CreateSnapshot();

        SetPrivateField(data, "activeEnemyLimit", 99);
        OSEnemyPrototype[] enemies = GetEnemyArray(data);
        enemies[0] = new OSEnemyPrototype("enemy_changed", OSEnemyClass.Normal, "enemy_changed", 1f, 1f, 1f, 0, 0, 0f);

        Assert.That(snapshot.ActiveEnemyLimit, Is.EqualTo(180));
        Assert.That(snapshot.EnemyPrototypes.Length, Is.EqualTo(6));
        Assert.That(snapshot.EnemyPrototypes[0].Id, Is.EqualTo("enemy_chaser"));
        Assert.That(data.ActiveEnemyLimit, Is.EqualTo(99));
        Assert.That(data.GetEnemyPrototypeAt(0).Id, Is.EqualTo("enemy_changed"));

        UnityEngine.Object.DestroyImmediate(data);
    }

    private static void AssertWave(
        OSEncounterBalanceData data,
        float startTime,
        OSEncounterWaveKind kind,
        string enemyId)
    {
        for (int i = 0; i < data.WaveCount; i++)
        {
            OSEncounterWave wave = data.GetWaveAt(i);
            if (Mathf.Approximately(wave.StartTimeSeconds, startTime) &&
                wave.Kind == kind &&
                wave.EnemyId == enemyId)
            {
                return;
            }
        }

        Assert.Fail($"Missing wave: {startTime}, {kind}, {enemyId}");
    }

    private static OSEnemyPrototype[] GetEnemyArray(OSEncounterBalanceData data)
    {
        return (OSEnemyPrototype[])GetPrivateField(data, "enemyPrototypes");
    }

    private static OSEncounterWave[] GetWaveArray(OSEncounterBalanceData data)
    {
        return (OSEncounterWave[])GetPrivateField(data, "waves");
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field: {fieldName}");
        return field.GetValue(target);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field: {fieldName}");
        field.SetValue(target, value);
    }
}
#endif
