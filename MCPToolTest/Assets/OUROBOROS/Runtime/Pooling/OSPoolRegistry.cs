using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class OSPoolRegistry : MonoBehaviour
{
    [SerializeField] private OSPoolEntry[] poolEntries = Array.Empty<OSPoolEntry>();
    [SerializeField] private int activeEnemyLimit = 180;
    [SerializeField] private int activeProjectileLimit = 120;
    [SerializeField] private int activePickupLimit = 120;
    [SerializeField] private int activeEffectLimit = 120;
    [SerializeField] private bool warmUpOnAwake;

    private readonly Dictionary<string, OSPoolRuntime> poolsByKey = new Dictionary<string, OSPoolRuntime>(StringComparer.Ordinal);
    private readonly Dictionary<GameObject, OSPoolRuntime> activeOwners = new Dictionary<GameObject, OSPoolRuntime>();
    private int activeEnemyCount;
    private int activeProjectileCount;
    private int activePickupCount;
    private int activeEffectCount;
    private bool isWarmedUp;

    public int PoolCount => poolsByKey.Count;
    public int ActiveEnemyCount => activeEnemyCount;
    public int ActiveProjectileCount => activeProjectileCount;
    public int ActivePickupCount => activePickupCount;
    public int ActiveEffectCount => activeEffectCount;
    public bool IsWarmedUp => isWarmedUp;
    public bool WarmUpOnAwake => warmUpOnAwake;

    public void ConfigureForTests(
        OSPoolEntry[] entries,
        int enemyLimit,
        int projectileLimit,
        int pickupLimit = 120,
        int effectLimit = 120)
    {
        poolEntries = entries ?? Array.Empty<OSPoolEntry>();
        activeEnemyLimit = enemyLimit;
        activeProjectileLimit = projectileLimit;
        activePickupLimit = pickupLimit;
        activeEffectLimit = effectLimit;
    }

    public OSRuleResult<int> WarmUp()
    {
        ClearRuntimeState(destroyObjects: true);

        if (activeEnemyLimit <= 0 ||
            activeProjectileLimit <= 0 ||
            activePickupLimit <= 0 ||
            activeEffectLimit <= 0 ||
            poolEntries == null)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "pool_limits_invalid");
        }

        int createdCount = 0;
        for (int i = 0; i < poolEntries.Length; i++)
        {
            OSPoolEntry entry = poolEntries[i];
            if (entry == null || !entry.IsValid())
            {
                ClearRuntimeState(destroyObjects: true);
                return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "pool_entry_invalid");
            }

            if (poolsByKey.ContainsKey(entry.Key))
            {
                ClearRuntimeState(destroyObjects: true);
                return OSRuleResult<int>.Rejected(OSResultCode.Duplicate, "pool_key_duplicate");
            }

            if (entry.Category == OSPoolCategory.Enemy && entry.Capacity > activeEnemyLimit)
            {
                ClearRuntimeState(destroyObjects: true);
                return OSRuleResult<int>.Rejected(OSResultCode.RejectedCapacity, "enemy_pool_capacity_exceeds_limit");
            }

            if (entry.Category == OSPoolCategory.Projectile && entry.Capacity > activeProjectileLimit)
            {
                ClearRuntimeState(destroyObjects: true);
                return OSRuleResult<int>.Rejected(OSResultCode.RejectedCapacity, "projectile_pool_capacity_exceeds_limit");
            }

            if (entry.Category == OSPoolCategory.Pickup && entry.Capacity > activePickupLimit)
            {
                ClearRuntimeState(destroyObjects: true);
                return OSRuleResult<int>.Rejected(OSResultCode.RejectedCapacity, "pickup_pool_capacity_exceeds_limit");
            }

            if (entry.Category == OSPoolCategory.Effect && entry.Capacity > activeEffectLimit)
            {
                ClearRuntimeState(destroyObjects: true);
                return OSRuleResult<int>.Rejected(OSResultCode.RejectedCapacity, "effect_pool_capacity_exceeds_limit");
            }

            OSPoolRuntime runtime = new OSPoolRuntime(entry);
            poolsByKey.Add(entry.Key, runtime);

            for (int created = 0; created < entry.Capacity; created++)
            {
                GameObject instance = Instantiate(entry.Prefab, transform);
                instance.name = $"{entry.Key}_{created:00}";
                instance.SetActive(false);
                runtime.Inactive.Enqueue(instance);
                createdCount++;
            }
        }

        isWarmedUp = true;
        return OSRuleResult<int>.Accept(createdCount);
    }

    public OSRuleResult<GameObject> Rent(string key)
    {
        if (!isWarmedUp)
        {
            return OSRuleResult<GameObject>.Rejected(OSResultCode.RejectedState, "pool_not_warmed");
        }

        if (string.IsNullOrWhiteSpace(key) || !poolsByKey.TryGetValue(key, out OSPoolRuntime runtime))
        {
            return OSRuleResult<GameObject>.Rejected(OSResultCode.ConfigurationError, "pool_key_unknown");
        }

        if (!HasCategoryCapacity(runtime.Entry.Category))
        {
            return OSRuleResult<GameObject>.Rejected(OSResultCode.RejectedCapacity, "pool_category_capacity_reached");
        }

        if (runtime.Inactive.Count == 0 || runtime.ActiveCount >= runtime.Entry.Capacity)
        {
            return OSRuleResult<GameObject>.Rejected(OSResultCode.RejectedCapacity, "pool_capacity_reached");
        }

        GameObject instance = runtime.Inactive.Dequeue();
        runtime.ActiveCount++;
        IncrementCategory(runtime.Entry.Category);
        activeOwners.Add(instance, runtime);
        instance.SetActive(true);
        return OSRuleResult<GameObject>.Accept(instance);
    }

    public OSRuleResult<GameObject> Return(GameObject instance)
    {
        if (instance == null)
        {
            return OSRuleResult<GameObject>.Rejected(OSResultCode.ConfigurationError, "pool_instance_invalid");
        }

        if (!activeOwners.TryGetValue(instance, out OSPoolRuntime runtime))
        {
            return OSRuleResult<GameObject>.Rejected(OSResultCode.RejectedState, "pool_instance_not_owned");
        }

        activeOwners.Remove(instance);
        runtime.ActiveCount--;
        DecrementCategory(runtime.Entry.Category);
        instance.transform.SetParent(transform, false);
        instance.SetActive(false);
        runtime.Inactive.Enqueue(instance);
        return OSRuleResult<GameObject>.Accept(instance);
    }

    public OSRuleResult<OSPoolUsage> GetUsage(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !poolsByKey.TryGetValue(key, out OSPoolRuntime runtime))
        {
            return OSRuleResult<OSPoolUsage>.Rejected(OSResultCode.ConfigurationError, "pool_key_unknown");
        }

        return OSRuleResult<OSPoolUsage>.Accept(new OSPoolUsage(
            runtime.Entry.Key,
            runtime.Entry.Category,
            runtime.Entry.Capacity,
            runtime.ActiveCount,
            runtime.Inactive.Count));
    }

    private void Awake()
    {
        if (warmUpOnAwake)
        {
            WarmUp();
        }
    }

    private bool HasCategoryCapacity(OSPoolCategory category)
    {
        switch (category)
        {
            case OSPoolCategory.Enemy:
                return activeEnemyCount < activeEnemyLimit;
            case OSPoolCategory.Projectile:
                return activeProjectileCount < activeProjectileLimit;
            case OSPoolCategory.Pickup:
                return activePickupCount < activePickupLimit;
            case OSPoolCategory.Effect:
                return activeEffectCount < activeEffectLimit;
            default:
                return false;
        }
    }

    private void IncrementCategory(OSPoolCategory category)
    {
        if (category == OSPoolCategory.Enemy)
        {
            activeEnemyCount++;
        }
        else if (category == OSPoolCategory.Projectile)
        {
            activeProjectileCount++;
        }
        else if (category == OSPoolCategory.Pickup)
        {
            activePickupCount++;
        }
        else if (category == OSPoolCategory.Effect)
        {
            activeEffectCount++;
        }
    }

    private void DecrementCategory(OSPoolCategory category)
    {
        if (category == OSPoolCategory.Enemy)
        {
            activeEnemyCount = Math.Max(0, activeEnemyCount - 1);
        }
        else if (category == OSPoolCategory.Projectile)
        {
            activeProjectileCount = Math.Max(0, activeProjectileCount - 1);
        }
        else if (category == OSPoolCategory.Pickup)
        {
            activePickupCount = Math.Max(0, activePickupCount - 1);
        }
        else if (category == OSPoolCategory.Effect)
        {
            activeEffectCount = Math.Max(0, activeEffectCount - 1);
        }
    }

    private void ClearRuntimeState(bool destroyObjects)
    {
        if (destroyObjects)
        {
            foreach (KeyValuePair<string, OSPoolRuntime> pair in poolsByKey)
            {
                Queue<GameObject> inactive = pair.Value.Inactive;
                while (inactive.Count > 0)
                {
                    DestroyPoolObject(inactive.Dequeue());
                }
            }

            foreach (KeyValuePair<GameObject, OSPoolRuntime> pair in activeOwners)
            {
                DestroyPoolObject(pair.Key);
            }
        }

        poolsByKey.Clear();
        activeOwners.Clear();
        activeEnemyCount = 0;
        activeProjectileCount = 0;
        activePickupCount = 0;
        activeEffectCount = 0;
        isWarmedUp = false;
    }

    private static void DestroyPoolObject(GameObject instance)
    {
        if (instance == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(instance);
        }
        else
        {
            DestroyImmediate(instance);
        }
    }

    private sealed class OSPoolRuntime
    {
        public OSPoolRuntime(OSPoolEntry entry)
        {
            Entry = entry;
            Inactive = new Queue<GameObject>(entry.Capacity);
        }

        public OSPoolEntry Entry { get; }
        public Queue<GameObject> Inactive { get; }
        public int ActiveCount { get; set; }
    }
}

[Serializable]
public sealed class OSPoolEntry
{
    [SerializeField] private string key;
    [SerializeField] private OSPoolCategory category;
    [SerializeField] private GameObject prefab;
    [SerializeField] private int capacity = 1;

    public OSPoolEntry(string key, OSPoolCategory category, GameObject prefab, int capacity)
    {
        this.key = key;
        this.category = category;
        this.prefab = prefab;
        this.capacity = capacity;
    }

    public string Key => key;
    public OSPoolCategory Category => category;
    public GameObject Prefab => prefab;
    public int Capacity => capacity;

    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(key) &&
            prefab != null &&
            capacity > 0 &&
            (category == OSPoolCategory.Enemy ||
                category == OSPoolCategory.Projectile ||
                category == OSPoolCategory.Pickup ||
                category == OSPoolCategory.Effect);
    }
}

public readonly struct OSPoolUsage
{
    public OSPoolUsage(string key, OSPoolCategory category, int capacity, int activeCount, int inactiveCount)
    {
        Key = key;
        Category = category;
        Capacity = capacity;
        ActiveCount = activeCount;
        InactiveCount = inactiveCount;
    }

    public string Key { get; }
    public OSPoolCategory Category { get; }
    public int Capacity { get; }
    public int ActiveCount { get; }
    public int InactiveCount { get; }
}
