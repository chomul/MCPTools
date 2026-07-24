#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSEnemyControllerTests
{
    private GameObject host;
    private OSEnemyController enemy;
    private OSEncounterBalanceData encounterBalance;
    private float now;

    [SetUp]
    public void SetUp()
    {
        now = 10f;
        encounterBalance = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        host = new GameObject("Enemy");
        enemy = host.AddComponent<OSEnemyController>();
        enemy.ConfigureForTests(encounterBalance, null, null, () => now);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(host);
        Object.DestroyImmediate(encounterBalance);
    }

    [Test]
    public void Initialize_FromSnapshotCopiesRuntimeIdentityAndResetsState()
    {
        OSRuleResult<OSEnemySnapshot> result = enemy.Initialize(
            "enemy_runtime_001",
            CreatePrototype("enemy_chaser", OSEnemyClass.Normal, 24f, 2.2f));

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.RuntimeId, Is.EqualTo("enemy_runtime_001"));
        Assert.That(result.Payload.EnemyId, Is.EqualTo("enemy_chaser"));
        Assert.That(result.Payload.Class, Is.EqualTo(OSEnemyClass.Normal));
        Assert.That(result.Payload.CurrentHp, Is.EqualTo(24f));
        Assert.That(enemy.CurrentHp, Is.EqualTo(24f));
        Assert.That(enemy.IsDead, Is.False);
        Assert.That(enemy.IsInitialized, Is.True);
    }

    [Test]
    public void Initialize_FromEncounterBalanceUsesConfiguredEnemyId()
    {
        OSRuleResult<OSEnemySnapshot> result = enemy.Initialize("enemy_runtime_002");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.EnemyId, Is.EqualTo("enemy_chaser"));
        Assert.That(result.Payload.CurrentHp, Is.EqualTo(24f));
    }

    [Test]
    public void ApplyDamage_RejectsDuplicateAndDamageAfterDeath()
    {
        enemy.Initialize("enemy_runtime_003", CreatePrototype("enemy_chaser", OSEnemyClass.Normal, 24f, 2.2f));

        OSRuleResult<OSEnemyDamageResult> first = enemy.ApplyDamage(
            new OSDamageEvent("damage_01", OSCombatEventType.HeadDamage, 10f));
        OSRuleResult<OSEnemyDamageResult> duplicate = enemy.ApplyDamage(
            new OSDamageEvent("damage_01", OSCombatEventType.HeadDamage, 10f));
        OSRuleResult<OSEnemyDamageResult> lethal = enemy.ApplyDamage(
            new OSDamageEvent("damage_02", OSCombatEventType.HeadDamage, 20f));
        OSRuleResult<OSEnemyDamageResult> afterDeath = enemy.ApplyDamage(
            new OSDamageEvent("damage_03", OSCombatEventType.HeadDamage, 1f));

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(first.Payload.CurrentHp, Is.EqualTo(14f));
        Assert.That(duplicate.Code, Is.EqualTo(OSResultCode.Duplicate));
        Assert.That(lethal.IsAccepted, Is.True);
        Assert.That(lethal.Payload.IsLethal, Is.True);
        Assert.That(afterDeath.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(enemy.CurrentHp, Is.EqualTo(0f));
    }

    [Test]
    public void ApplyMovementLock_UsesNormalAndEliteBossDurationsWithoutStacking()
    {
        enemy.Initialize("normal", CreatePrototype("enemy_chaser", OSEnemyClass.Normal, 24f, 2.2f));
        OSRuleResult<OSEnemyControlResult> normalFirst = enemy.ApplyMovementLock(1f, 0.5f);
        now = 10.2f;
        OSRuleResult<OSEnemyControlResult> normalSecond = enemy.ApplyMovementLock(1f, 0.5f);

        Assert.That(normalFirst.Payload.MovementLockUntil, Is.EqualTo(11f).Within(0.0001f));
        Assert.That(normalSecond.Payload.MovementLockUntil, Is.EqualTo(11.2f).Within(0.0001f));

        enemy.Initialize("elite", CreatePrototype("enemy_elite", OSEnemyClass.Elite, 100f, 1.5f));
        now = 20f;
        OSRuleResult<OSEnemyControlResult> elite = enemy.ApplyMovementLock(1f, 0.5f);

        enemy.Initialize("boss", CreatePrototype("boss_swarm_core", OSEnemyClass.Boss, 1000f, 1f));
        now = 30f;
        OSRuleResult<OSEnemyControlResult> boss = enemy.ApplyMovementLock(1f, 0.5f);

        Assert.That(elite.Payload.MovementLockUntil, Is.EqualTo(20.5f).Within(0.0001f));
        Assert.That(boss.Payload.MovementLockUntil, Is.EqualTo(30.5f).Within(0.0001f));
    }

    [Test]
    public void ApplyMovementLock_DoesNotShortenExistingLongerLock()
    {
        enemy.Initialize("enemy_runtime_004", CreatePrototype("enemy_chaser", OSEnemyClass.Normal, 24f, 2.2f));
        enemy.ApplyMovementLock(2f);
        now = 10.5f;

        OSRuleResult<OSEnemyControlResult> shorter = enemy.ApplyMovementLock(0.25f);

        Assert.That(shorter.IsAccepted, Is.True);
        Assert.That(shorter.Payload.MovementLockUntil, Is.EqualTo(12f).Within(0.0001f));
    }

    [Test]
    public void DeathDropAndPoolReturn_HappenOnlyOnce()
    {
        GameObject prefab = new GameObject("EnemyPrefab");
        prefab.AddComponent<OSEnemyController>();
        GameObject poolHost = new GameObject("PoolRegistry");
        OSPoolRegistry pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[] { new OSPoolEntry("enemy_chaser", OSPoolCategory.Enemy, prefab, 1) },
            1,
            1);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);
        OSRuleResult<GameObject> rent = pool.Rent("enemy_chaser");
        OSEnemyController pooledEnemy = rent.Payload.GetComponent<OSEnemyController>();
        pooledEnemy.ConfigureForTests(encounterBalance, pool, null, () => now);
        pooledEnemy.SetHealDropRollForTests(0f);
        pooledEnemy.Initialize(
            "enemy_runtime_005",
            CreatePrototype("enemy_chaser", OSEnemyClass.Normal, 10f, 2.2f, healDropChance: 1f),
            null,
            pool);

        int damagedCount = 0;
        int diedCount = 0;
        int dropCount = 0;
        pooledEnemy.EnemyDamaged += _ => damagedCount++;
        pooledEnemy.EnemyDied += death =>
        {
            diedCount++;
            Assert.That(death.Drop.ExperienceAmount, Is.EqualTo(1));
            Assert.That(death.Drop.BodyFragmentAmount, Is.EqualTo(2));
            Assert.That(death.Drop.HealAmount, Is.EqualTo(1));
            Assert.That(death.PoolReturnCode, Is.EqualTo(OSResultCode.Accepted));
        };
        pooledEnemy.DropRequested += _ => dropCount++;

        OSRuleResult<OSEnemyDamageResult> lethal = pooledEnemy.ApplyDamage(
            new OSDamageEvent("damage_lethal", OSCombatEventType.HeadDamage, 10f));
        OSRuleResult<OSEnemyDamageResult> afterDeath = pooledEnemy.ApplyDamage(
            new OSDamageEvent("damage_after_death", OSCombatEventType.HeadDamage, 10f));
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("enemy_chaser");

        Assert.That(lethal.IsAccepted, Is.True);
        Assert.That(afterDeath.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(damagedCount, Is.EqualTo(1));
        Assert.That(diedCount, Is.EqualTo(1));
        Assert.That(dropCount, Is.EqualTo(1));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(0));
        Assert.That(usage.Payload.InactiveCount, Is.EqualTo(1));

        Object.DestroyImmediate(poolHost);
        Object.DestroyImmediate(prefab);
    }

    [Test]
    public void ContactWithPlayer_AppliesHeadDamageAndReturnsWithoutDrop()
    {
        GameObject sessionHost = null;
        GameObject healthHost = null;
        GameObject prefab = null;
        GameObject poolHost = null;
        OSPlayerBalanceData playerBalance = null;
        OSBodyBalanceData bodyBalance = null;
        OSUpgradeCatalog upgradeCatalog = null;

        try
        {
            playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
            bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
            upgradeCatalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

            healthHost = new GameObject("PlayerHealth");
            OSPlayerHealth playerHealth = healthHost.AddComponent<OSPlayerHealth>();

            sessionHost = new GameObject("GameSession");
            OSGameSessionController session = sessionHost.AddComponent<OSGameSessionController>();
            playerHealth.ConfigureForTests(playerBalance, bodyBalance, session, () => now);
            session.ConfigureForTests(playerBalance, bodyBalance, encounterBalance, upgradeCatalog, null, playerHealth);
            StartCombatSession(session);

            prefab = new GameObject("EnemyPrefab");
            prefab.AddComponent<OSEnemyController>();
            poolHost = new GameObject("PoolRegistry");
            OSPoolRegistry pool = poolHost.AddComponent<OSPoolRegistry>();
            pool.ConfigureForTests(
                new[] { new OSPoolEntry("enemy_chaser", OSPoolCategory.Enemy, prefab, 1) },
                1,
                1);
            Assert.That(pool.WarmUp().IsAccepted, Is.True);

            OSRuleResult<GameObject> rent = pool.Rent("enemy_chaser");
            OSEnemyController pooledEnemy = rent.Payload.GetComponent<OSEnemyController>();
            pooledEnemy.ConfigureForTests(encounterBalance, pool, null, () => now);
            pooledEnemy.Initialize(
                "enemy_runtime_contact",
                CreatePrototype("enemy_chaser", OSEnemyClass.Normal, 10f, 2.2f),
                null,
                pool);

            int dropCount = 0;
            int contactCount = 0;
            pooledEnemy.DropRequested += _ => dropCount++;
            pooledEnemy.EnemyContactConsumed += _ => contactCount++;

            OSRuleResult<OSEnemyContactResult> contact = pooledEnemy.TryApplyContactToPlayer(healthHost);
            OSRuleResult<OSPoolUsage> usage = pool.GetUsage("enemy_chaser");

            Assert.That(contact.IsAccepted, Is.True);
            Assert.That(contact.Payload.DamageResultCode, Is.EqualTo(OSResultCode.Accepted));
            Assert.That(contact.Payload.PoolReturnCode, Is.EqualTo(OSResultCode.Accepted));
            Assert.That(playerHealth.CurrentHp, Is.EqualTo(92f));
            Assert.That(pooledEnemy.IsDead, Is.True);
            Assert.That(dropCount, Is.EqualTo(0));
            Assert.That(contactCount, Is.EqualTo(1));
            Assert.That(usage.Payload.ActiveCount, Is.EqualTo(0));
            Assert.That(usage.Payload.InactiveCount, Is.EqualTo(1));
        }
        finally
        {
            Object.DestroyImmediate(sessionHost);
            Object.DestroyImmediate(healthHost);
            Object.DestroyImmediate(prefab);
            Object.DestroyImmediate(poolHost);
            Object.DestroyImmediate(playerBalance);
            Object.DestroyImmediate(bodyBalance);
            Object.DestroyImmediate(upgradeCatalog);
        }
    }

    [Test]
    public void ContactWithBodySegment_CutsHitSegmentThroughTailAndReturnsWithoutDrop()
    {
        GameObject bodyChainHost = null;
        GameObject segmentHost = null;
        OSBodyBalanceData bodyBalance = null;

        try
        {
            bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
            bodyChainHost = new GameObject("BodyChain");
            OSBodyChain bodyChain = bodyChainHost.AddComponent<OSBodyChain>();
            bodyChain.ConfigureForTests(bodyBalance);
            for (int i = 0; i < 4; i++)
            {
                Assert.That(bodyChain.AppendSegment(OSBodyRoleType.Attack).IsAccepted, Is.True);
            }

            int hitStableId = bodyChain.GetSegmentAt(1).StableId;
            segmentHost = new GameObject("BodySegmentCollider");
            OSBodySegmentCollider segmentCollider = segmentHost.AddComponent<OSBodySegmentCollider>();
            segmentCollider.Bind(bodyChain, hitStableId, OSBodyRoleType.Attack);

            enemy.Initialize(
                "enemy_runtime_body_contact",
                CreatePrototype("enemy_chaser", OSEnemyClass.Normal, 10f, 2.2f));

            int dropCount = 0;
            int contactCount = 0;
            enemy.DropRequested += _ => dropCount++;
            enemy.EnemyContactConsumed += _ => contactCount++;

            OSRuleResult<OSEnemyContactResult> contact = enemy.TryApplyContactToPlayer(segmentHost);

            Assert.That(contact.IsAccepted, Is.True);
            Assert.That(contact.Payload.DamageResultCode, Is.EqualTo(OSResultCode.Accepted));
            Assert.That(contact.Payload.PoolReturnCode, Is.EqualTo(OSResultCode.Accepted));
            Assert.That(bodyChain.ActiveSegmentCount, Is.EqualTo(1));
            Assert.That(enemy.IsDead, Is.True);
            Assert.That(dropCount, Is.EqualTo(0));
            Assert.That(contactCount, Is.EqualTo(1));
        }
        finally
        {
            Object.DestroyImmediate(segmentHost);
            Object.DestroyImmediate(bodyChainHost);
            Object.DestroyImmediate(bodyBalance);
        }
    }

    [Test]
    public void TickMovement_MovesTowardTargetUnlessLocked()
    {
        GameObject target = new GameObject("Target");
        target.transform.position = new Vector3(10f, 0f, 0f);
        host.transform.position = Vector3.zero;
        enemy.Initialize(
            "enemy_runtime_006",
            CreatePrototype("enemy_chaser", OSEnemyClass.Normal, 24f, 2f),
            target.transform);

        OSRuleResult<Vector2> moved = enemy.TickMovement(0.5f);
        enemy.ApplyMovementLock(1f);
        OSRuleResult<Vector2> locked = enemy.TickMovement(0.5f);

        Assert.That(moved.IsAccepted, Is.True);
        Assert.That(moved.Payload.x, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(locked.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(host.transform.position.x, Is.EqualTo(1f).Within(0.0001f));
        Object.DestroyImmediate(target);
    }

    private static OSEnemyPrototypeSnapshot CreatePrototype(
        string id,
        OSEnemyClass enemyClass,
        float hp,
        float speed,
        float healDropChance = 0f)
    {
        return new OSEnemyPrototypeSnapshot(
            id,
            enemyClass,
            id,
            hp,
            speed,
            8f,
            1,
            2,
            healDropChance);
    }

    private static void StartCombatSession(OSGameSessionController session)
    {
        Assert.That(session.StartSession().IsAccepted, Is.True);
        Assert.That(session.CompleteCurrentSelection(0).IsAccepted, Is.True);
        Assert.That(session.CompleteCurrentSelection(1).IsAccepted, Is.True);
        Assert.That(session.CurrentState, Is.EqualTo(OSSessionState.Combat));
    }
}
#endif
