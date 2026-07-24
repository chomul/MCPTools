using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSEnemyController : MonoBehaviour
{
    private static readonly List<OSEnemyController> activeEnemies = new List<OSEnemyController>(180);

    [Header("References")]
    [SerializeField] private OSEncounterBalanceData encounterBalance;
    [SerializeField] private OSPoolRegistry poolRegistry;
    [SerializeField] private Transform target;

    [Header("Spawn")]
    [SerializeField] private string enemyId = "enemy_chaser";

    private readonly HashSet<string> damageEventIds = new HashSet<string>(StringComparer.Ordinal);
    private Rigidbody2D body;
    private OSEnemyPrototypeSnapshot prototype;
    private Func<float> timeProvider;
    private float currentHp;
    private float movementLockUntil;
    private float healDropRollOverride = -1f;
    private string runtimeId = string.Empty;
    private bool isInitialized;
    private bool isDead;
    private bool deathRaised;
    private bool returnAttempted;

    public event Action<OSEnemyDamageResult> EnemyDamaged;
    public event Action<OSEnemyDeathResult> EnemyDied;
    public event Action<OSEnemyDropResult> DropRequested;
    public event Action<OSEnemyContactResult> EnemyContactConsumed;

    public static IReadOnlyList<OSEnemyController> ActiveEnemies => activeEnemies;
    public string RuntimeId => runtimeId;
    public string EnemyId => prototype.Id;
    public OSEnemyClass EnemyClass => prototype.Class;
    public float CurrentHp => currentHp;
    public float MaxHp => prototype.MaxHp;
    public float MovementLockUntil => movementLockUntil;
    public bool IsInitialized => isInitialized;
    public bool IsDead => isDead;
    public bool IsMovementLocked => isInitialized && !isDead && GetTime() < movementLockUntil;
    public OSEnemyPrototypeSnapshot Prototype => prototype;

    public void ConfigureForTests(
        OSEncounterBalanceData encounter,
        OSPoolRegistry pool = null,
        Transform moveTarget = null,
        Func<float> clock = null)
    {
        encounterBalance = encounter;
        poolRegistry = pool;
        target = moveTarget;
        timeProvider = clock;
    }

    public void SetHealDropRollForTests(float roll)
    {
        healDropRollOverride = roll;
    }

    public OSRuleResult<OSEnemySnapshot> Initialize(string newRuntimeId)
    {
        if (encounterBalance == null ||
            encounterBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted)
        {
            return OSRuleResult<OSEnemySnapshot>.Rejected(OSResultCode.ConfigurationError, "encounter_balance_invalid");
        }

        OSEnemyPrototype selectedPrototype = encounterBalance.GetEnemyPrototype(enemyId);
        if (selectedPrototype == null)
        {
            return OSRuleResult<OSEnemySnapshot>.Rejected(OSResultCode.ConfigurationError, "enemy_id_unknown");
        }

        return Initialize(newRuntimeId, selectedPrototype.CreateSnapshot(), target, poolRegistry);
    }

    public OSRuleResult<OSEnemySnapshot> Initialize(
        string newRuntimeId,
        OSEnemyPrototypeSnapshot prototypeSnapshot,
        Transform moveTarget = null,
        OSPoolRegistry pool = null)
    {
        if (string.IsNullOrWhiteSpace(newRuntimeId) || !IsValidPrototype(prototypeSnapshot))
        {
            return OSRuleResult<OSEnemySnapshot>.Rejected(OSResultCode.ConfigurationError, "enemy_initialize_invalid");
        }

        runtimeId = newRuntimeId;
        prototype = prototypeSnapshot;
        target = moveTarget;
        poolRegistry = pool;
        currentHp = prototype.MaxHp;
        movementLockUntil = 0f;
        isInitialized = true;
        isDead = false;
        deathRaised = false;
        returnAttempted = false;
        damageEventIds.Clear();
        RegisterActiveEnemy();
        gameObject.SetActive(true);
        return OSRuleResult<OSEnemySnapshot>.Accept(CreateSnapshot());
    }

    public OSRuleResult<OSEnemyDamageResult> ApplyDamage(OSDamageEvent damageEvent)
    {
        OSRuleResult<int> validation = ValidateDamageEvent(damageEvent);
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSEnemyDamageResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (!damageEventIds.Add(damageEvent.EventId))
        {
            return OSRuleResult<OSEnemyDamageResult>.Rejected(OSResultCode.Duplicate, "enemy_damage_duplicate");
        }

        float previousHp = currentHp;
        currentHp = Mathf.Max(0f, currentHp - damageEvent.Amount);
        OSEnemyDamageResult damageResult = new OSEnemyDamageResult(
            runtimeId,
            damageEvent.EventId,
            damageEvent.Amount,
            previousHp,
            currentHp,
            currentHp <= 0f);

        EnemyDamaged?.Invoke(damageResult);

        if (currentHp <= 0f)
        {
            RaiseDeath(damageEvent.EventId);
        }

        return OSRuleResult<OSEnemyDamageResult>.Accept(damageResult);
    }

    public OSRuleResult<OSEnemyControlResult> ApplyMovementLock(float normalDuration, float eliteBossDuration)
    {
        OSRuleResult<int> validation = ValidateActiveEnemy();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSEnemyControlResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (!IsPositiveFinite(normalDuration) || !IsPositiveFinite(eliteBossDuration))
        {
            return OSRuleResult<OSEnemyControlResult>.Rejected(OSResultCode.ConfigurationError, "movement_lock_duration_invalid");
        }

        float duration = prototype.Class == OSEnemyClass.Normal ? normalDuration : eliteBossDuration;
        return ApplyMovementLock(duration);
    }

    public OSRuleResult<OSEnemyControlResult> ApplyMovementLock(float duration)
    {
        OSRuleResult<int> validation = ValidateActiveEnemy();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSEnemyControlResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (!IsPositiveFinite(duration))
        {
            return OSRuleResult<OSEnemyControlResult>.Rejected(OSResultCode.ConfigurationError, "movement_lock_duration_invalid");
        }

        float previousUntil = movementLockUntil;
        float requestedUntil = GetTime() + duration;
        if (requestedUntil > movementLockUntil)
        {
            movementLockUntil = requestedUntil;
        }

        return OSRuleResult<OSEnemyControlResult>.Accept(new OSEnemyControlResult(
            runtimeId,
            duration,
            previousUntil,
            movementLockUntil));
    }

    public OSRuleResult<OSEnemyContactResult> TryApplyContactToPlayer(GameObject playerObject)
    {
        OSRuleResult<int> validation = ValidateActiveEnemy();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSEnemyContactResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (playerObject == null)
        {
            return OSRuleResult<OSEnemyContactResult>.Rejected(OSResultCode.ConfigurationError, "player_object_missing");
        }

        OSBodySegmentCollider bodySegment = playerObject.GetComponentInParent<OSBodySegmentCollider>();
        if (bodySegment != null)
        {
            return TryApplyContactToBody(bodySegment);
        }

        OSPlayerHealth playerHealth = playerObject.GetComponentInParent<OSPlayerHealth>();
        if (playerHealth == null)
        {
            return OSRuleResult<OSEnemyContactResult>.Rejected(OSResultCode.RejectedState, "player_health_missing");
        }

        OSDamageEvent contactDamage = new OSDamageEvent(
            $"{runtimeId}:contact:{Time.frameCount}",
            OSCombatEventType.HeadDamage,
            prototype.ContactDamage,
            prototype.Id,
            "player_head");

        OSRuleResult<OSPlayerHealthSnapshot> damageResult = playerHealth.TryApplyHeadHit(contactDamage);
        OSResultCode poolReturnCode = ConsumeByContact();
        OSEnemyContactResult contactResult = new OSEnemyContactResult(
            runtimeId,
            contactDamage.EventId,
            prototype.ContactDamage,
            damageResult.Code,
            poolReturnCode);

        EnemyContactConsumed?.Invoke(contactResult);
        return OSRuleResult<OSEnemyContactResult>.Accept(contactResult);
    }

    public OSRuleResult<OSEnemyContactResult> TryApplyContactToBody(OSBodySegmentCollider bodySegment)
    {
        OSRuleResult<int> validation = ValidateActiveEnemy();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSEnemyContactResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (bodySegment == null)
        {
            return OSRuleResult<OSEnemyContactResult>.Rejected(OSResultCode.ConfigurationError, "body_segment_missing");
        }

        OSDamageEvent contactDamage = new OSDamageEvent(
            $"{runtimeId}:body_contact:{Time.frameCount}",
            OSCombatEventType.BodyDamage,
            prototype.ContactDamage,
            prototype.Id,
            $"body_segment:{bodySegment.StableId}");

        OSRuleResult<OSBodyCutResult> cutResult = bodySegment.TryApplyBodyHit(contactDamage);
        OSResultCode poolReturnCode = cutResult.IsAccepted
            ? ConsumeByContact()
            : OSResultCode.RejectedState;
        OSEnemyContactResult contactResult = new OSEnemyContactResult(
            runtimeId,
            contactDamage.EventId,
            prototype.ContactDamage,
            cutResult.Code,
            poolReturnCode);

        if (cutResult.IsAccepted)
        {
            EnemyContactConsumed?.Invoke(contactResult);
            return OSRuleResult<OSEnemyContactResult>.Accept(contactResult);
        }

        return OSRuleResult<OSEnemyContactResult>.Rejected(cutResult.Code, cutResult.ReasonKey);
    }

    public OSRuleResult<Vector2> TickMovement(float deltaTime)
    {
        OSRuleResult<int> validation = ValidateActiveEnemy();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<Vector2>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (!IsPositiveFinite(deltaTime))
        {
            return OSRuleResult<Vector2>.Rejected(OSResultCode.ConfigurationError, "enemy_delta_time_invalid");
        }

        if (target == null)
        {
            return OSRuleResult<Vector2>.Rejected(OSResultCode.RejectedState, "enemy_target_missing");
        }

        if (IsMovementLocked)
        {
            return OSRuleResult<Vector2>.Rejected(OSResultCode.RejectedState, "enemy_movement_locked");
        }

        Vector2 current = transform.position;
        Vector2 destination = target.position;
        Vector2 next = Vector2.MoveTowards(current, destination, prototype.MoveSpeed * deltaTime);

        if (body != null)
        {
            body.MovePosition(next);
        }
        else
        {
            transform.position = next;
        }

        return OSRuleResult<Vector2>.Accept(next);
    }

    private void Awake()
    {
        body = GetComponent<Rigidbody2D>();
    }

    private void FixedUpdate()
    {
        if (isInitialized && !isDead && target != null)
        {
            TickMovement(Time.fixedDeltaTime);
        }
    }

    private void OnDisable()
    {
        UnregisterActiveEnemy();
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other == null)
        {
            return;
        }

        TryApplyContactToPlayer(other.gameObject);
    }

    private OSRuleResult<int> ValidateDamageEvent(OSDamageEvent damageEvent)
    {
        OSRuleResult<int> activeValidation = ValidateActiveEnemy();
        if (!activeValidation.IsAccepted)
        {
            return activeValidation;
        }

        if (string.IsNullOrWhiteSpace(damageEvent.EventId) ||
            damageEvent.Amount <= 0f ||
            float.IsNaN(damageEvent.Amount) ||
            float.IsInfinity(damageEvent.Amount))
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "enemy_damage_event_invalid");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private OSRuleResult<int> ValidateActiveEnemy()
    {
        if (!isInitialized)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "enemy_not_initialized");
        }

        if (isDead)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "enemy_dead");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private void RaiseDeath(string killingEventId)
    {
        if (deathRaised)
        {
            return;
        }

        isDead = true;
        deathRaised = true;
        UnregisterActiveEnemy();
        OSEnemyDropResult dropResult = CreateDropResult();
        DropRequested?.Invoke(dropResult);

        OSResultCode poolReturnCode = OSResultCode.Accepted;
        if (poolRegistry != null && !returnAttempted)
        {
            returnAttempted = true;
            OSRuleResult<GameObject> returnResult = poolRegistry.Return(gameObject);
            poolReturnCode = returnResult.Code;
        }

        EnemyDied?.Invoke(new OSEnemyDeathResult(
            runtimeId,
            prototype.Id,
            killingEventId,
            dropResult,
            poolReturnCode));
    }

    private OSResultCode ConsumeByContact()
    {
        if (deathRaised)
        {
            return OSResultCode.RejectedState;
        }

        isDead = true;
        deathRaised = true;
        UnregisterActiveEnemy();

        if (poolRegistry != null && !returnAttempted)
        {
            returnAttempted = true;
            return poolRegistry.Return(gameObject).Code;
        }

        gameObject.SetActive(false);
        return OSResultCode.Accepted;
    }

    private OSEnemyDropResult CreateDropResult()
    {
        bool dropsHeal = prototype.HealDropChance > 0f && GetHealRoll() <= prototype.HealDropChance;
        return new OSEnemyDropResult(
            runtimeId,
            prototype.ExperienceDrop,
            prototype.BodyFragmentDrop,
            dropsHeal ? 1 : 0);
    }

    private void RegisterActiveEnemy()
    {
        if (!activeEnemies.Contains(this))
        {
            activeEnemies.Add(this);
        }
    }

    private void UnregisterActiveEnemy()
    {
        activeEnemies.Remove(this);
    }

    private OSEnemySnapshot CreateSnapshot()
    {
        return new OSEnemySnapshot(
            runtimeId,
            prototype.Id,
            prototype.Class,
            currentHp,
            prototype.MaxHp,
            movementLockUntil,
            isDead);
    }

    private float GetHealRoll()
    {
        if (healDropRollOverride >= 0f)
        {
            return healDropRollOverride;
        }

        return UnityEngine.Random.value;
    }

    private float GetTime()
    {
        return timeProvider != null ? timeProvider() : Time.time;
    }

    private static bool IsValidPrototype(OSEnemyPrototypeSnapshot snapshot)
    {
        return !string.IsNullOrWhiteSpace(snapshot.Id) &&
            !string.IsNullOrWhiteSpace(snapshot.PrefabKey) &&
            IsPositiveFinite(snapshot.MaxHp) &&
            IsPositiveFinite(snapshot.MoveSpeed) &&
            IsPositiveFinite(snapshot.ContactDamage) &&
            snapshot.ExperienceDrop >= 0 &&
            snapshot.BodyFragmentDrop >= 0 &&
            snapshot.HealDropChance >= 0f &&
            snapshot.HealDropChance <= 1f &&
            !float.IsNaN(snapshot.HealDropChance) &&
            !float.IsInfinity(snapshot.HealDropChance);
    }

    private static bool IsPositiveFinite(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

public readonly struct OSEnemySnapshot
{
    public OSEnemySnapshot(
        string runtimeId,
        string enemyId,
        OSEnemyClass enemyClass,
        float currentHp,
        float maxHp,
        float movementLockUntil,
        bool isDead)
    {
        RuntimeId = runtimeId ?? string.Empty;
        EnemyId = enemyId ?? string.Empty;
        Class = enemyClass;
        CurrentHp = currentHp;
        MaxHp = maxHp;
        MovementLockUntil = movementLockUntil;
        IsDead = isDead;
    }

    public string RuntimeId { get; }
    public string EnemyId { get; }
    public OSEnemyClass Class { get; }
    public float CurrentHp { get; }
    public float MaxHp { get; }
    public float MovementLockUntil { get; }
    public bool IsDead { get; }
}

public readonly struct OSEnemyDamageResult
{
    public OSEnemyDamageResult(
        string runtimeId,
        string damageEventId,
        float damageAmount,
        float previousHp,
        float currentHp,
        bool isLethal)
    {
        RuntimeId = runtimeId ?? string.Empty;
        DamageEventId = damageEventId ?? string.Empty;
        DamageAmount = damageAmount;
        PreviousHp = previousHp;
        CurrentHp = currentHp;
        IsLethal = isLethal;
    }

    public string RuntimeId { get; }
    public string DamageEventId { get; }
    public float DamageAmount { get; }
    public float PreviousHp { get; }
    public float CurrentHp { get; }
    public bool IsLethal { get; }
}

public readonly struct OSEnemyControlResult
{
    public OSEnemyControlResult(
        string runtimeId,
        float appliedDuration,
        float previousLockUntil,
        float movementLockUntil)
    {
        RuntimeId = runtimeId ?? string.Empty;
        AppliedDuration = appliedDuration;
        PreviousLockUntil = previousLockUntil;
        MovementLockUntil = movementLockUntil;
    }

    public string RuntimeId { get; }
    public float AppliedDuration { get; }
    public float PreviousLockUntil { get; }
    public float MovementLockUntil { get; }
}

public readonly struct OSEnemyDropResult
{
    public OSEnemyDropResult(
        string runtimeId,
        int experienceAmount,
        int bodyFragmentAmount,
        int healAmount)
    {
        RuntimeId = runtimeId ?? string.Empty;
        ExperienceAmount = experienceAmount;
        BodyFragmentAmount = bodyFragmentAmount;
        HealAmount = healAmount;
    }

    public string RuntimeId { get; }
    public int ExperienceAmount { get; }
    public int BodyFragmentAmount { get; }
    public int HealAmount { get; }
}

public readonly struct OSEnemyDeathResult
{
    public OSEnemyDeathResult(
        string runtimeId,
        string enemyId,
        string killingEventId,
        OSEnemyDropResult drop,
        OSResultCode poolReturnCode)
    {
        RuntimeId = runtimeId ?? string.Empty;
        EnemyId = enemyId ?? string.Empty;
        KillingEventId = killingEventId ?? string.Empty;
        Drop = drop;
        PoolReturnCode = poolReturnCode;
    }

    public string RuntimeId { get; }
    public string EnemyId { get; }
    public string KillingEventId { get; }
    public OSEnemyDropResult Drop { get; }
    public OSResultCode PoolReturnCode { get; }
}

public readonly struct OSEnemyContactResult
{
    public OSEnemyContactResult(
        string runtimeId,
        string damageEventId,
        float damageAmount,
        OSResultCode damageResultCode,
        OSResultCode poolReturnCode)
    {
        RuntimeId = runtimeId ?? string.Empty;
        DamageEventId = damageEventId ?? string.Empty;
        DamageAmount = damageAmount;
        DamageResultCode = damageResultCode;
        PoolReturnCode = poolReturnCode;
    }

    public string RuntimeId { get; }
    public string DamageEventId { get; }
    public float DamageAmount { get; }
    public OSResultCode DamageResultCode { get; }
    public OSResultCode PoolReturnCode { get; }
}
