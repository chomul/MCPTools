#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class OSPoolRegistryTests
{
    private readonly List<GameObject> cleanupObjects = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        for (int i = cleanupObjects.Count - 1; i >= 0; i--)
        {
            if (cleanupObjects[i] != null)
            {
                Object.DestroyImmediate(cleanupObjects[i]);
            }
        }

        cleanupObjects.Clear();
    }

    [Test]
    public void WarmUpRentReturnAndGetUsage_AreImplemented()
    {
        OSPoolRegistry registry = CreateRegistry(
            new OSPoolEntry("enemy_chaser", OSPoolCategory.Enemy, CreatePrefab("EnemyPrefab"), 2),
            enemyLimit: 180,
            projectileLimit: 120);

        OSRuleResult<int> warmUp = registry.WarmUp();
        OSRuleResult<GameObject> first = registry.Rent("enemy_chaser");
        OSRuleResult<OSPoolUsage> afterRent = registry.GetUsage("enemy_chaser");

        if (!warmUp.IsAccepted)
        {
            Assert.Fail($"{warmUp.Code}:{warmUp.ReasonKey}");
        }

        Assert.That(warmUp.Payload, Is.EqualTo(2));
        if (!first.IsAccepted)
        {
            Assert.Fail($"{first.Code}:{first.ReasonKey}");
        }

        Assert.That(first.Payload.activeSelf, Is.True);
        Assert.That(afterRent.Payload.ActiveCount, Is.EqualTo(1));
        Assert.That(afterRent.Payload.InactiveCount, Is.EqualTo(1));
        Assert.That(registry.ActiveEnemyCount, Is.EqualTo(1));

        OSRuleResult<GameObject> returned = registry.Return(first.Payload);
        OSRuleResult<OSPoolUsage> afterReturn = registry.GetUsage("enemy_chaser");

        if (!returned.IsAccepted)
        {
            Assert.Fail($"{returned.Code}:{returned.ReasonKey}");
        }

        Assert.That(first.Payload.activeSelf, Is.False);
        Assert.That(afterReturn.Payload.ActiveCount, Is.EqualTo(0));
        Assert.That(afterReturn.Payload.InactiveCount, Is.EqualTo(2));
        Assert.That(registry.ActiveEnemyCount, Is.EqualTo(0));
    }

    [Test]
    public void RentDoesNotInstantiateAfterWarmUpAndRejectsPoolCapacity()
    {
        OSPoolRegistry registry = CreateRegistry(
            new OSPoolEntry("projectile_head_basic", OSPoolCategory.Projectile, CreatePrefab("ProjectilePrefab"), 2),
            enemyLimit: 180,
            projectileLimit: 120);

        Assert.That(registry.WarmUp().Payload, Is.EqualTo(2));
        int childCountAfterWarmUp = registry.transform.childCount;

        Assert.That(registry.Rent("projectile_head_basic").IsAccepted, Is.True);
        Assert.That(registry.Rent("projectile_head_basic").IsAccepted, Is.True);
        OSRuleResult<GameObject> depleted = registry.Rent("projectile_head_basic");

        Assert.That(depleted.Code, Is.EqualTo(OSResultCode.RejectedCapacity));
        Assert.That(registry.transform.childCount, Is.EqualTo(childCountAfterWarmUp));
        Assert.That(registry.ActiveProjectileCount, Is.EqualTo(2));
    }

    [Test]
    public void ReturnRejectsUnownedAndDuplicateInstances()
    {
        OSPoolRegistry registry = CreateRegistry(
            new OSPoolEntry("enemy_chaser", OSPoolCategory.Enemy, CreatePrefab("EnemyPrefab"), 1),
            enemyLimit: 180,
            projectileLimit: 120);
        GameObject foreign = CreatePrefab("Foreign");

        registry.WarmUp();
        GameObject rented = registry.Rent("enemy_chaser").Payload;

        Assert.That(registry.Return(foreign).Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(registry.Return(rented).IsAccepted, Is.True);
        Assert.That(registry.Return(rented).Code, Is.EqualTo(OSResultCode.RejectedState));
    }

    [Test]
    public void EnemyAndProjectileLimitsRejectOversizedPools()
    {
        OSPoolRegistry enemyRegistry = CreateRegistry(
            new OSPoolEntry("enemy_chaser", OSPoolCategory.Enemy, CreatePrefab("EnemyPrefab"), 181),
            enemyLimit: 180,
            projectileLimit: 120);
        OSPoolRegistry projectileRegistry = CreateRegistry(
            new OSPoolEntry("projectile_head_basic", OSPoolCategory.Projectile, CreatePrefab("ProjectilePrefab"), 121),
            enemyLimit: 180,
            projectileLimit: 120);

        Assert.That(enemyRegistry.WarmUp().Code, Is.EqualTo(OSResultCode.RejectedCapacity));
        Assert.That(projectileRegistry.WarmUp().Code, Is.EqualTo(OSResultCode.RejectedCapacity));
    }

    [Test]
    public void CategoryActiveLimitRejectsAdditionalRentSafely()
    {
        OSPoolRegistry registry = CreateRegistry(
            new OSPoolEntry("enemy_chaser", OSPoolCategory.Enemy, CreatePrefab("EnemyPrefab"), 2),
            enemyLimit: 2,
            projectileLimit: 120);

        Assert.That(registry.WarmUp().IsAccepted, Is.True);
        Assert.That(registry.Rent("enemy_chaser").IsAccepted, Is.True);
        Assert.That(registry.Rent("enemy_chaser").IsAccepted, Is.True);

        OSRuleResult<GameObject> overLimit = registry.Rent("enemy_chaser");

        Assert.That(overLimit.Code, Is.EqualTo(OSResultCode.RejectedCapacity));
        Assert.That(registry.ActiveEnemyCount, Is.EqualTo(2));
    }

    [Test]
    public void InvalidConfigurationAndUnknownKeysAreRejected()
    {
        OSPoolRegistry invalid = CreateRegistry(
            new OSPoolEntry(" ", OSPoolCategory.Enemy, CreatePrefab("EnemyPrefab"), 1),
            enemyLimit: 180,
            projectileLimit: 120);
        OSPoolRegistry registry = CreateRegistry(
            new OSPoolEntry("pickup_experience", OSPoolCategory.Projectile, CreatePrefab("PickupPrefab"), 1),
            enemyLimit: 180,
            projectileLimit: 120);

        Assert.That(invalid.WarmUp().Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(registry.Rent("pickup_experience").Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(registry.WarmUp().IsAccepted, Is.True);
        Assert.That(registry.Rent("missing").Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(registry.GetUsage("missing").Code, Is.EqualTo(OSResultCode.ConfigurationError));
    }

    private OSPoolRegistry CreateRegistry(OSPoolEntry entry, int enemyLimit, int projectileLimit)
    {
        GameObject host = new GameObject("PoolRegistry");
        cleanupObjects.Add(host);
        OSPoolRegistry registry = host.AddComponent<OSPoolRegistry>();
        registry.ConfigureForTests(new[] { entry }, enemyLimit, projectileLimit);
        return registry;
    }

    private GameObject CreatePrefab(string name)
    {
        GameObject prefab = new GameObject(name);
        cleanupObjects.Add(prefab);
        return prefab;
    }
}
#endif
