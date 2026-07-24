using System;
using UnityEngine;

[CreateAssetMenu(fileName = "EncounterBalance", menuName = "OUROBOROS/Data/Encounter Balance")]
public sealed class OSEncounterBalanceData : ScriptableObject
{
    [Header("Pool Limits")]
    [SerializeField] private int activeEnemyLimit = 180;
    [SerializeField] private int activeProjectileLimit = 120;

    [Header("Pool Keys")]
    [SerializeField] private string headProjectilePrefabKey = "projectile_head_basic";
    [SerializeField] private string bodyProjectilePrefabKey = "projectile_body_basic";
    [SerializeField] private string controlProjectilePrefabKey = "projectile_control";
    [SerializeField] private string experiencePickupPrefabKey = "pickup_experience";
    [SerializeField] private string bodyFragmentPickupPrefabKey = "pickup_body_fragment";
    [SerializeField] private string healPickupPrefabKey = "pickup_heal";

    [Header("Enemy Prototypes")]
    [SerializeField] private OSEnemyPrototype[] enemyPrototypes =
    {
        new OSEnemyPrototype("enemy_chaser", OSEnemyClass.Normal, "enemy_chaser", 24f, 2.2f, 8f, 1, 1, 0.05f),
        new OSEnemyPrototype("enemy_charger", OSEnemyClass.Normal, "enemy_charger", 36f, 2.8f, 12f, 1, 1, 0.04f),
        new OSEnemyPrototype("enemy_shooter", OSEnemyClass.Normal, "enemy_shooter", 28f, 1.6f, 8f, 2, 1, 0.03f),
        new OSEnemyPrototype("enemy_splitter", OSEnemyClass.Normal, "enemy_splitter", 32f, 1.9f, 8f, 1, 2, 0.03f),
        new OSEnemyPrototype("enemy_elite", OSEnemyClass.Elite, "enemy_elite", 320f, 1.7f, 16f, 12, 4, 0.12f),
        new OSEnemyPrototype("boss_swarm_core", OSEnemyClass.Boss, "boss_swarm_core", 6000f, 1.2f, 24f, 0, 0, 0f)
    };

    [Header("Wave Schedule")]
    [SerializeField] private OSEncounterWave[] waves =
    {
        new OSEncounterWave("wave_00_intro", OSEncounterWaveKind.SpawnGroup, 0f, "enemy_chaser", 18, 1.3f),
        new OSEncounterWave("wave_01_expand", OSEncounterWaveKind.SpawnGroup, 20f, "enemy_chaser", 22, 1.2f),
        new OSEncounterWave("wave_02_charge", OSEncounterWaveKind.SpawnGroup, 40f, "enemy_charger", 16, 1.4f),
        new OSEncounterWave("wave_03_sustain", OSEncounterWaveKind.SpawnGroup, 60f, "enemy_chaser", 20, 1.2f),
        new OSEncounterWave("wave_04_cross_pressure", OSEncounterWaveKind.SpawnGroup, 80f, "enemy_charger", 18, 1.25f),
        new OSEncounterWave("wave_05_first_shooter", OSEncounterWaveKind.SpawnGroup, 100f, "enemy_shooter", 14, 1.8f),
        new OSEncounterWave("wave_06_sustain_splitter", OSEncounterWaveKind.SpawnGroup, 120f, "enemy_splitter", 14, 1.7f),
        new OSEncounterWave("wave_07_pre_elite", OSEncounterWaveKind.SpawnGroup, 150f, "enemy_charger", 18, 1.2f),
        new OSEncounterWave("wave_08_elite", OSEncounterWaveKind.SpawnElite, 180f, "enemy_elite", 1, 0f),
        new OSEncounterWave("wave_09_shooter", OSEncounterWaveKind.SpawnGroup, 240f, "enemy_shooter", 16, 2.0f),
        new OSEncounterWave("wave_10_splitter", OSEncounterWaveKind.SpawnGroup, 300f, "enemy_splitter", 16, 2.0f),
        new OSEncounterWave("wave_11_elite", OSEncounterWaveKind.SpawnElite, 360f, "enemy_elite", 1, 0f),
        new OSEncounterWave("wave_12_mixed_pressure", OSEncounterWaveKind.SpawnGroup, 420f, "enemy_charger", 22, 1.4f),
        new OSEncounterWave("wave_13_high_density", OSEncounterWaveKind.SpawnGroup, 480f, "enemy_splitter", 24, 1.4f),
        new OSEncounterWave("wave_14_boss_warning", OSEncounterWaveKind.BossWarning, 540f, "boss_swarm_core", 0, 0f),
        new OSEncounterWave("wave_15_boss", OSEncounterWaveKind.SpawnBoss, 600f, "boss_swarm_core", 1, 0f)
    };

    public int ActiveEnemyLimit => activeEnemyLimit;
    public int ActiveProjectileLimit => activeProjectileLimit;
    public string HeadProjectilePrefabKey => headProjectilePrefabKey;
    public string BodyProjectilePrefabKey => bodyProjectilePrefabKey;
    public string ControlProjectilePrefabKey => controlProjectilePrefabKey;
    public string ExperiencePickupPrefabKey => experiencePickupPrefabKey;
    public string BodyFragmentPickupPrefabKey => bodyFragmentPickupPrefabKey;
    public string HealPickupPrefabKey => healPickupPrefabKey;
    public int EnemyPrototypeCount => enemyPrototypes?.Length ?? 0;
    public int WaveCount => waves?.Length ?? 0;

    public OSEnemyPrototype GetEnemyPrototypeAt(int index)
    {
        if (enemyPrototypes == null || index < 0 || index >= enemyPrototypes.Length)
        {
            return null;
        }

        return enemyPrototypes[index];
    }

    public OSEncounterWave GetWaveAt(int index)
    {
        if (waves == null || index < 0 || index >= waves.Length)
        {
            return null;
        }

        return waves[index];
    }

    public OSEnemyPrototype GetEnemyPrototype(string enemyId)
    {
        if (enemyPrototypes == null)
        {
            return null;
        }

        for (int i = 0; i < enemyPrototypes.Length; i++)
        {
            OSEnemyPrototype prototype = enemyPrototypes[i];
            if (prototype != null && prototype.Id == enemyId)
            {
                return prototype;
            }
        }

        return null;
    }

    public bool HasPoolCapacity(OSPoolCategory category, int activeCount)
    {
        return category switch
        {
            OSPoolCategory.Enemy => activeCount < activeEnemyLimit,
            OSPoolCategory.Projectile => activeCount < activeProjectileLimit,
            OSPoolCategory.Pickup => activeCount < activeProjectileLimit,
            _ => false
        };
    }

    public OSConfigurationValidationResult ValidateConfiguration()
    {
        if (!IsPositive(activeEnemyLimit) ||
            !IsPositive(activeProjectileLimit) ||
            IsBlank(headProjectilePrefabKey) ||
            IsBlank(bodyProjectilePrefabKey) ||
            IsBlank(controlProjectilePrefabKey) ||
            IsBlank(experiencePickupPrefabKey) ||
            IsBlank(bodyFragmentPickupPrefabKey) ||
            IsBlank(healPickupPrefabKey) ||
            !ValidateEnemyPrototypes() ||
            !ValidateWaves())
        {
            return OSConfigurationValidationResult.ConfigurationError;
        }

        return OSConfigurationValidationResult.Accepted;
    }

    public OSEncounterBalanceSnapshot CreateSnapshot()
    {
        return new OSEncounterBalanceSnapshot(
            activeEnemyLimit,
            activeProjectileLimit,
            headProjectilePrefabKey,
            bodyProjectilePrefabKey,
            controlProjectilePrefabKey,
            experiencePickupPrefabKey,
            bodyFragmentPickupPrefabKey,
            healPickupPrefabKey,
            CopyEnemySnapshots(),
            CopyWaveSnapshots());
    }

    private void OnValidate()
    {
        ValidateConfiguration();
    }

    private bool ValidateEnemyPrototypes()
    {
        if (enemyPrototypes == null || enemyPrototypes.Length == 0)
        {
            return false;
        }

        int normalCount = 0;
        int eliteCount = 0;
        int bossCount = 0;

        for (int i = 0; i < enemyPrototypes.Length; i++)
        {
            OSEnemyPrototype prototype = enemyPrototypes[i];
            if (prototype == null || !prototype.IsValid())
            {
                return false;
            }

            if (ContainsDuplicateEnemyId(prototype.Id, i))
            {
                return false;
            }

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
            }
        }

        return normalCount >= 4 && eliteCount >= 1 && bossCount >= 1;
    }

    private bool ValidateWaves()
    {
        if (waves == null || waves.Length == 0)
        {
            return false;
        }

        bool hasThreeMinuteElite = false;
        bool hasSixMinuteElite = false;
        bool hasNineMinuteBossWarning = false;
        bool hasTenMinuteBoss = false;

        for (int i = 0; i < waves.Length; i++)
        {
            OSEncounterWave wave = waves[i];
            if (wave == null || !wave.IsValid())
            {
                return false;
            }

            if (ContainsDuplicateWaveId(wave.Id, i) || GetEnemyPrototype(wave.EnemyId) == null)
            {
                return false;
            }

            OSEnemyPrototype prototype = GetEnemyPrototype(wave.EnemyId);
            if (wave.Kind == OSEncounterWaveKind.SpawnElite && prototype.Class != OSEnemyClass.Elite)
            {
                return false;
            }

            if ((wave.Kind == OSEncounterWaveKind.BossWarning || wave.Kind == OSEncounterWaveKind.SpawnBoss) &&
                prototype.Class != OSEnemyClass.Boss)
            {
                return false;
            }

            hasThreeMinuteElite |= wave.Kind == OSEncounterWaveKind.SpawnElite && Mathf.Approximately(wave.StartTimeSeconds, 180f);
            hasSixMinuteElite |= wave.Kind == OSEncounterWaveKind.SpawnElite && Mathf.Approximately(wave.StartTimeSeconds, 360f);
            hasNineMinuteBossWarning |= wave.Kind == OSEncounterWaveKind.BossWarning && Mathf.Approximately(wave.StartTimeSeconds, 540f);
            hasTenMinuteBoss |= wave.Kind == OSEncounterWaveKind.SpawnBoss && Mathf.Approximately(wave.StartTimeSeconds, 600f);
        }

        return hasThreeMinuteElite && hasSixMinuteElite && hasNineMinuteBossWarning && hasTenMinuteBoss;
    }

    private bool ContainsDuplicateEnemyId(string id, int currentIndex)
    {
        for (int i = 0; i < currentIndex; i++)
        {
            if (enemyPrototypes[i] != null && enemyPrototypes[i].Id == id)
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsDuplicateWaveId(string id, int currentIndex)
    {
        for (int i = 0; i < currentIndex; i++)
        {
            if (waves[i] != null && waves[i].Id == id)
            {
                return true;
            }
        }

        return false;
    }

    private OSEnemyPrototypeSnapshot[] CopyEnemySnapshots()
    {
        if (enemyPrototypes == null)
        {
            return Array.Empty<OSEnemyPrototypeSnapshot>();
        }

        OSEnemyPrototypeSnapshot[] result = new OSEnemyPrototypeSnapshot[enemyPrototypes.Length];
        for (int i = 0; i < enemyPrototypes.Length; i++)
        {
            result[i] = enemyPrototypes[i].CreateSnapshot();
        }

        return result;
    }

    private OSEncounterWaveSnapshot[] CopyWaveSnapshots()
    {
        if (waves == null)
        {
            return Array.Empty<OSEncounterWaveSnapshot>();
        }

        OSEncounterWaveSnapshot[] result = new OSEncounterWaveSnapshot[waves.Length];
        for (int i = 0; i < waves.Length; i++)
        {
            result[i] = waves[i].CreateSnapshot();
        }

        return result;
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

    internal static bool IsBlank(string value)
    {
        return string.IsNullOrWhiteSpace(value);
    }
}

public enum OSEnemyClass
{
    Normal,
    Elite,
    Boss
}

public enum OSEncounterWaveKind
{
    SpawnGroup,
    SpawnElite,
    BossWarning,
    SpawnBoss
}

public enum OSPoolCategory
{
    Enemy,
    Projectile,
    Pickup,
    Effect
}

[Serializable]
public sealed class OSEnemyPrototype
{
    [SerializeField] private string id;
    [SerializeField] private OSEnemyClass enemyClass;
    [SerializeField] private string prefabKey;
    [SerializeField] private float maxHp;
    [SerializeField] private float moveSpeed;
    [SerializeField] private float contactDamage;
    [SerializeField] private int experienceDrop;
    [SerializeField] private int bodyFragmentDrop;
    [SerializeField] private float healDropChance;

    public OSEnemyPrototype(
        string id,
        OSEnemyClass enemyClass,
        string prefabKey,
        float maxHp,
        float moveSpeed,
        float contactDamage,
        int experienceDrop,
        int bodyFragmentDrop,
        float healDropChance)
    {
        this.id = id;
        this.enemyClass = enemyClass;
        this.prefabKey = prefabKey;
        this.maxHp = maxHp;
        this.moveSpeed = moveSpeed;
        this.contactDamage = contactDamage;
        this.experienceDrop = experienceDrop;
        this.bodyFragmentDrop = bodyFragmentDrop;
        this.healDropChance = healDropChance;
    }

    public string Id => id;
    public OSEnemyClass Class => enemyClass;
    public string PrefabKey => prefabKey;
    public float MaxHp => maxHp;
    public float MoveSpeed => moveSpeed;
    public float ContactDamage => contactDamage;
    public int ExperienceDrop => experienceDrop;
    public int BodyFragmentDrop => bodyFragmentDrop;
    public float HealDropChance => healDropChance;

    public bool IsValid()
    {
        return !OSEncounterBalanceData.IsBlank(id) &&
            !OSEncounterBalanceData.IsBlank(prefabKey) &&
            OSEncounterBalanceData.IsPositiveFinite(maxHp) &&
            OSEncounterBalanceData.IsPositiveFinite(moveSpeed) &&
            OSEncounterBalanceData.IsPositiveFinite(contactDamage) &&
            experienceDrop >= 0 &&
            bodyFragmentDrop >= 0 &&
            healDropChance >= 0f &&
            healDropChance <= 1f &&
            !float.IsNaN(healDropChance) &&
            !float.IsInfinity(healDropChance);
    }

    public OSEnemyPrototypeSnapshot CreateSnapshot()
    {
        return new OSEnemyPrototypeSnapshot(
            id,
            enemyClass,
            prefabKey,
            maxHp,
            moveSpeed,
            contactDamage,
            experienceDrop,
            bodyFragmentDrop,
            healDropChance);
    }
}

[Serializable]
public sealed class OSEncounterWave
{
    [SerializeField] private string id;
    [SerializeField] private OSEncounterWaveKind kind;
    [SerializeField] private float startTimeSeconds;
    [SerializeField] private string enemyId;
    [SerializeField] private int spawnCount;
    [SerializeField] private float spawnIntervalSeconds;

    public OSEncounterWave(
        string id,
        OSEncounterWaveKind kind,
        float startTimeSeconds,
        string enemyId,
        int spawnCount,
        float spawnIntervalSeconds)
    {
        this.id = id;
        this.kind = kind;
        this.startTimeSeconds = startTimeSeconds;
        this.enemyId = enemyId;
        this.spawnCount = spawnCount;
        this.spawnIntervalSeconds = spawnIntervalSeconds;
    }

    public string Id => id;
    public OSEncounterWaveKind Kind => kind;
    public float StartTimeSeconds => startTimeSeconds;
    public string EnemyId => enemyId;
    public int SpawnCount => spawnCount;
    public float SpawnIntervalSeconds => spawnIntervalSeconds;

    public bool IsValid()
    {
        if (OSEncounterBalanceData.IsBlank(id) ||
            OSEncounterBalanceData.IsBlank(enemyId) ||
            !OSEncounterBalanceData.IsNonNegativeFinite(startTimeSeconds))
        {
            return false;
        }

        if (kind == OSEncounterWaveKind.BossWarning)
        {
            return spawnCount == 0 && OSEncounterBalanceData.IsNonNegativeFinite(spawnIntervalSeconds);
        }

        return spawnCount > 0 && OSEncounterBalanceData.IsNonNegativeFinite(spawnIntervalSeconds);
    }

    public OSEncounterWaveSnapshot CreateSnapshot()
    {
        return new OSEncounterWaveSnapshot(id, kind, startTimeSeconds, enemyId, spawnCount, spawnIntervalSeconds);
    }
}

[Serializable]
public readonly struct OSEncounterBalanceSnapshot
{
    public OSEncounterBalanceSnapshot(
        int activeEnemyLimit,
        int activeProjectileLimit,
        string headProjectilePrefabKey,
        string bodyProjectilePrefabKey,
        string controlProjectilePrefabKey,
        string experiencePickupPrefabKey,
        string bodyFragmentPickupPrefabKey,
        string healPickupPrefabKey,
        OSEnemyPrototypeSnapshot[] enemyPrototypes,
        OSEncounterWaveSnapshot[] waves)
    {
        ActiveEnemyLimit = activeEnemyLimit;
        ActiveProjectileLimit = activeProjectileLimit;
        HeadProjectilePrefabKey = headProjectilePrefabKey;
        BodyProjectilePrefabKey = bodyProjectilePrefabKey;
        ControlProjectilePrefabKey = controlProjectilePrefabKey;
        ExperiencePickupPrefabKey = experiencePickupPrefabKey;
        BodyFragmentPickupPrefabKey = bodyFragmentPickupPrefabKey;
        HealPickupPrefabKey = healPickupPrefabKey;
        EnemyPrototypes = enemyPrototypes;
        Waves = waves;
    }

    public int ActiveEnemyLimit { get; }
    public int ActiveProjectileLimit { get; }
    public string HeadProjectilePrefabKey { get; }
    public string BodyProjectilePrefabKey { get; }
    public string ControlProjectilePrefabKey { get; }
    public string ExperiencePickupPrefabKey { get; }
    public string BodyFragmentPickupPrefabKey { get; }
    public string HealPickupPrefabKey { get; }
    public OSEnemyPrototypeSnapshot[] EnemyPrototypes { get; }
    public OSEncounterWaveSnapshot[] Waves { get; }
}

[Serializable]
public readonly struct OSEnemyPrototypeSnapshot
{
    public OSEnemyPrototypeSnapshot(
        string id,
        OSEnemyClass enemyClass,
        string prefabKey,
        float maxHp,
        float moveSpeed,
        float contactDamage,
        int experienceDrop,
        int bodyFragmentDrop,
        float healDropChance)
    {
        Id = id;
        Class = enemyClass;
        PrefabKey = prefabKey;
        MaxHp = maxHp;
        MoveSpeed = moveSpeed;
        ContactDamage = contactDamage;
        ExperienceDrop = experienceDrop;
        BodyFragmentDrop = bodyFragmentDrop;
        HealDropChance = healDropChance;
    }

    public string Id { get; }
    public OSEnemyClass Class { get; }
    public string PrefabKey { get; }
    public float MaxHp { get; }
    public float MoveSpeed { get; }
    public float ContactDamage { get; }
    public int ExperienceDrop { get; }
    public int BodyFragmentDrop { get; }
    public float HealDropChance { get; }
}

[Serializable]
public readonly struct OSEncounterWaveSnapshot
{
    public OSEncounterWaveSnapshot(
        string id,
        OSEncounterWaveKind kind,
        float startTimeSeconds,
        string enemyId,
        int spawnCount,
        float spawnIntervalSeconds)
    {
        Id = id;
        Kind = kind;
        StartTimeSeconds = startTimeSeconds;
        EnemyId = enemyId;
        SpawnCount = spawnCount;
        SpawnIntervalSeconds = spawnIntervalSeconds;
    }

    public string Id { get; }
    public OSEncounterWaveKind Kind { get; }
    public float StartTimeSeconds { get; }
    public string EnemyId { get; }
    public int SpawnCount { get; }
    public float SpawnIntervalSeconds { get; }
}
