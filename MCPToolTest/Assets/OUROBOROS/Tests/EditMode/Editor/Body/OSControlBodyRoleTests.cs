#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSControlBodyRoleTests
{
    private GameObject roleHost;
    private OSControlBodyRole controlRole;
    private OSBodyBalanceData bodyBalance;
    private GameObject bodyChainHost;
    private OSBodyChain bodyChain;
    private GameObject poolHost;
    private OSPoolRegistry pool;
    private GameObject projectilePrefab;
    private GameObject[] enemyHosts;
    private OSControlBodyShotResult[] firedShots;
    private float currentTime;

    [SetUp]
    public void SetUp()
    {
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();

        bodyChainHost = new GameObject("BodyChain");
        bodyChain = bodyChainHost.AddComponent<OSBodyChain>();
        bodyChain.ConfigureForTests(bodyBalance);
        bodyChain.RecordHeadPosition(Vector2.zero, Vector2.right);

        projectilePrefab = CreateProjectilePrefab();
        poolHost = new GameObject("PoolRegistry");
        pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[] { new OSPoolEntry("projectile_control", OSPoolCategory.Projectile, projectilePrefab, 8) },
            8,
            8);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);

        roleHost = new GameObject("ControlBodyRole");
        controlRole = roleHost.AddComponent<OSControlBodyRole>();
        controlRole.ConfigureForTests(bodyBalance, bodyChain, pool);
        controlRole.SetCombatEnabledForTests(true);
        enemyHosts = new GameObject[0];
        firedShots = new OSControlBodyShotResult[0];
        currentTime = 10f;
        controlRole.ControlShotFired += AddFiredShot;
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

        Object.DestroyImmediate(roleHost);
        Object.DestroyImmediate(poolHost);
        Object.DestroyImmediate(projectilePrefab);
        Object.DestroyImmediate(bodyChainHost);
        Object.DestroyImmediate(bodyBalance);
    }

    [Test]
    public void ChainChanges_RegisterAndUnregisterOnlyControlSegments()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Control);
        bodyChain.AppendSegment(OSBodyRoleType.Attack);
        bodyChain.AppendSegment(OSBodyRoleType.Control);

        Assert.That(controlRole.RegisteredControlCount, Is.EqualTo(2));

        bodyChain.TryCutFrom(2);

        Assert.That(controlRole.RegisteredControlCount, Is.EqualTo(1));
    }

    [Test]
    public void Tick_NoEnemyStillFiresBothSideControlProjectiles()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Control);

        OSRuleResult<OSControlBodyTickResult> result = controlRole.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_control");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.DidFire, Is.True);
        Assert.That(result.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(2));
        Assert.That(firedShots[0].TargetRuntimeId, Is.EqualTo(string.Empty));
        Assert.That(firedShots[1].TargetRuntimeId, Is.EqualTo(string.Empty));
        Assert.That(Mathf.Abs(firedShots[0].Direction.y), Is.EqualTo(1f).Within(0.0001f));
        Assert.That(Mathf.Abs(firedShots[1].Direction.y), Is.EqualTo(1f).Within(0.0001f));
        Assert.That(firedShots[0].Direction.y, Is.Not.EqualTo(firedShots[1].Direction.y).Within(0.0001f));
    }

    [Test]
    public void Tick_TargetsClosestEnemyOnEachSideOnly()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Control);
        OSEnemyController closer = CreateEnemy("enemy_close", OSEnemyClass.Normal, new Vector2(0f, 2f));
        CreateEnemy("enemy_farther", OSEnemyClass.Normal, new Vector2(0f, 4f));
        OSEnemyController lower = CreateEnemy("enemy_lower", OSEnemyClass.Normal, new Vector2(0f, -2f));
        CreateEnemy("enemy_front", OSEnemyClass.Normal, new Vector2(2f, 0f));

        OSRuleResult<OSControlBodyTickResult> result = controlRole.Tick(0f);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(CountTargetedShots(closer.RuntimeId), Is.EqualTo(1));
        Assert.That(CountTargetedShots(lower.RuntimeId), Is.EqualTo(1));
        Assert.That(CountTargetedShots("enemy_farther"), Is.EqualTo(0));
        Assert.That(CountTargetedShots("enemy_front"), Is.EqualTo(0));
    }

    [Test]
    public void Tick_OutOfRangeEnemyFallsBackToSideFireWithoutTargeting()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Control);
        CreateEnemy("enemy_far", OSEnemyClass.Normal, new Vector2(0f, 7f));

        OSRuleResult<OSControlBodyTickResult> result = controlRole.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_control");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(2));
        Assert.That(CountTargetedShots("enemy_far"), Is.EqualTo(0));
    }

    [Test]
    public void Tick_FiredPayloadHasZeroDamageAndConfiguredLockDurations()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Control);
        CreateEnemy("enemy_a", OSEnemyClass.Normal, new Vector2(0f, 2f));

        OSRuleResult<OSControlBodyTickResult> result = controlRole.Tick(0f);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(firedShots[0].Damage, Is.EqualTo(0f).Within(0.0001f));
        Assert.That(firedShots[0].NormalLockDuration, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(firedShots[0].EliteBossLockDuration, Is.EqualTo(0.5f).Within(0.0001f));
    }

    [Test]
    public void ProjectileHit_AppliesMovementLockWithoutDamage()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Control);
        OSEnemyController enemy = CreateEnemy("enemy_a", OSEnemyClass.Normal, new Vector2(0f, 2f));

        OSRuleResult<OSControlBodyTickResult> result = controlRole.Tick(0f);
        OSProjectile projectile = GetFirstActiveProjectile();
        OSRuleResult<OSProjectileHitResult> hit = projectile.HitEnemy(enemy);

        Assert.That(result.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(hit.IsAccepted, Is.True);
        Assert.That(enemy.CurrentHp, Is.EqualTo(24f).Within(0.0001f));
        Assert.That(enemy.MovementLockUntil, Is.EqualTo(11f).Within(0.0001f));
        Assert.That(pool.GetUsage("projectile_control").Payload.ActiveCount, Is.EqualTo(1));
    }

    [Test]
    public void ProjectileHit_EliteAndBossUseShorterLockDuration()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Control);
        OSEnemyController elite = CreateEnemy("enemy_elite", OSEnemyClass.Elite, new Vector2(0f, 2f));

        controlRole.Tick(0f);
        OSProjectile projectile = GetFirstActiveProjectile();
        OSRuleResult<OSProjectileHitResult> hit = projectile.HitEnemy(elite);

        Assert.That(hit.IsAccepted, Is.True);
        Assert.That(elite.CurrentHp, Is.EqualTo(24f).Within(0.0001f));
        Assert.That(elite.MovementLockUntil, Is.EqualTo(10.5f).Within(0.0001f));
    }

    [Test]
    public void Tick_RemovedControlSegmentStopsFiring()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Control);
        CreateEnemy("enemy_a", OSEnemyClass.Normal, new Vector2(0f, 2f));
        bodyChain.TryCutFrom(0);

        OSRuleResult<OSControlBodyTickResult> result = controlRole.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_control");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.RegisteredControlCount, Is.EqualTo(0));
        Assert.That(result.Payload.FiredCount, Is.EqualTo(0));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(0));
    }

    [Test]
    public void Tick_CooldownBlocksImmediateSecondShotAndAllowsAfterFourSeconds()
    {
        bodyChain.AppendSegment(OSBodyRoleType.Control);
        CreateEnemy("enemy_a", OSEnemyClass.Normal, new Vector2(0f, 2f));

        OSRuleResult<OSControlBodyTickResult> first = controlRole.Tick(0f);
        OSRuleResult<OSControlBodyTickResult> immediateSecond = controlRole.Tick(0f);
        OSRuleResult<OSControlBodyTickResult> afterCooldown = controlRole.Tick(4f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_control");

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(first.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(immediateSecond.IsAccepted, Is.True);
        Assert.That(immediateSecond.Payload.FiredCount, Is.EqualTo(0));
        Assert.That(afterCooldown.IsAccepted, Is.True);
        Assert.That(afterCooldown.Payload.FiredCount, Is.EqualTo(2));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(4));
    }

    private void AddFiredShot(OSControlBodyShotResult shot)
    {
        OSControlBodyShotResult[] next = new OSControlBodyShotResult[firedShots.Length + 1];
        for (int i = 0; i < firedShots.Length; i++)
        {
            next[i] = firedShots[i];
        }

        next[next.Length - 1] = shot;
        firedShots = next;
    }

    private int CountTargetedShots(string runtimeId)
    {
        int count = 0;
        for (int i = 0; i < firedShots.Length; i++)
        {
            if (firedShots[i].TargetRuntimeId == runtimeId)
            {
                count++;
            }
        }

        return count;
    }

    private OSProjectile GetFirstActiveProjectile()
    {
        OSProjectile[] projectiles = poolHost.GetComponentsInChildren<OSProjectile>(true);
        for (int i = 0; i < projectiles.Length; i++)
        {
            if (projectiles[i].gameObject.activeSelf)
            {
                return projectiles[i];
            }
        }

        Assert.Fail("Expected an active control projectile.");
        return null;
    }

    private OSEnemyController CreateEnemy(string runtimeId, OSEnemyClass enemyClass, Vector2 position)
    {
        GameObject host = new GameObject(runtimeId);
        host.transform.position = position;
        OSEnemyController enemy = host.AddComponent<OSEnemyController>();
        enemy.ConfigureForTests(null, null, null, () => currentTime);
        OSRuleResult<OSEnemySnapshot> result = enemy.Initialize(
            runtimeId,
            new OSEnemyPrototypeSnapshot(
                "enemy_chaser",
                enemyClass,
                "enemy_chaser",
                24f,
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

    private static GameObject CreateProjectilePrefab()
    {
        GameObject prefab = new GameObject("ControlProjectilePrefab");
        prefab.AddComponent<OSProjectile>();
        return prefab;
    }
}
#endif
