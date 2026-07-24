#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSExplosionControllerTests
{
    private GameObject explosionHost;
    private GameObject bodyChainHost;
    private GameObject sessionHost;
    private GameObject healthHost;
    private GameObject poolHost;
    private GameObject telegraphPrefab;
    private GameObject blastPrefab;
    private OSExplosionController explosionController;
    private OSBodyChain bodyChain;
    private OSGameSessionController sessionController;
    private OSPlayerHealth playerHealth;
    private OSPoolRegistry poolRegistry;
    private OSBodyBalanceData bodyBalance;
    private OSPlayerBalanceData playerBalance;
    private OSEncounterBalanceData encounterBalance;
    private OSUpgradeCatalog upgradeCatalog;
    private GameObject[] enemyHosts;
    private float currentTime;

    [SetUp]
    public void SetUp()
    {
        currentTime = 10f;
        playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        encounterBalance = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        upgradeCatalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        bodyChainHost = new GameObject("BodyChain");
        bodyChain = bodyChainHost.AddComponent<OSBodyChain>();
        bodyChain.ConfigureForTests(bodyBalance);
        bodyChain.RecordHeadPosition(Vector2.zero, Vector2.right);

        healthHost = new GameObject("PlayerHealth");
        playerHealth = healthHost.AddComponent<OSPlayerHealth>();
        playerHealth.ConfigureForTests(playerBalance, bodyBalance, null, () => currentTime);

        telegraphPrefab = new GameObject("ExplosionTelegraphPrefab");
        blastPrefab = new GameObject("ExplosionBlastPrefab");
        poolHost = new GameObject("PoolRegistry");
        poolRegistry = poolHost.AddComponent<OSPoolRegistry>();
        poolRegistry.ConfigureForTests(
            new[]
            {
                new OSPoolEntry("effect_explosion_telegraph", OSPoolCategory.Effect, telegraphPrefab, 64),
                new OSPoolEntry("effect_explosion_blast", OSPoolCategory.Effect, blastPrefab, 64)
            },
            180,
            120,
            120,
            128);
        Assert.That(poolRegistry.WarmUp().IsAccepted, Is.True);

        sessionHost = new GameObject("GameSession");
        sessionController = sessionHost.AddComponent<OSGameSessionController>();
        sessionController.ConfigureForTests(
            playerBalance,
            bodyBalance,
            encounterBalance,
            upgradeCatalog,
            null,
            playerHealth);

        explosionHost = new GameObject("ExplosionController");
        explosionController = explosionHost.AddComponent<OSExplosionController>();
        explosionController.ConfigureForTests(bodyBalance, bodyChain, playerHealth, sessionController, null, poolRegistry);
        enemyHosts = new GameObject[0];
        StartCombatSession();
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = 0; i < enemyHosts.Length; i++)
        {
            if (enemyHosts[i] != null)
            {
                Object.DestroyImmediate(enemyHosts[i]);
            }
        }

        Object.DestroyImmediate(explosionHost);
        Object.DestroyImmediate(sessionHost);
        Object.DestroyImmediate(healthHost);
        Object.DestroyImmediate(poolHost);
        Object.DestroyImmediate(telegraphPrefab);
        Object.DestroyImmediate(blastPrefab);
        Object.DestroyImmediate(bodyChainHost);
        Object.DestroyImmediate(playerBalance);
        Object.DestroyImmediate(bodyBalance);
        Object.DestroyImmediate(encounterBalance);
        Object.DestroyImmediate(upgradeCatalog);
    }

    [Test]
    public void TryRequestExplosion_RejectsWithThreeSegmentsAndDoesNotReserve()
    {
        AppendMany(3);

        OSRuleResult<OSExplosionSnapshot> result = explosionController.TryRequestExplosion();

        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(3));
        Assert.That(bodyChain.ReservedTailCount, Is.EqualTo(0));
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.Combat));
    }

    [Test]
    public void TryRequestEncircleExplosion_ClosedLoopWithEnemyStartsTelegraphWithoutInput()
    {
        BuildClosedLoop(15);
        CreateEnemy("enemy_inside_loop", new Vector2(0f, 0.5f), 100f);

        OSRuleResult<OSExplosionSnapshot> result = explosionController.TryRequestEncircleExplosion();

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.IsPending, Is.True);
        Assert.That(result.Payload.ActiveSegmentCountAtRequest, Is.EqualTo(15));
        Assert.That(bodyChain.ReservedTailCount, Is.EqualTo(5));
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.ExplosionTelegraph));
    }

    [Test]
    public void TryRequestEncircleExplosion_OpenChainWithNearbyEnemyDoesNotTrigger()
    {
        BuildOpenChain(15);
        CreateEnemy("enemy_near_open_chain", new Vector2(0f, 0.5f), 100f);

        OSRuleResult<OSExplosionSnapshot> result = explosionController.TryRequestEncircleExplosion();

        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(result.ReasonKey, Is.EqualTo("explosion_encircle_condition_missing"));
        Assert.That(explosionController.HasPendingExplosion, Is.False);
        Assert.That(bodyChain.ReservedTailCount, Is.EqualTo(0));
    }

    [Test]
    public void TryRequestEncircleExplosion_ClosedLoopWithoutEnemyDoesNotTrigger()
    {
        BuildClosedLoop(15);
        CreateEnemy("enemy_outside_loop", new Vector2(4f, 4f), 100f);

        OSRuleResult<OSExplosionSnapshot> result = explosionController.TryRequestEncircleExplosion();

        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(result.ReasonKey, Is.EqualTo("explosion_encircle_condition_missing"));
        Assert.That(explosionController.HasPendingExplosion, Is.False);
        Assert.That(bodyChain.ReservedTailCount, Is.EqualTo(0));
    }

    [Test]
    public void Tick_EncircleTriggeredExplosionDamagesInsideEnemyOnce()
    {
        BuildClosedLoop(15);
        OSEnemyController enemy = CreateEnemy("enemy_inside_loop", new Vector2(0f, 0.5f), 300f);

        OSRuleResult<OSExplosionSnapshot> request = explosionController.TryRequestEncircleExplosion();
        OSRuleResult<OSExplosionTickResult> tick = explosionController.Tick(0.25f);

        Assert.That(request.IsAccepted, Is.True);
        Assert.That(tick.IsAccepted, Is.True);
        Assert.That(tick.Payload.DidComplete, Is.True);
        Assert.That(tick.Payload.Completion.EnemyHitCount, Is.EqualTo(1));
        Assert.That(tick.Payload.Completion.DamagePerEnemy, Is.EqualTo(175f).Within(0.0001f));
        Assert.That(enemy.CurrentHp, Is.EqualTo(125f).Within(0.0001f));
    }

    [TestCase(4, 2)]
    [TestCase(5, 2)]
    [TestCase(10, 3)]
    [TestCase(64, 20)]
    public void TryRequestExplosion_ConsumptionBoundariesUseCeilThirtyPercent(int segmentCount, int expectedConsumed)
    {
        AppendMany(segmentCount);

        OSRuleResult<OSExplosionSnapshot> result = explosionController.TryRequestExplosion();

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.ActiveSegmentCountAtRequest, Is.EqualTo(segmentCount));
        Assert.That(result.Payload.ConsumedSegmentCount, Is.EqualTo(expectedConsumed));
        Assert.That(result.Payload.ReservedSegments.Length, Is.EqualTo(expectedConsumed));
        Assert.That(bodyChain.ReservedTailCount, Is.EqualTo(expectedConsumed));
    }

    [Test]
    public void Tick_BeforeTelegraphEndsKeepsReservedSegmentsAlive()
    {
        AppendMany(4);
        explosionController.TryRequestExplosion();

        OSRuleResult<OSExplosionTickResult> tick = explosionController.Tick(0.24f);

        Assert.That(tick.IsAccepted, Is.True);
        Assert.That(tick.Payload.DidComplete, Is.False);
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(4));
        Assert.That(bodyChain.ReservedTailCount, Is.EqualTo(2));
        Assert.That(explosionController.HasPendingExplosion, Is.True);
    }

    [Test]
    public void Tick_AfterTelegraphDamagesOnceConsumesReservedTailAndAppliesInvulnerability()
    {
        AppendMany(5);
        OSEnemyController enemy = CreateEnemy("enemy_overlap", new Vector2(0f, 0f), 200f);

        OSRuleResult<OSExplosionSnapshot> request = explosionController.TryRequestExplosion();
        OSRuleResult<OSExplosionTickResult> tick = explosionController.Tick(0.25f);

        Assert.That(request.IsAccepted, Is.True);
        Assert.That(tick.IsAccepted, Is.True);
        Assert.That(tick.Payload.DidComplete, Is.True);
        Assert.That(tick.Payload.Completion.ReservedSegmentCount, Is.EqualTo(2));
        Assert.That(tick.Payload.Completion.ConsumedSegmentCount, Is.EqualTo(2));
        Assert.That(tick.Payload.Completion.RemainingSegmentCount, Is.EqualTo(3));
        Assert.That(tick.Payload.Completion.EnemyHitCount, Is.EqualTo(1));
        Assert.That(tick.Payload.Completion.DamagePerEnemy, Is.EqualTo(70f).Within(0.0001f));
        Assert.That(enemy.CurrentHp, Is.EqualTo(130f).Within(0.0001f));
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(3));
        Assert.That(bodyChain.ReservedTailCount, Is.EqualTo(0));
        Assert.That(playerHealth.ExplosionInvulnerableUntil, Is.EqualTo(10.4f).Within(0.0001f));
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.ExplosionTelegraph));

        sessionController.ProcessFixedUpdate();

        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.Combat));
    }

    [Test]
    public void Tick_UsesReservedPositionsEvenIfChainMovesBeforeCompletion()
    {
        AppendMany(4);
        OSEnemyController enemy = CreateEnemy("enemy_original_position", new Vector2(0f, 0f), 120f);

        explosionController.TryRequestExplosion();
        bodyChain.RecordHeadPosition(new Vector2(20f, 0f), Vector2.right);
        OSRuleResult<OSExplosionTickResult> tick = explosionController.Tick(0.25f);

        Assert.That(tick.IsAccepted, Is.True);
        Assert.That(tick.Payload.Completion.EnemyHitCount, Is.EqualTo(1));
        Assert.That(enemy.CurrentHp, Is.EqualTo(50f).Within(0.0001f));
    }

    [Test]
    public void Tick_ConsumesOnlyOriginallyReservedTailWhenNewSegmentsAreAddedDuringTelegraph()
    {
        AppendMany(5);
        explosionController.TryRequestExplosion();

        OSRuleResult<OSBodySegmentSnapshot> sixth = bodyChain.AppendSegment(OSBodyRoleType.Control);
        OSRuleResult<OSBodySegmentSnapshot> seventh = bodyChain.AppendSegment(OSBodyRoleType.Laser);
        OSRuleResult<OSExplosionTickResult> tick = explosionController.Tick(0.25f);

        Assert.That(sixth.IsAccepted, Is.True);
        Assert.That(seventh.IsAccepted, Is.True);
        Assert.That(tick.IsAccepted, Is.True);
        Assert.That(tick.Payload.Completion.ConsumedSegmentCount, Is.EqualTo(2));
        Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(5));
        Assert.That(ContainsStableId(bodyChain.CreateSnapshot(), sixth.Payload.StableId), Is.True);
        Assert.That(ContainsStableId(bodyChain.CreateSnapshot(), seventh.Payload.StableId), Is.True);
    }

    [Test]
    public void Tick_SameEnemyInMultipleReservedRadiiTakesDamageOnlyOnce()
    {
        AppendMany(10);
        OSEnemyController enemy = CreateEnemy("enemy_overlap", new Vector2(0f, 0f), 300f);

        explosionController.TryRequestExplosion();
        OSRuleResult<OSExplosionTickResult> tick = explosionController.Tick(0.25f);

        Assert.That(tick.IsAccepted, Is.True);
        Assert.That(tick.Payload.Completion.ReservedSegmentCount, Is.EqualTo(3));
        Assert.That(tick.Payload.Completion.EnemyHitCount, Is.EqualTo(1));
        Assert.That(tick.Payload.Completion.DamagePerEnemy, Is.EqualTo(105f).Within(0.0001f));
        Assert.That(enemy.CurrentHp, Is.EqualTo(195f).Within(0.0001f));
    }

    [Test]
    public void Tick_CutDuringTelegraph_DamagesAndConsumesOnlySurvivingReservedSegments()
    {
        AppendMany(5);
        OSEnemyController enemy = CreateEnemy("enemy_cut_tail", new Vector2(0f, 0f), 100f);

        explosionController.TryRequestExplosion();
        OSRuleResult<OSBodyCutResult> cut = bodyChain.TryCutFrom(4);
        OSRuleResult<OSExplosionTickResult> tick = explosionController.Tick(0.25f);

        Assert.That(cut.IsAccepted, Is.True);
        Assert.That(tick.IsAccepted, Is.True);
        Assert.That(tick.Payload.DidComplete, Is.True);
        Assert.That(tick.Payload.Completion.ReservedSegmentCount, Is.EqualTo(1));
        Assert.That(tick.Payload.Completion.ConsumedSegmentCount, Is.EqualTo(1));
        Assert.That(tick.Payload.Completion.RemainingSegmentCount, Is.EqualTo(3));
        Assert.That(tick.Payload.Completion.DamagePerEnemy, Is.EqualTo(35f).Within(0.0001f));
        Assert.That(enemy.CurrentHp, Is.EqualTo(65f).Within(0.0001f));
    }

    private void StartCombatSession()
    {
        Assert.That(sessionController.StartSession().IsAccepted, Is.True);
        Assert.That(sessionController.CompleteCurrentSelection(0).IsAccepted, Is.True);
        Assert.That(sessionController.CompleteCurrentSelection(1).IsAccepted, Is.True);
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.Combat));
    }

    private void AppendMany(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.That(bodyChain.AppendSegment(OSBodyRoleType.Attack).IsAccepted, Is.True);
        }
    }

    private void BuildClosedLoop(int segmentCount)
    {
        bodyChain.RecordHeadPosition(new Vector2(0.05f, 0f), Vector2.right);
        bodyChain.RecordHeadPosition(new Vector2(1f, 0f), Vector2.up);
        bodyChain.RecordHeadPosition(new Vector2(1f, 1f), Vector2.left);
        bodyChain.RecordHeadPosition(new Vector2(0f, 1f), Vector2.left);
        bodyChain.RecordHeadPosition(new Vector2(-1f, 1f), Vector2.down);
        bodyChain.RecordHeadPosition(new Vector2(-1f, 0f), Vector2.right);
        bodyChain.RecordHeadPosition(Vector2.zero, Vector2.right);
        AppendMany(segmentCount);
    }

    private void BuildOpenChain(int segmentCount)
    {
        bodyChain.RecordHeadPosition(new Vector2(-4f, 0f), Vector2.right);
        bodyChain.RecordHeadPosition(new Vector2(-3f, 0f), Vector2.right);
        bodyChain.RecordHeadPosition(new Vector2(-2f, 0f), Vector2.right);
        bodyChain.RecordHeadPosition(new Vector2(-1f, 0f), Vector2.right);
        bodyChain.RecordHeadPosition(Vector2.zero, Vector2.right);
        AppendMany(segmentCount);
    }

    private OSEnemyController CreateEnemy(string runtimeId, Vector2 position, float maxHp)
    {
        GameObject host = new GameObject(runtimeId);
        host.transform.position = position;
        OSEnemyController enemy = host.AddComponent<OSEnemyController>();
        OSRuleResult<OSEnemySnapshot> result = enemy.Initialize(
            runtimeId,
            new OSEnemyPrototypeSnapshot(
                "enemy_chaser",
                OSEnemyClass.Normal,
                "enemy_chaser",
                maxHp,
                2f,
                8f,
                1,
                1,
                0f));

        Assert.That(result.IsAccepted, Is.True);
        AddEnemyHost(host);
        return enemy;
    }

    private void AddEnemyHost(GameObject host)
    {
        GameObject[] next = new GameObject[enemyHosts.Length + 1];
        for (int i = 0; i < enemyHosts.Length; i++)
        {
            next[i] = enemyHosts[i];
        }

        next[next.Length - 1] = host;
        enemyHosts = next;
    }

    private static bool ContainsStableId(OSBodyChainSnapshot snapshot, int stableId)
    {
        for (int i = 0; i < snapshot.Segments.Length; i++)
        {
            if (snapshot.Segments[i].StableId == stableId)
            {
                return true;
            }
        }

        return false;
    }
}
#endif
