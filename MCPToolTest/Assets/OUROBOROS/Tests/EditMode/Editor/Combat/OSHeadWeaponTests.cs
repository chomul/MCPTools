#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

public sealed class OSHeadWeaponTests
{
    private GameObject weaponHost;
    private OSHeadWeapon weapon;
    private OSPlayerBalanceData playerBalance;
    private OSBodyBalanceData bodyBalance;
    private GameObject bodyChainHost;
    private OSBodyChain bodyChain;
    private GameObject poolHost;
    private OSPoolRegistry pool;
    private GameObject projectilePrefab;
    private GameObject[] enemyHosts;

    [SetUp]
    public void SetUp()
    {
        playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();

        weaponHost = new GameObject("HeadWeapon");
        weapon = weaponHost.AddComponent<OSHeadWeapon>();

        bodyChainHost = new GameObject("BodyChain");
        bodyChain = bodyChainHost.AddComponent<OSBodyChain>();
        bodyChain.ConfigureForTests(bodyBalance);

        projectilePrefab = CreateProjectilePrefab();
        poolHost = new GameObject("PoolRegistry");
        pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[] { new OSPoolEntry("projectile_head_basic", OSPoolCategory.Projectile, projectilePrefab, 8) },
            8,
            8);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);

        weapon.ConfigureForTests(playerBalance, bodyBalance, bodyChain, pool, weaponHost.transform);
        weapon.SetCombatEnabledForTests(true);
        enemyHosts = new GameObject[0];
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

        Object.DestroyImmediate(poolHost);
        Object.DestroyImmediate(projectilePrefab);
        Object.DestroyImmediate(bodyChainHost);
        Object.DestroyImmediate(weaponHost);
        Object.DestroyImmediate(playerBalance);
        Object.DestroyImmediate(bodyBalance);
    }

    [Test]
    public void Tick_FiresAtClosestAliveEnemyInRange()
    {
        OSEnemyController farther = CreateEnemy("enemy_b", new Vector2(4f, 0f));
        OSEnemyController closer = CreateEnemy("enemy_a", new Vector2(2f, 0f));

        OSRuleResult<OSHeadShotResult> result = weapon.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_head_basic");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.DidFire, Is.True);
        Assert.That(result.Payload.TargetRuntimeId, Is.EqualTo(closer.RuntimeId));
        Assert.That(result.Payload.TargetRuntimeId, Is.Not.EqualTo(farther.RuntimeId));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(1));
    }

    [Test]
    public void SelectTarget_TieKeepsCurrentTarget()
    {
        OSEnemyController lowerId = CreateEnemy("enemy_a", new Vector2(2f, 0f));
        OSEnemyController current = CreateEnemy("enemy_b", new Vector2(-2f, 0f));
        weapon.SetCurrentTargetForTests(current);

        OSRuleResult<OSEnemyController> result = weapon.SelectTargetForTests(OSEnemyController.ActiveEnemies);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload, Is.EqualTo(current));
        Assert.That(result.Payload, Is.Not.EqualTo(lowerId));
    }

    [Test]
    public void SelectTarget_TieWithoutCurrentUsesRuntimeIdOrder()
    {
        OSEnemyController later = CreateEnemy("enemy_b", new Vector2(2f, 0f));
        OSEnemyController earlier = CreateEnemy("enemy_a", new Vector2(-2f, 0f));

        OSRuleResult<OSEnemyController> result = weapon.SelectTargetForTests(OSEnemyController.ActiveEnemies);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload, Is.EqualTo(earlier));
        Assert.That(result.Payload, Is.Not.EqualTo(later));
    }

    [Test]
    public void Tick_ActiveBodyCountScalesDamageAndAuxiliaryProjectiles()
    {
        CreateEnemy("enemy_a", new Vector2(2f, 0f));
        AppendSegments(10);

        OSRuleResult<OSHeadShotResult> result = weapon.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_head_basic");

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.BodyCount, Is.EqualTo(10));
        Assert.That(result.Payload.ProjectileCount, Is.EqualTo(3));
        Assert.That(result.Payload.Damage, Is.EqualTo(14f).Within(0.0001f));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(3));
    }

    [Test]
    public void Tick_NoTargetDoesNotConsumeCooldown()
    {
        weapon.SetCooldownForTests(0f);

        OSRuleResult<OSHeadShotResult> result = weapon.Tick(0f);

        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(result.ReasonKey, Is.EqualTo("head_weapon_target_missing"));
        Assert.That(weapon.CooldownRemaining, Is.EqualTo(0f));
    }

    [Test]
    public void Tick_PoolSaturationRejectsWithoutCooldownOrProjectileChange()
    {
        Object.DestroyImmediate(poolHost);
        poolHost = new GameObject("SmallPoolRegistry");
        pool = poolHost.AddComponent<OSPoolRegistry>();
        pool.ConfigureForTests(
            new[] { new OSPoolEntry("projectile_head_basic", OSPoolCategory.Projectile, projectilePrefab, 1) },
            1,
            1);
        Assert.That(pool.WarmUp().IsAccepted, Is.True);
        Assert.That(pool.Rent("projectile_head_basic").IsAccepted, Is.True);
        weapon.ConfigureForTests(playerBalance, bodyBalance, bodyChain, pool, weaponHost.transform);
        weapon.SetCombatEnabledForTests(true);
        CreateEnemy("enemy_a", new Vector2(2f, 0f));

        OSRuleResult<OSHeadShotResult> result = weapon.Tick(0f);
        OSRuleResult<OSPoolUsage> usage = pool.GetUsage("projectile_head_basic");

        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedCapacity));
        Assert.That(result.ReasonKey, Is.EqualTo("head_weapon_pool_capacity"));
        Assert.That(weapon.CooldownRemaining, Is.EqualTo(0f));
        Assert.That(usage.Payload.ActiveCount, Is.EqualTo(1));
    }

    [Test]
    public void Tick_BodyChangesAreReadOnNextShot()
    {
        CreateEnemy("enemy_a", new Vector2(2f, 0f));

        OSRuleResult<OSHeadShotResult> first = weapon.Tick(0f);
        weapon.SetCooldownForTests(0f);
        AppendSegments(5);
        OSRuleResult<OSHeadShotResult> second = weapon.Tick(0f);

        Assert.That(first.IsAccepted, Is.True);
        Assert.That(first.Payload.BodyCount, Is.EqualTo(0));
        Assert.That(first.Payload.ProjectileCount, Is.EqualTo(1));
        Assert.That(second.IsAccepted, Is.True);
        Assert.That(second.Payload.BodyCount, Is.EqualTo(5));
        Assert.That(second.Payload.ProjectileCount, Is.EqualTo(2));
    }

    private void AppendSegments(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.That(bodyChain.AppendSegment(OSBodyRoleType.Attack).IsAccepted, Is.True);
        }
    }

    private OSEnemyController CreateEnemy(string runtimeId, Vector2 position)
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
        GameObject prefab = new GameObject("ProjectilePrefab");
        prefab.AddComponent<OSProjectile>();
        return prefab;
    }
}
#endif
