#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSProjectileTests
{
    private GameObject projectileHost;
    private OSProjectile projectile;
    private GameObject enemyHost;
    private OSEnemyController enemy;
    private OSEncounterBalanceData encounterBalance;

    [SetUp]
    public void SetUp()
    {
        projectileHost = new GameObject("Projectile");
        projectile = projectileHost.AddComponent<OSProjectile>();
        encounterBalance = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        enemyHost = new GameObject("Enemy");
        enemy = enemyHost.AddComponent<OSEnemyController>();
        enemy.ConfigureForTests(encounterBalance);
        enemy.Initialize(
            "enemy_runtime_001",
            new OSEnemyPrototypeSnapshot(
                "enemy_chaser",
                OSEnemyClass.Normal,
                "enemy_chaser",
                24f,
                2f,
                8f,
                1,
                1,
                0f));
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(projectileHost);
        Object.DestroyImmediate(enemyHost);
        Object.DestroyImmediate(encounterBalance);
    }

    [Test]
    public void Initialize_SetsOwnerVelocityLifetimePayloadAndEventId()
    {
        OSProjectilePayload payload = OSProjectilePayload.CreateDamage(10f);

        OSRuleResult<OSProjectileSnapshot> result = projectile.Initialize(
            "player_head",
            "shot_001",
            Vector2.zero,
            Vector2.right * 8f,
            1.25f,
            payload);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.OwnerId, Is.EqualTo("player_head"));
        Assert.That(result.Payload.EventId, Is.EqualTo("shot_001"));
        Assert.That(result.Payload.Velocity, Is.EqualTo(Vector2.right * 8f));
        Assert.That(result.Payload.LifetimeSeconds, Is.EqualTo(1.25f));
        Assert.That(result.Payload.Payload.Kind, Is.EqualTo(OSProjectilePayloadKind.Damage));
        Assert.That(result.Payload.Payload.Damage, Is.EqualTo(10f));
        Assert.That(projectile.HasReturned, Is.False);
    }

    [Test]
    public void Tick_MovesAndReturnsToPoolWhenLifetimeExpires()
    {
        GameObject prefab = CreateProjectilePrefab();
        GameObject poolHost = new GameObject("PoolRegistry");
        OSPoolRegistry pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[] { new OSPoolEntry("projectile_head_basic", OSPoolCategory.Projectile, prefab, 1) },
            1,
            1);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);
        OSRuleResult<GameObject> rent = pool.Rent("projectile_head_basic");
        OSProjectile pooledProjectile = rent.Payload.GetComponent<OSProjectile>();
        pooledProjectile.ConfigureForTests("projectile_head_basic", pool);
        pooledProjectile.Initialize(
            "player_head",
            "shot_life",
            Vector2.zero,
            Vector2.right * 2f,
            0.5f,
            OSProjectilePayload.CreateDamage(5f),
            pool);

        OSRuleResult<OSProjectileSnapshot> tick = pooledProjectile.Tick(0.5f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_head_basic");

        Assert.That(tick.IsAccepted, Is.True);
        Assert.That(tick.Payload.Position.x, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(tick.Payload.HasReturned, Is.True);
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(0));
        Assert.That(usage.Payload.InactiveCount, Is.EqualTo(1));

        Object.DestroyImmediate(poolHost);
        Object.DestroyImmediate(prefab);
    }

    [Test]
    public void DamageProjectile_HitsSameEnemyOnlyOnceAndReturns()
    {
        int hitCount = 0;
        int returnCount = 0;
        projectile.ProjectileHit += _ => hitCount++;
        projectile.ProjectileReturned += _ => returnCount++;
        projectile.Initialize(
            "player_head",
            "shot_damage",
            Vector2.right,
            1f,
            OSProjectilePayload.CreateDamage(8f));

        OSRuleResult<OSProjectileHitResult> first = projectile.HitEnemy(enemy);
        OSRuleResult<OSProjectileHitResult> second = projectile.HitEnemy(enemy);

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(second.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(enemy.CurrentHp, Is.EqualTo(16f));
        Assert.That(hitCount, Is.EqualTo(1));
        Assert.That(returnCount, Is.EqualTo(1));
        Assert.That(projectile.HasReturned, Is.True);
    }

    [Test]
    public void ControlProjectile_AppliesMovementLockWithoutDamageWhenDamageIsZero()
    {
        projectile.Initialize(
            "control_segment",
            "shot_control",
            Vector2.right,
            1f,
            OSProjectilePayload.CreateControl(1f, 0.5f));

        OSRuleResult<OSProjectileHitResult> result = projectile.HitEnemy(enemy);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.Payload.Kind, Is.EqualTo(OSProjectilePayloadKind.Control));
        Assert.That(enemy.CurrentHp, Is.EqualTo(24f));
        Assert.That(enemy.MovementLockUntil, Is.GreaterThan(0f));
    }

    [Test]
    public void ControlProjectile_WithDamageAppliesLockAndDamage()
    {
        projectile.Initialize(
            "control_segment",
            "shot_control_damage",
            Vector2.right,
            1f,
            OSProjectilePayload.CreateControl(1f, 0.5f, 3f));

        OSRuleResult<OSProjectileHitResult> result = projectile.HitEnemy(enemy);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(enemy.CurrentHp, Is.EqualTo(21f));
        Assert.That(enemy.MovementLockUntil, Is.GreaterThan(0f));
    }

    [Test]
    public void ReturnToPool_IsOneShotAndBlocksFurtherTicks()
    {
        int returnCount = 0;
        projectile.ProjectileReturned += _ => returnCount++;
        projectile.Initialize(
            "player_head",
            "shot_return",
            Vector2.right,
            1f,
            OSProjectilePayload.CreateDamage(5f));

        OSRuleResult<OSProjectileReturnResult> first = projectile.ReturnToPool("manual");
        OSRuleResult<OSProjectileReturnResult> second = projectile.ReturnToPool("manual_again");
        OSRuleResult<OSProjectileSnapshot> tickAfterReturn = projectile.Tick(0.1f);

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(second.Code, Is.EqualTo(OSResultCode.Duplicate));
        Assert.That(tickAfterReturn.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(returnCount, Is.EqualTo(1));
    }

    [Test]
    public void ReturnToPool_WithUnownedPoolRegistryReportsFailureAndBlocksFurtherEvents()
    {
        GameObject prefab = CreateProjectilePrefab();
        GameObject poolHost = new GameObject("PoolRegistry");
        OSPoolRegistry pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[] { new OSPoolEntry("projectile_head_basic", OSPoolCategory.Projectile, prefab, 1) },
            1,
            1);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);

        int returnCount = 0;
        projectile.ProjectileReturned += returned =>
        {
            returnCount++;
            Assert.That(returned.PoolReturnCode, Is.EqualTo(OSResultCode.RejectedState));
        };
        projectile.ConfigureForTests("projectile_head_basic", pool);
        projectile.Initialize(
            "player_head",
            "shot_unowned",
            Vector2.right,
            1f,
            OSProjectilePayload.CreateDamage(5f),
            pool);

        OSRuleResult<OSProjectileReturnResult> result = projectile.ReturnToPool("unowned");
        OSRuleResult<OSProjectileHitResult> hitAfterFailedPoolReturn = projectile.HitEnemy(enemy);
        OSRuleResult<OSProjectileReturnResult> secondReturn = projectile.ReturnToPool("again");

        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(projectile.HasReturned, Is.True);
        Assert.That(projectile.gameObject.activeSelf, Is.False);
        Assert.That(hitAfterFailedPoolReturn.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(secondReturn.Code, Is.EqualTo(OSResultCode.Duplicate));
        Assert.That(returnCount, Is.EqualTo(1));

        Object.DestroyImmediate(poolHost);
        Object.DestroyImmediate(prefab);
    }

    [Test]
    public void Initialize_RejectsInvalidPayloadAndVelocity()
    {
        OSRuleResult<OSProjectileSnapshot> invalidVelocity = projectile.Initialize(
            "player_head",
            "shot_invalid",
            Vector2.zero,
            1f,
            OSProjectilePayload.CreateDamage(5f));
        OSRuleResult<OSProjectileSnapshot> invalidPayload = projectile.Initialize(
            "player_head",
            "shot_invalid_payload",
            Vector2.right,
            1f,
            OSProjectilePayload.CreateDamage(0f));

        Assert.That(invalidVelocity.Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(invalidPayload.Code, Is.EqualTo(OSResultCode.ConfigurationError));
    }

    private static GameObject CreateProjectilePrefab()
    {
        GameObject prefab = new GameObject("ProjectilePrefab");
        prefab.AddComponent<OSProjectile>();
        return prefab;
    }
}
#endif
