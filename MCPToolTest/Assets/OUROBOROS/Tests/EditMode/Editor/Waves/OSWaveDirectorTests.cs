#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class OSWaveDirectorTests
{
    private GameObject directorHost;
    private OSWaveDirector director;
    private OSEncounterBalanceData encounterBalance;
    private OSPlayerBalanceData playerBalance;
    private OSBodyBalanceData bodyBalance;
    private OSUpgradeCatalog upgradeCatalog;
    private GameObject sessionHost;
    private OSGameSessionController session;
    private GameObject poolHost;
    private OSPoolRegistry pool;
    private GameObject enemyPrefab;
    private GameObject pickupPrefab;
    private GameObject playerTargetHost;

    [SetUp]
    public void SetUp()
    {
        encounterBalance = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        upgradeCatalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        enemyPrefab = CreateEnemyPrefab();
        pickupPrefab = CreatePickupPrefab();
        poolHost = new GameObject("PoolRegistry");
        pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[]
            {
                new OSPoolEntry("enemy_chaser", OSPoolCategory.Enemy, enemyPrefab, 80),
                new OSPoolEntry("enemy_charger", OSPoolCategory.Enemy, enemyPrefab, 48),
                new OSPoolEntry("enemy_shooter", OSPoolCategory.Enemy, enemyPrefab, 32),
                new OSPoolEntry("enemy_splitter", OSPoolCategory.Enemy, enemyPrefab, 32),
                new OSPoolEntry("enemy_elite", OSPoolCategory.Enemy, enemyPrefab, 8),
                new OSPoolEntry("boss_swarm_core", OSPoolCategory.Enemy, enemyPrefab, 2),
                new OSPoolEntry("pickup_experience", OSPoolCategory.Pickup, pickupPrefab, 32),
                new OSPoolEntry("pickup_body_fragment", OSPoolCategory.Pickup, pickupPrefab, 32),
                new OSPoolEntry("pickup_heal", OSPoolCategory.Pickup, pickupPrefab, 8)
            },
            180,
            1,
            72);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);

        sessionHost = new GameObject("GameSession");
        session = sessionHost.AddComponent<OSGameSessionController>();
        session.ConfigureForTests(playerBalance, bodyBalance, encounterBalance, upgradeCatalog);

        playerTargetHost = new GameObject("PlayerTarget");
        directorHost = new GameObject("WaveDirector");
        director = directorHost.AddComponent<OSWaveDirector>();
        director.ConfigureForTests(
            encounterBalance,
            pool,
            session,
            playerTargetHost.transform,
            null,
            new Vector2(8f, 4f));
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(directorHost);
        Object.DestroyImmediate(playerTargetHost);
        Object.DestroyImmediate(sessionHost);
        Object.DestroyImmediate(poolHost);
        Object.DestroyImmediate(enemyPrefab);
        Object.DestroyImmediate(pickupPrefab);
        Object.DestroyImmediate(encounterBalance);
        Object.DestroyImmediate(playerBalance);
        Object.DestroyImmediate(bodyBalance);
        Object.DestroyImmediate(upgradeCatalog);
    }

    [Test]
    public void Tick_AdvancesOnlyDuringCombatState()
    {
        Assert.That(session.StartSession().IsAccepted, Is.True);
        Assert.That(director.BeginWaves().IsAccepted, Is.True);

        OSRuleResult<OSWaveSnapshot> selectionTick = director.Tick(10f);

        Assert.That(selectionTick.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(director.ElapsedCombatSeconds, Is.EqualTo(0f));

        CompleteStartingSelections();
        OSRuleResult<OSWaveSnapshot> combatTick = director.Tick(1f);

        Assert.That(combatTick.IsAccepted, Is.True);
        Assert.That(combatTick.Payload.ElapsedCombatSeconds, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(combatTick.Payload.SpawnedEnemyCount, Is.EqualTo(1));
    }

    [Test]
    public void Tick_EarlyPressureScheduleAddsWaveJobsAndSpawnsMoreEnemies()
    {
        StartCombatWaves();

        director.Tick(1f);
        int introSpawnCount = director.SpawnedEnemyCount;
        OSRuleResult<OSWaveSnapshot> expanded = director.Tick(20f);

        Assert.That(introSpawnCount, Is.EqualTo(1));
        Assert.That(expanded.IsAccepted, Is.True);
        Assert.That(expanded.Payload.NextWaveIndex, Is.GreaterThanOrEqualTo(2));
        Assert.That(expanded.Payload.SpawnedEnemyCount, Is.GreaterThan(introSpawnCount));
    }

    [Test]
    public void Tick_ContinuesSpawningAfterFirstMinute()
    {
        StartCombatWaves();

        OSRuleResult<OSWaveSnapshot> oneMinute = director.Tick(60f);
        int spawnedAtOneMinute = oneMinute.Payload.SpawnedEnemyCount;
        OSRuleResult<OSWaveSnapshot> afterOneMinute = director.Tick(25f);

        Assert.That(oneMinute.IsAccepted, Is.True);
        Assert.That(oneMinute.Payload.NextWaveIndex, Is.GreaterThanOrEqualTo(4));
        Assert.That(spawnedAtOneMinute, Is.GreaterThan(0));
        Assert.That(afterOneMinute.IsAccepted, Is.True);
        Assert.That(afterOneMinute.Payload.SpawnedEnemyCount, Is.GreaterThan(spawnedAtOneMinute));
    }

    [Test]
    public void ConfiguredPool_IncludesEveryEncounterEnemyPrefabKey()
    {
        string[] enemyKeys =
        {
            "enemy_chaser",
            "enemy_charger",
            "enemy_shooter",
            "enemy_splitter",
            "enemy_elite",
            "boss_swarm_core"
        };

        for (int i = 0; i < enemyKeys.Length; i++)
        {
            OSRuleResult<OSPoolUsage> usage = pool.GetUsage(enemyKeys[i]);

            Assert.That(usage.IsAccepted, Is.True, enemyKeys[i]);
            Assert.That(usage.Payload.Category, Is.EqualTo(OSPoolCategory.Enemy), enemyKeys[i]);
        }
    }

    [Test]
    public void SpawnEnemyForTests_UsesConfiguredEnemyPoolWithoutFallback()
    {
        StartCombatWaves();

        OSRuleResult<OSEnemyController> spawned = director.SpawnEnemyForTests("enemy_charger");

        Assert.That(spawned.IsAccepted, Is.True);
        Assert.That(spawned.Payload.EnemyId, Is.EqualTo("enemy_charger"));
        Assert.That(pool.GetUsage("enemy_charger").Payload.ActiveCount, Is.EqualTo(1));
        Assert.That(pool.GetUsage("enemy_chaser").Payload.ActiveCount, Is.EqualTo(0));
    }

    [Test]
    public void Tick_ResumesActiveSpawnJobAfterLevelUpSelectionCompletes()
    {
        StartCombatWaves();

        Assert.That(director.Tick(1f).IsAccepted, Is.True);
        int beforeSelectionSpawnCount = director.SpawnedEnemyCount;
        Assert.That(beforeSelectionSpawnCount, Is.EqualTo(1));

        Assert.That(session.EnqueueGeneratedLevelUpSelection().IsAccepted, Is.True);
        Assert.That(session.ProcessFixedUpdate().IsAccepted, Is.True);
        Assert.That(session.CurrentState, Is.EqualTo(OSSessionState.LevelUpSelection));
        OSRuleResult<OSWaveSnapshot> selectionTick = director.Tick(3f);

        Assert.That(selectionTick.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(director.SpawnedEnemyCount, Is.EqualTo(beforeSelectionSpawnCount));

        Assert.That(session.CompleteCurrentSelection(0).IsAccepted, Is.True);
        Assert.That(session.CurrentState, Is.EqualTo(OSSessionState.Combat));
        OSRuleResult<OSWaveSnapshot> resumedTick = director.Tick(0.8f);

        Assert.That(resumedTick.IsAccepted, Is.True);
        Assert.That(resumedTick.Payload.SpawnedEnemyCount, Is.GreaterThan(beforeSelectionSpawnCount));
    }

    [Test]
    public void Tick_ThreeSixNineTenMinuteEventsReachEliteWarningAndBoss()
    {
        StartCombatWaves();
        List<OSWaveEvent> events = new List<OSWaveEvent>();
        director.WaveEventRaised += events.Add;

        director.Tick(180f);
        ReturnActiveEnemiesToPool();
        director.Tick(180f);
        ReturnActiveEnemiesToPool();
        director.Tick(180f);
        ReturnActiveEnemiesToPool();
        director.Tick(60f);

        Assert.That(events.Exists(e => e.EventType == OSWaveEventType.WaveStarted && e.EnemyId == "enemy_elite"), Is.True);
        Assert.That(director.BossWarningCount, Is.EqualTo(1));
        Assert.That(director.BossSpawned, Is.True);
        Assert.That(FindSpawnedEnemy("boss_swarm_core"), Is.Not.Null);
    }

    [Test]
    public void Tick_AfterTenMinuteBossContinuesWithEndlessPressure()
    {
        StartCombatWaves();

        director.Tick(600f);
        Assert.That(director.BossSpawned, Is.True);
        Assert.That(director.NextWaveIndex, Is.EqualTo(encounterBalance.WaveCount));

        ReturnActiveEnemiesToPool();
        int spawnedBeforeEndless = director.SpawnedEnemyCount;
        OSRuleResult<OSWaveSnapshot> endlessTick = director.Tick(21f);

        Assert.That(endlessTick.IsAccepted, Is.True);
        Assert.That(endlessTick.Payload.EndlessWaveIndex, Is.GreaterThan(0));
        Assert.That(endlessTick.Payload.SpawnedEnemyCount, Is.GreaterThan(spawnedBeforeEndless));
        Assert.That(endlessTick.Payload.ActiveSpawnJobCount, Is.GreaterThan(0));
    }

    [Test]
    public void Tick_EnemyLimitRejectsAdditionalSpawnsWithoutConsumingJob()
    {
        Object.DestroyImmediate(poolHost);
        poolHost = new GameObject("TinyPoolRegistry");
        pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[]
            {
                new OSPoolEntry("enemy_chaser", OSPoolCategory.Enemy, enemyPrefab, 1),
                new OSPoolEntry("pickup_experience", OSPoolCategory.Pickup, pickupPrefab, 1),
                new OSPoolEntry("pickup_body_fragment", OSPoolCategory.Pickup, pickupPrefab, 1),
                new OSPoolEntry("pickup_heal", OSPoolCategory.Pickup, pickupPrefab, 1)
            },
            1,
            1,
            3);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);
        director.ConfigureForTests(encounterBalance, pool, session, playerTargetHost.transform);
        StartCombatWaves();

        director.Tick(1f);
        OSRuleResult<OSWaveSnapshot> capped = director.Tick(1f);

        Assert.That(capped.IsAccepted, Is.True);
        Assert.That(capped.Payload.SpawnedEnemyCount, Is.EqualTo(1));
        Assert.That(capped.Payload.ActiveSpawnJobCount, Is.EqualTo(1));
        Assert.That(capped.Payload.RejectedCapacityCount, Is.GreaterThan(0));
    }

    [Test]
    public void SpawnedEnemyDeathRequestsPickupsThroughT18Pipeline()
    {
        StartCombatWaves();
        OSEnemyController enemy = director.SpawnEnemyForTests("enemy_chaser").Payload;

        OSRuleResult<OSEnemyDamageResult> lethal = enemy.ApplyDamage(
            new OSDamageEvent("wave_damage_lethal", OSCombatEventType.HeadDamage, 999f));

        Assert.That(lethal.IsAccepted, Is.True);
        Assert.That(director.SpawnedPickupCount, Is.EqualTo(2));
        Assert.That(pool.GetUsage("pickup_experience").Payload.ActiveCount, Is.EqualTo(1));
        Assert.That(pool.GetUsage("pickup_body_fragment").Payload.ActiveCount, Is.EqualTo(1));
    }

    private void StartCombatWaves()
    {
        Assert.That(session.StartSession().IsAccepted, Is.True);
        CompleteStartingSelections();
        Assert.That(director.BeginWaves().IsAccepted, Is.True);
    }

    private void CompleteStartingSelections()
    {
        Assert.That(session.CompleteCurrentSelection(0).IsAccepted, Is.True);
        Assert.That(session.CompleteCurrentSelection(1).IsAccepted, Is.True);
    }

    private OSEnemyController FindSpawnedEnemy(string enemyId)
    {
        IReadOnlyList<OSEnemyController> enemies = OSEnemyController.ActiveEnemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            if (enemies[i] != null && enemies[i].EnemyId == enemyId)
            {
                return enemies[i];
            }
        }

        return null;
    }

    private void ReturnActiveEnemiesToPool()
    {
        List<OSEnemyController> enemies = new List<OSEnemyController>(OSEnemyController.ActiveEnemies);
        for (int i = 0; i < enemies.Count; i++)
        {
            if (enemies[i] != null)
            {
                pool.Return(enemies[i].gameObject);
            }
        }
    }

    private static GameObject CreateEnemyPrefab()
    {
        GameObject prefab = new GameObject("EnemyPrefab");
        prefab.AddComponent<OSEnemyController>();
        return prefab;
    }

    private static GameObject CreatePickupPrefab()
    {
        GameObject prefab = new GameObject("PickupPrefab");
        prefab.AddComponent<OSPickup>();
        return prefab;
    }
}
#endif
