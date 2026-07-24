using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSProjectile : MonoBehaviour
{
    [Header("Pool")]
    [SerializeField] private string poolKey = "projectile_head_basic";
    [SerializeField] private OSPoolRegistry poolRegistry;

    private readonly HashSet<string> hitEnemyRuntimeIds = new HashSet<string>(StringComparer.Ordinal);
    private OSProjectilePayload payload;
    private Vector2 velocity;
    private string ownerId = string.Empty;
    private string eventId = string.Empty;
    private float lifetimeSeconds;
    private float elapsedSeconds;
    private bool isInitialized;
    private bool isReturning;
    private bool hasReturned;

    public event Action<OSProjectileHitResult> ProjectileHit;
    public event Action<OSProjectileReturnResult> ProjectileReturned;

    public string PoolKey => poolKey;
    public string OwnerId => ownerId;
    public string EventId => eventId;
    public Vector2 Velocity => velocity;
    public float LifetimeSeconds => lifetimeSeconds;
    public float ElapsedSeconds => elapsedSeconds;
    public OSProjectilePayload Payload => payload;
    public bool IsInitialized => isInitialized;
    public bool HasReturned => hasReturned;

    public void ConfigureForTests(string key = "projectile_head_basic", OSPoolRegistry pool = null)
    {
        poolKey = string.IsNullOrWhiteSpace(key) ? "projectile_head_basic" : key;
        poolRegistry = pool;
    }

    public OSRuleResult<OSProjectileSnapshot> Initialize(
        string newOwnerId,
        string newEventId,
        Vector2 newVelocity,
        float newLifetimeSeconds,
        OSProjectilePayload newPayload,
        OSPoolRegistry pool = null)
    {
        return Initialize(
            newOwnerId,
            newEventId,
            transform.position,
            newVelocity,
            newLifetimeSeconds,
            newPayload,
            pool);
    }

    public OSRuleResult<OSProjectileSnapshot> Initialize(
        string newOwnerId,
        string newEventId,
        Vector2 initialPosition,
        Vector2 newVelocity,
        float newLifetimeSeconds,
        OSProjectilePayload newPayload,
        OSPoolRegistry pool = null)
    {
        if (string.IsNullOrWhiteSpace(newOwnerId) ||
            string.IsNullOrWhiteSpace(newEventId) ||
            !IsFinite(initialPosition) ||
            !IsFinite(newVelocity) ||
            newVelocity == Vector2.zero ||
            !IsPositiveFinite(newLifetimeSeconds) ||
            !newPayload.IsValid)
        {
            return OSRuleResult<OSProjectileSnapshot>.Rejected(OSResultCode.ConfigurationError, "projectile_initialize_invalid");
        }

        ownerId = newOwnerId;
        eventId = newEventId;
        velocity = newVelocity;
        lifetimeSeconds = newLifetimeSeconds;
        payload = newPayload;
        poolRegistry = pool ?? poolRegistry;
        elapsedSeconds = 0f;
        isInitialized = true;
        isReturning = false;
        hasReturned = false;
        hitEnemyRuntimeIds.Clear();
        transform.position = initialPosition;
        gameObject.SetActive(true);

        return OSRuleResult<OSProjectileSnapshot>.Accept(CreateSnapshot());
    }

    public OSRuleResult<OSProjectileSnapshot> Tick(float deltaTime)
    {
        OSRuleResult<int> validation = ValidateActiveProjectile();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSProjectileSnapshot>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (!IsPositiveFinite(deltaTime))
        {
            return OSRuleResult<OSProjectileSnapshot>.Rejected(OSResultCode.ConfigurationError, "projectile_delta_time_invalid");
        }

        transform.position = (Vector2)transform.position + velocity * deltaTime;
        elapsedSeconds += deltaTime;

        if (elapsedSeconds >= lifetimeSeconds)
        {
            ReturnToPool("lifetime_expired");
        }

        return OSRuleResult<OSProjectileSnapshot>.Accept(CreateSnapshot());
    }

    public OSRuleResult<OSProjectileHitResult> HitEnemy(OSEnemyController enemy)
    {
        OSRuleResult<int> validation = ValidateActiveProjectile();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSProjectileHitResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (enemy == null || !enemy.IsInitialized || enemy.IsDead)
        {
            return OSRuleResult<OSProjectileHitResult>.Rejected(OSResultCode.RejectedState, "projectile_target_invalid");
        }

        if (!hitEnemyRuntimeIds.Add(enemy.RuntimeId))
        {
            return OSRuleResult<OSProjectileHitResult>.Rejected(OSResultCode.Duplicate, "projectile_hit_duplicate");
        }

        OSResultCode effectCode = OSResultCode.Accepted;
        string hitEventId = $"{eventId}:{enemy.RuntimeId}";
        if (payload.Kind == OSProjectilePayloadKind.Damage)
        {
            OSRuleResult<OSEnemyDamageResult> damageResult = enemy.ApplyDamage(
                new OSDamageEvent(hitEventId, OSCombatEventType.HeadDamage, payload.Damage, ownerId, enemy.RuntimeId));
            effectCode = damageResult.Code;
        }
        else if (payload.Kind == OSProjectilePayloadKind.Control)
        {
            OSRuleResult<OSEnemyControlResult> controlResult = enemy.ApplyMovementLock(
                payload.NormalLockDuration,
                payload.EliteBossLockDuration);
            effectCode = controlResult.Code;

            if (payload.Damage > 0f && controlResult.IsAccepted)
            {
                OSRuleResult<OSEnemyDamageResult> damageResult = enemy.ApplyDamage(
                    new OSDamageEvent(hitEventId, OSCombatEventType.HeadDamage, payload.Damage, ownerId, enemy.RuntimeId));
                effectCode = damageResult.Code;
            }
        }

        if (effectCode != OSResultCode.Accepted)
        {
            return OSRuleResult<OSProjectileHitResult>.Rejected(effectCode, "projectile_effect_rejected");
        }

        OSProjectileHitResult hitResult = new OSProjectileHitResult(
            eventId,
            ownerId,
            enemy.RuntimeId,
            enemy.EnemyId,
            payload);
        ProjectileHit?.Invoke(hitResult);
        ReturnToPool("hit");
        return OSRuleResult<OSProjectileHitResult>.Accept(hitResult);
    }

    public OSRuleResult<OSProjectileReturnResult> ReturnToPool(string reasonKey = "returned")
    {
        if (hasReturned || isReturning)
        {
            return OSRuleResult<OSProjectileReturnResult>.Rejected(OSResultCode.Duplicate, "projectile_return_duplicate");
        }

        if (!isInitialized)
        {
            return OSRuleResult<OSProjectileReturnResult>.Rejected(OSResultCode.RejectedState, "projectile_not_initialized");
        }

        isReturning = true;
        OSResultCode poolReturnCode = OSResultCode.Accepted;
        if (poolRegistry != null)
        {
            OSRuleResult<GameObject> returnResult = poolRegistry.Return(gameObject);
            poolReturnCode = returnResult.Code;
        }
        else
        {
            gameObject.SetActive(false);
        }

        hasReturned = true;
        isInitialized = false;
        isReturning = false;
        if (poolRegistry != null && poolReturnCode != OSResultCode.Accepted)
        {
            gameObject.SetActive(false);
        }

        OSProjectileReturnResult result = new OSProjectileReturnResult(
            eventId,
            ownerId,
            string.IsNullOrWhiteSpace(reasonKey) ? "returned" : reasonKey,
            poolReturnCode);
        ProjectileReturned?.Invoke(result);
        if (poolReturnCode != OSResultCode.Accepted)
        {
            return OSRuleResult<OSProjectileReturnResult>.Rejected(poolReturnCode, "projectile_pool_return_failed");
        }

        return OSRuleResult<OSProjectileReturnResult>.Accept(result);
    }

    private void Update()
    {
        if (isInitialized && !hasReturned)
        {
            Tick(Time.deltaTime);
        }
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other == null)
        {
            return;
        }

        OSEnemyController enemy = other.GetComponentInParent<OSEnemyController>();
        if (enemy != null)
        {
            HitEnemy(enemy);
        }
    }

    private OSRuleResult<int> ValidateActiveProjectile()
    {
        if (!isInitialized)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "projectile_not_initialized");
        }

        if (hasReturned || isReturning)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "projectile_returning");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private OSProjectileSnapshot CreateSnapshot()
    {
        return new OSProjectileSnapshot(
            eventId,
            ownerId,
            transform.position,
            velocity,
            elapsedSeconds,
            lifetimeSeconds,
            payload,
            hasReturned);
    }

    private static bool IsFinite(Vector2 value)
    {
        return !float.IsNaN(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsInfinity(value.x) &&
            !float.IsInfinity(value.y);
    }

    private static bool IsPositiveFinite(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

public enum OSProjectilePayloadKind
{
    Damage,
    Control
}

public readonly struct OSProjectilePayload
{
    private OSProjectilePayload(
        OSProjectilePayloadKind kind,
        float damage,
        float normalLockDuration,
        float eliteBossLockDuration)
    {
        Kind = kind;
        Damage = damage;
        NormalLockDuration = normalLockDuration;
        EliteBossLockDuration = eliteBossLockDuration;
    }

    public OSProjectilePayloadKind Kind { get; }
    public float Damage { get; }
    public float NormalLockDuration { get; }
    public float EliteBossLockDuration { get; }

    public bool IsValid
    {
        get
        {
            if (Kind == OSProjectilePayloadKind.Damage)
            {
                return IsPositiveFinite(Damage);
            }

            if (Kind == OSProjectilePayloadKind.Control)
            {
                return Damage >= 0f &&
                    !float.IsNaN(Damage) &&
                    !float.IsInfinity(Damage) &&
                    IsPositiveFinite(NormalLockDuration) &&
                    IsPositiveFinite(EliteBossLockDuration);
            }

            return false;
        }
    }

    public static OSProjectilePayload CreateDamage(float damage)
    {
        return new OSProjectilePayload(OSProjectilePayloadKind.Damage, damage, 0f, 0f);
    }

    public static OSProjectilePayload CreateControl(
        float normalLockDuration,
        float eliteBossLockDuration,
        float damage = 0f)
    {
        return new OSProjectilePayload(
            OSProjectilePayloadKind.Control,
            damage,
            normalLockDuration,
            eliteBossLockDuration);
    }

    private static bool IsPositiveFinite(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

public readonly struct OSProjectileSnapshot
{
    public OSProjectileSnapshot(
        string eventId,
        string ownerId,
        Vector2 position,
        Vector2 velocity,
        float elapsedSeconds,
        float lifetimeSeconds,
        OSProjectilePayload payload,
        bool hasReturned)
    {
        EventId = eventId ?? string.Empty;
        OwnerId = ownerId ?? string.Empty;
        Position = position;
        Velocity = velocity;
        ElapsedSeconds = elapsedSeconds;
        LifetimeSeconds = lifetimeSeconds;
        Payload = payload;
        HasReturned = hasReturned;
    }

    public string EventId { get; }
    public string OwnerId { get; }
    public Vector2 Position { get; }
    public Vector2 Velocity { get; }
    public float ElapsedSeconds { get; }
    public float LifetimeSeconds { get; }
    public OSProjectilePayload Payload { get; }
    public bool HasReturned { get; }
}

public readonly struct OSProjectileHitResult
{
    public OSProjectileHitResult(
        string eventId,
        string ownerId,
        string enemyRuntimeId,
        string enemyId,
        OSProjectilePayload payload)
    {
        EventId = eventId ?? string.Empty;
        OwnerId = ownerId ?? string.Empty;
        EnemyRuntimeId = enemyRuntimeId ?? string.Empty;
        EnemyId = enemyId ?? string.Empty;
        Payload = payload;
    }

    public string EventId { get; }
    public string OwnerId { get; }
    public string EnemyRuntimeId { get; }
    public string EnemyId { get; }
    public OSProjectilePayload Payload { get; }
}

public readonly struct OSProjectileReturnResult
{
    public OSProjectileReturnResult(
        string eventId,
        string ownerId,
        string reasonKey,
        OSResultCode poolReturnCode)
    {
        EventId = eventId ?? string.Empty;
        OwnerId = ownerId ?? string.Empty;
        ReasonKey = reasonKey ?? string.Empty;
        PoolReturnCode = poolReturnCode;
    }

    public string EventId { get; }
    public string OwnerId { get; }
    public string ReasonKey { get; }
    public OSResultCode PoolReturnCode { get; }
}
