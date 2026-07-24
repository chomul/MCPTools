using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSWaveDirector : MonoBehaviour
{
    private const string FallbackEnemyPoolKey = "enemy_chaser";
    private const float DefaultEliteHpMultiplier = 12f;
    private const float DefaultEliteDamageMultiplier = 2f;
    private const float DefaultBossHpMultiplier = 250f;
    private const float DefaultBossDamageMultiplier = 3f;

    [Header("References")]
    [SerializeField] private OSEncounterBalanceData encounterBalance;
    [SerializeField] private OSPoolRegistry poolRegistry;
    [SerializeField] private OSGameSessionController sessionController;
    [SerializeField] private Transform playerTarget;

    [Header("Spawn Area")]
    [SerializeField] private Transform spawnAreaCenter;
    [SerializeField] private Vector2 spawnAreaHalfExtents = new Vector2(9f, 5f);
    [SerializeField] private float minimumPlayerDistance = 5f;

    [Header("Runtime")]
    [SerializeField] private bool beginOnSessionStart = true;
    [SerializeField] private bool beginOnAwake;

    [Header("Endless")]
    [SerializeField] private bool enableEndlessMode = true;
    [SerializeField] private float endlessWaveIntervalSeconds = 20f;
    [SerializeField] private float endlessIntensityPerMinute = 0.18f;
    [SerializeField] private int endlessMaxSpawnCount = 48;
    [SerializeField] private float endlessMinimumSpawnInterval = 0.65f;
    [SerializeField] private int endlessEliteEveryWaves = 9;
    [SerializeField] private int endlessBossWarningEveryWaves = 14;
    [SerializeField] private int endlessBossEveryWaves = 15;

    private readonly List<OSWaveSpawnJob> spawnJobs = new List<OSWaveSpawnJob>(12);
    private readonly Dictionary<OSEnemyController, Action<OSEnemyDropResult>> dropSubscriptions =
        new Dictionary<OSEnemyController, Action<OSEnemyDropResult>>();

    private float elapsedCombatSeconds;
    private int nextWaveIndex;
    private int spawnedEnemyCount;
    private int spawnedPickupCount;
    private int rejectedCapacityCount;
    private int bossWarningCount;
    private int endlessWaveIndex;
    private float nextEndlessWaveTime;
    private bool isRunning;
    private bool bossSpawned;

    public event Action<OSWaveEvent> WaveEventRaised;
    public event Action<OSEnemyController> EnemySpawned;
    public event Action<OSPickup> PickupSpawned;

    public bool IsRunning => isRunning;
    public float ElapsedCombatSeconds => elapsedCombatSeconds;
    public int NextWaveIndex => nextWaveIndex;
    public int ActiveSpawnJobCount => spawnJobs.Count;
    public int SpawnedEnemyCount => spawnedEnemyCount;
    public int SpawnedPickupCount => spawnedPickupCount;
    public int RejectedCapacityCount => rejectedCapacityCount;
    public int BossWarningCount => bossWarningCount;
    public int EndlessWaveIndex => endlessWaveIndex;
    public bool BossSpawned => bossSpawned;

    public void ConfigureForTests(
        OSEncounterBalanceData encounter,
        OSPoolRegistry pool,
        OSGameSessionController session = null,
        Transform target = null,
        Transform areaCenter = null,
        Vector2? halfExtents = null)
    {
        encounterBalance = encounter;
        poolRegistry = pool;
        sessionController = session;
        playerTarget = target;
        spawnAreaCenter = areaCenter;
        if (halfExtents.HasValue)
        {
            spawnAreaHalfExtents = halfExtents.Value;
        }
    }

    public OSRuleResult<OSWaveSnapshot> BeginWaves()
    {
        OSRuleResult<int> validation = ValidateConfiguration();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSWaveSnapshot>.Rejected(validation.Code, validation.ReasonKey);
        }

        elapsedCombatSeconds = 0f;
        nextWaveIndex = 0;
        spawnedEnemyCount = 0;
        spawnedPickupCount = 0;
        rejectedCapacityCount = 0;
        bossWarningCount = 0;
        endlessWaveIndex = 0;
        nextEndlessWaveTime = CalculateInitialEndlessWaveTime();
        bossSpawned = false;
        spawnJobs.Clear();
        UnsubscribeAllDrops();
        isRunning = true;
        return OSRuleResult<OSWaveSnapshot>.Accept(CreateSnapshot());
    }

    public OSRuleResult<OSWaveSnapshot> StopWaves()
    {
        isRunning = false;
        spawnJobs.Clear();
        UnsubscribeAllDrops();
        return OSRuleResult<OSWaveSnapshot>.Accept(CreateSnapshot());
    }

    public OSRuleResult<OSWaveSnapshot> Tick(float deltaTime)
    {
        if (!isRunning)
        {
            return OSRuleResult<OSWaveSnapshot>.Rejected(OSResultCode.RejectedState, "wave_director_not_running");
        }

        OSRuleResult<int> validation = ValidateConfiguration();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSWaveSnapshot>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (!IsPositiveFinite(deltaTime))
        {
            return OSRuleResult<OSWaveSnapshot>.Rejected(OSResultCode.ConfigurationError, "wave_delta_time_invalid");
        }

        if (!CanAdvanceCombatTime())
        {
            return OSRuleResult<OSWaveSnapshot>.Rejected(OSResultCode.RejectedState, "wave_director_not_in_combat");
        }

        elapsedCombatSeconds += deltaTime;
        ActivateDueWaves();
        ProcessSpawnJobs(deltaTime);
        return OSRuleResult<OSWaveSnapshot>.Accept(CreateSnapshot());
    }

    public OSRuleResult<OSEnemyController> SpawnEnemyForTests(string enemyId)
    {
        return TrySpawnEnemy(enemyId);
    }

    private void OnEnable()
    {
        if (sessionController != null)
        {
            sessionController.SessionStarted += OnSessionStarted;
            sessionController.SessionEnded += OnSessionEnded;
        }
    }

    private void OnDisable()
    {
        if (sessionController != null)
        {
            sessionController.SessionStarted -= OnSessionStarted;
            sessionController.SessionEnded -= OnSessionEnded;
        }

        UnsubscribeAllDrops();
    }

    private void Awake()
    {
        if (beginOnAwake)
        {
            BeginWaves();
        }
    }

    private void Update()
    {
        if (isRunning)
        {
            Tick(Time.deltaTime);
        }
    }

    private void OnSessionStarted(OSSessionRuntimeState state)
    {
        if (beginOnSessionStart)
        {
            BeginWaves();
        }
    }

    private void OnSessionEnded(OSSessionSummary summary)
    {
        StopWaves();
    }

    private void ActivateDueWaves()
    {
        while (nextWaveIndex < encounterBalance.WaveCount)
        {
            OSEncounterWave wave = encounterBalance.GetWaveAt(nextWaveIndex);
            if (wave == null || wave.StartTimeSeconds > elapsedCombatSeconds)
            {
                break;
            }

            if (wave.Kind == OSEncounterWaveKind.BossWarning)
            {
                bossWarningCount++;
                RaiseWaveEvent(wave, OSWaveEventType.BossWarning);
            }
            else
            {
                spawnJobs.Add(new OSWaveSpawnJob(wave));
                RaiseWaveEvent(wave, OSWaveEventType.WaveStarted);
            }

            nextWaveIndex++;
        }

        ActivateEndlessWaves();
    }

    private void ActivateEndlessWaves()
    {
        if (!enableEndlessMode || nextWaveIndex < encounterBalance.WaveCount)
        {
            return;
        }

        if (nextEndlessWaveTime <= 0f)
        {
            nextEndlessWaveTime = CalculateInitialEndlessWaveTime();
        }

        while (elapsedCombatSeconds >= nextEndlessWaveTime)
        {
            OSEncounterWave wave = CreateEndlessWave();
            if (wave.Kind == OSEncounterWaveKind.BossWarning)
            {
                bossWarningCount++;
                RaiseWaveEvent(wave, OSWaveEventType.BossWarning);
            }
            else
            {
                spawnJobs.Add(new OSWaveSpawnJob(wave));
                RaiseWaveEvent(wave, OSWaveEventType.WaveStarted);
            }

            nextEndlessWaveTime += Mathf.Max(1f, endlessWaveIntervalSeconds);
        }
    }

    private void ProcessSpawnJobs(float deltaTime)
    {
        for (int i = spawnJobs.Count - 1; i >= 0; i--)
        {
            OSWaveSpawnJob job = spawnJobs[i];
            job.ElapsedSinceLastSpawn += deltaTime;

            while (job.RemainingCount > 0 && job.ElapsedSinceLastSpawn >= job.SpawnIntervalSeconds)
            {
                OSRuleResult<OSEnemyController> spawnResult = TrySpawnEnemy(job.EnemyId);
                if (!spawnResult.IsAccepted)
                {
                    if (spawnResult.Code == OSResultCode.RejectedCapacity)
                    {
                        rejectedCapacityCount++;
                        break;
                    }

                    job.RemainingCount = 0;
                    break;
                }

                job.RemainingCount--;
                job.ElapsedSinceLastSpawn -= job.SpawnIntervalSeconds;
            }

            if (job.RemainingCount <= 0)
            {
                spawnJobs.RemoveAt(i);
            }
        }
    }

    private OSRuleResult<OSEnemyController> TrySpawnEnemy(string enemyId)
    {
        if (poolRegistry.ActiveEnemyCount >= encounterBalance.ActiveEnemyLimit)
        {
            return OSRuleResult<OSEnemyController>.Rejected(OSResultCode.RejectedCapacity, "wave_enemy_limit_reached");
        }

        OSEnemyPrototype prototype = encounterBalance.GetEnemyPrototype(enemyId);
        if (prototype == null)
        {
            return OSRuleResult<OSEnemyController>.Rejected(OSResultCode.ConfigurationError, "wave_enemy_unknown");
        }

        string poolKey = SelectPoolKey(prototype);
        OSRuleResult<GameObject> rentResult = poolRegistry.Rent(poolKey);
        if (!rentResult.IsAccepted)
        {
            return OSRuleResult<OSEnemyController>.Rejected(rentResult.Code, rentResult.ReasonKey);
        }

        OSEnemyController enemy = rentResult.Payload.GetComponent<OSEnemyController>();
        if (enemy == null)
        {
            poolRegistry.Return(rentResult.Payload);
            return OSRuleResult<OSEnemyController>.Rejected(OSResultCode.ConfigurationError, "wave_enemy_component_missing");
        }

        rentResult.Payload.transform.position = CalculateSpawnPosition(spawnedEnemyCount);
        OSEnemyPrototypeSnapshot snapshot = CreateSpawnSnapshot(prototype, poolKey);
        OSRuleResult<OSEnemySnapshot> initializeResult = enemy.Initialize(
            $"enemy_{spawnedEnemyCount + 1:0000}",
            snapshot,
            playerTarget,
            poolRegistry);

        if (!initializeResult.IsAccepted)
        {
            poolRegistry.Return(rentResult.Payload);
            return OSRuleResult<OSEnemyController>.Rejected(initializeResult.Code, initializeResult.ReasonKey);
        }

        SubscribeDrop(enemy);
        spawnedEnemyCount++;
        if (snapshot.Class == OSEnemyClass.Boss)
        {
            bossSpawned = true;
            RaiseWaveEvent(null, OSWaveEventType.BossSpawned);
        }

        EnemySpawned?.Invoke(enemy);
        return OSRuleResult<OSEnemyController>.Accept(enemy);
    }

    private string SelectPoolKey(OSEnemyPrototype prototype)
    {
        if (prototype != null && poolRegistry.GetUsage(prototype.PrefabKey).IsAccepted)
        {
            return prototype.PrefabKey;
        }

        return FallbackEnemyPoolKey;
    }

    private OSEnemyPrototypeSnapshot CreateSpawnSnapshot(OSEnemyPrototype prototype, string poolKey)
    {
        if (poolKey == prototype.PrefabKey)
        {
            return prototype.CreateSnapshot();
        }

        OSEnemyPrototype fallback = encounterBalance.GetEnemyPrototype(FallbackEnemyPoolKey);
        OSEnemyPrototype source = fallback ?? prototype;
        float hp = source.MaxHp;
        float damage = source.ContactDamage;

        if (prototype.Class == OSEnemyClass.Elite)
        {
            hp *= DefaultEliteHpMultiplier;
            damage *= DefaultEliteDamageMultiplier;
        }
        else if (prototype.Class == OSEnemyClass.Boss)
        {
            hp *= DefaultBossHpMultiplier;
            damage *= DefaultBossDamageMultiplier;
        }

        return new OSEnemyPrototypeSnapshot(
            prototype.Id,
            prototype.Class,
            poolKey,
            hp,
            source.MoveSpeed,
            damage,
            prototype.ExperienceDrop,
            prototype.BodyFragmentDrop,
            prototype.HealDropChance);
    }

    private Vector3 CalculateSpawnPosition(int spawnIndex)
    {
        Vector2 center = spawnAreaCenter != null ? (Vector2)spawnAreaCenter.position : Vector2.zero;
        float width = Mathf.Max(0.1f, spawnAreaHalfExtents.x);
        float height = Mathf.Max(0.1f, spawnAreaHalfExtents.y);
        int side = spawnIndex % 4;
        float t = ((spawnIndex * 37) % 100) / 100f;
        Vector2 position;

        if (side == 0)
        {
            position = new Vector2(center.x - width, Mathf.Lerp(center.y - height, center.y + height, t));
        }
        else if (side == 1)
        {
            position = new Vector2(center.x + width, Mathf.Lerp(center.y - height, center.y + height, t));
        }
        else if (side == 2)
        {
            position = new Vector2(Mathf.Lerp(center.x - width, center.x + width, t), center.y - height);
        }
        else
        {
            position = new Vector2(Mathf.Lerp(center.x - width, center.x + width, t), center.y + height);
        }

        if (playerTarget != null && Vector2.Distance(position, playerTarget.position) < minimumPlayerDistance)
        {
            Vector2 away = (position - (Vector2)playerTarget.position).normalized;
            if (away.sqrMagnitude <= 0f)
            {
                away = Vector2.up;
            }

            position = (Vector2)playerTarget.position + away * minimumPlayerDistance;
        }

        return new Vector3(position.x, position.y, 0f);
    }

    private void SubscribeDrop(OSEnemyController enemy)
    {
        if (dropSubscriptions.TryGetValue(enemy, out Action<OSEnemyDropResult> existing))
        {
            enemy.DropRequested -= existing;
        }

        Action<OSEnemyDropResult> handler = drop => HandleDropRequested(enemy, drop);
        dropSubscriptions[enemy] = handler;
        enemy.DropRequested += handler;
    }

    private void HandleDropRequested(OSEnemyController enemy, OSEnemyDropResult drop)
    {
        if (enemy == null)
        {
            return;
        }

        Vector3 position = enemy.transform.position;
        SpawnPickup(encounterBalance.ExperiencePickupPrefabKey, OSPickupType.Experience, drop.ExperienceAmount, position);
        SpawnPickup(encounterBalance.BodyFragmentPickupPrefabKey, OSPickupType.BodyFragment, drop.BodyFragmentAmount, position);
        SpawnPickup(encounterBalance.HealPickupPrefabKey, OSPickupType.Heal, drop.HealAmount, position);

        if (dropSubscriptions.TryGetValue(enemy, out Action<OSEnemyDropResult> existing))
        {
            enemy.DropRequested -= existing;
            dropSubscriptions.Remove(enemy);
        }
    }

    private void SpawnPickup(string poolKey, OSPickupType pickupType, int amount, Vector3 position)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(poolKey))
        {
            return;
        }

        OSRuleResult<GameObject> rentResult = poolRegistry.Rent(poolKey);
        if (!rentResult.IsAccepted)
        {
            return;
        }

        OSPickup pickup = rentResult.Payload.GetComponent<OSPickup>();
        if (pickup == null)
        {
            poolRegistry.Return(rentResult.Payload);
            return;
        }

        rentResult.Payload.transform.position = position;
        OSRuleResult<OSPickupSnapshot> initializeResult = pickup.Initialize(
            $"pickup_{spawnedPickupCount + 1:0000}",
            pickupType,
            amount,
            sessionController,
            poolRegistry);

        if (!initializeResult.IsAccepted)
        {
            poolRegistry.Return(rentResult.Payload);
            return;
        }

        spawnedPickupCount++;
        PickupSpawned?.Invoke(pickup);
    }

    private void RaiseWaveEvent(OSEncounterWave wave, OSWaveEventType type)
    {
        WaveEventRaised?.Invoke(new OSWaveEvent(
            wave?.Id ?? string.Empty,
            type,
            elapsedCombatSeconds,
            wave?.EnemyId ?? string.Empty));
    }

    private OSRuleResult<int> ValidateConfiguration()
    {
        if (encounterBalance == null ||
            poolRegistry == null ||
            encounterBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            spawnAreaHalfExtents.x <= 0f ||
            spawnAreaHalfExtents.y <= 0f ||
            float.IsNaN(spawnAreaHalfExtents.x) ||
            float.IsNaN(spawnAreaHalfExtents.y) ||
            float.IsInfinity(spawnAreaHalfExtents.x) ||
            float.IsInfinity(spawnAreaHalfExtents.y))
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "wave_director_configuration_invalid");
        }

        if (!poolRegistry.IsWarmedUp)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "wave_pool_not_warmed");
        }

        if (!poolRegistry.GetUsage(FallbackEnemyPoolKey).IsAccepted)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "wave_fallback_enemy_pool_missing");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private bool CanAdvanceCombatTime()
    {
        return sessionController == null || sessionController.CurrentState == OSSessionState.Combat;
    }

    private OSWaveSnapshot CreateSnapshot()
    {
        return new OSWaveSnapshot(
            elapsedCombatSeconds,
            nextWaveIndex,
            spawnJobs.Count,
            spawnedEnemyCount,
            spawnedPickupCount,
            rejectedCapacityCount,
            bossWarningCount,
            endlessWaveIndex,
            bossSpawned);
    }

    private OSEncounterWave CreateEndlessWave()
    {
        int waveIndex = endlessWaveIndex;
        endlessWaveIndex++;

        if (endlessBossEveryWaves > 0 && (waveIndex + 1) % endlessBossEveryWaves == 0)
        {
            return new OSEncounterWave(
                $"endless_boss_{waveIndex:000}",
                OSEncounterWaveKind.SpawnBoss,
                nextEndlessWaveTime,
                "boss_swarm_core",
                1,
                0f);
        }

        if (endlessBossWarningEveryWaves > 0 && (waveIndex + 1) % endlessBossWarningEveryWaves == 0)
        {
            return new OSEncounterWave(
                $"endless_boss_warning_{waveIndex:000}",
                OSEncounterWaveKind.BossWarning,
                nextEndlessWaveTime,
                "boss_swarm_core",
                0,
                0f);
        }

        if (endlessEliteEveryWaves > 0 && (waveIndex + 1) % endlessEliteEveryWaves == 0)
        {
            return new OSEncounterWave(
                $"endless_elite_{waveIndex:000}",
                OSEncounterWaveKind.SpawnElite,
                nextEndlessWaveTime,
                "enemy_elite",
                1 + Mathf.FloorToInt(GetEndlessIntensity() * 0.35f),
                Mathf.Max(1.5f, GetScaledEndlessInterval(2.2f)));
        }

        string enemyId = SelectEndlessEnemyId(waveIndex);
        int baseCount = GetEndlessBaseCount(enemyId);
        float baseInterval = GetEndlessBaseInterval(enemyId);
        int spawnCount = Mathf.Min(endlessMaxSpawnCount, Mathf.CeilToInt(baseCount * GetEndlessIntensity()));

        return new OSEncounterWave(
            $"endless_group_{waveIndex:000}",
            OSEncounterWaveKind.SpawnGroup,
            nextEndlessWaveTime,
            enemyId,
            Mathf.Max(1, spawnCount),
            GetScaledEndlessInterval(baseInterval));
    }

    private float CalculateInitialEndlessWaveTime()
    {
        float lastScheduledTime = 0f;
        if (encounterBalance != null && encounterBalance.WaveCount > 0)
        {
            OSEncounterWave lastWave = encounterBalance.GetWaveAt(encounterBalance.WaveCount - 1);
            if (lastWave != null)
            {
                lastScheduledTime = lastWave.StartTimeSeconds;
            }
        }

        return lastScheduledTime + Mathf.Max(1f, endlessWaveIntervalSeconds);
    }

    private float GetEndlessIntensity()
    {
        float lastScheduledTime = nextEndlessWaveTime - endlessWaveIndex * Mathf.Max(1f, endlessWaveIntervalSeconds);
        float endlessSeconds = Mathf.Max(0f, nextEndlessWaveTime - lastScheduledTime);
        return 1f + endlessSeconds / 60f * Mathf.Max(0f, endlessIntensityPerMinute);
    }

    private float GetScaledEndlessInterval(float baseInterval)
    {
        float intensity = Mathf.Max(1f, GetEndlessIntensity());
        return Mathf.Max(endlessMinimumSpawnInterval, baseInterval / Mathf.Sqrt(intensity));
    }

    private static string SelectEndlessEnemyId(int waveIndex)
    {
        switch (waveIndex % 5)
        {
            case 1:
                return "enemy_charger";
            case 2:
                return "enemy_shooter";
            case 3:
                return "enemy_splitter";
            case 4:
                return "enemy_charger";
            default:
                return "enemy_chaser";
        }
    }

    private static int GetEndlessBaseCount(string enemyId)
    {
        switch (enemyId)
        {
            case "enemy_shooter":
            case "enemy_splitter":
                return 16;
            case "enemy_charger":
                return 20;
            default:
                return 24;
        }
    }

    private static float GetEndlessBaseInterval(string enemyId)
    {
        switch (enemyId)
        {
            case "enemy_shooter":
                return 1.8f;
            case "enemy_splitter":
                return 1.6f;
            case "enemy_charger":
                return 1.25f;
            default:
                return 1.15f;
        }
    }

    private void UnsubscribeAllDrops()
    {
        foreach (KeyValuePair<OSEnemyController, Action<OSEnemyDropResult>> pair in dropSubscriptions)
        {
            if (pair.Key != null)
            {
                pair.Key.DropRequested -= pair.Value;
            }
        }

        dropSubscriptions.Clear();
    }

    private static bool IsPositiveFinite(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private sealed class OSWaveSpawnJob
    {
        public OSWaveSpawnJob(OSEncounterWave wave)
        {
            EnemyId = wave.EnemyId;
            RemainingCount = wave.SpawnCount;
            SpawnIntervalSeconds = Mathf.Max(0.0001f, wave.SpawnIntervalSeconds);
            ElapsedSinceLastSpawn = SpawnIntervalSeconds;
        }

        public string EnemyId { get; }
        public int RemainingCount { get; set; }
        public float SpawnIntervalSeconds { get; }
        public float ElapsedSinceLastSpawn { get; set; }
    }
}

public enum OSWaveEventType
{
    WaveStarted,
    BossWarning,
    BossSpawned
}

public readonly struct OSWaveEvent
{
    public OSWaveEvent(string waveId, OSWaveEventType eventType, float elapsedSeconds, string enemyId)
    {
        WaveId = waveId ?? string.Empty;
        EventType = eventType;
        ElapsedSeconds = elapsedSeconds;
        EnemyId = enemyId ?? string.Empty;
    }

    public string WaveId { get; }
    public OSWaveEventType EventType { get; }
    public float ElapsedSeconds { get; }
    public string EnemyId { get; }
}

public readonly struct OSWaveSnapshot
{
    public OSWaveSnapshot(
        float elapsedCombatSeconds,
        int nextWaveIndex,
        int activeSpawnJobCount,
        int spawnedEnemyCount,
        int spawnedPickupCount,
        int rejectedCapacityCount,
        int bossWarningCount,
        int endlessWaveIndex,
        bool bossSpawned)
    {
        ElapsedCombatSeconds = elapsedCombatSeconds;
        NextWaveIndex = nextWaveIndex;
        ActiveSpawnJobCount = activeSpawnJobCount;
        SpawnedEnemyCount = spawnedEnemyCount;
        SpawnedPickupCount = spawnedPickupCount;
        RejectedCapacityCount = rejectedCapacityCount;
        BossWarningCount = bossWarningCount;
        EndlessWaveIndex = endlessWaveIndex;
        BossSpawned = bossSpawned;
    }

    public float ElapsedCombatSeconds { get; }
    public int NextWaveIndex { get; }
    public int ActiveSpawnJobCount { get; }
    public int SpawnedEnemyCount { get; }
    public int SpawnedPickupCount { get; }
    public int RejectedCapacityCount { get; }
    public int BossWarningCount { get; }
    public int EndlessWaveIndex { get; }
    public bool BossSpawned { get; }
}
