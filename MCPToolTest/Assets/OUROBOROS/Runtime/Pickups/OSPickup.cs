using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSPickup : MonoBehaviour
{
    [Header("Pickup")]
    [SerializeField] private string pickupId = "pickup_experience";
    [SerializeField] private OSPickupType pickupType = OSPickupType.Experience;
    [SerializeField] private int amount = 1;

    [Header("References")]
    [SerializeField] private OSGameSessionController sessionController;
    [SerializeField] private OSPoolRegistry poolRegistry;

    private bool isInitialized;
    private bool isCollected;
    private bool hasReturned;

    public event Action<OSPickupCollectResult> PickupCollected;

    public string PickupId => pickupId;
    public OSPickupType PickupType => pickupType;
    public int Amount => amount;
    public bool IsInitialized => isInitialized;
    public bool IsCollected => isCollected;
    public bool HasReturned => hasReturned;

    public void ConfigureForTests(OSGameSessionController session = null, OSPoolRegistry pool = null)
    {
        sessionController = session;
        poolRegistry = pool;
    }

    public OSRuleResult<OSPickupSnapshot> Initialize(
        string runtimePickupId,
        OSPickupType type,
        int pickupAmount,
        OSGameSessionController session = null,
        OSPoolRegistry pool = null)
    {
        if (string.IsNullOrWhiteSpace(runtimePickupId) ||
            pickupAmount <= 0 ||
            !IsValidPickupType(type))
        {
            return OSRuleResult<OSPickupSnapshot>.Rejected(OSResultCode.ConfigurationError, "pickup_initialize_invalid");
        }

        pickupId = runtimePickupId;
        pickupType = type;
        amount = pickupAmount;
        sessionController = session ?? sessionController;
        poolRegistry = pool ?? poolRegistry;
        if (sessionController == null)
        {
            return OSRuleResult<OSPickupSnapshot>.Rejected(OSResultCode.ConfigurationError, "pickup_session_missing");
        }

        isInitialized = true;
        isCollected = false;
        hasReturned = false;
        gameObject.SetActive(true);

        return OSRuleResult<OSPickupSnapshot>.Accept(CreateSnapshot());
    }

    public OSRuleResult<OSPickupCollectResult> TryCollect(GameObject collector)
    {
        OSRuleResult<int> validation = ValidateCollection(collector);
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSPickupCollectResult>.Rejected(validation.Code, validation.ReasonKey);
        }

        string eventId = pickupId;
        OSPickupEvent pickupEvent = new OSPickupEvent(eventId, pickupType, amount, pickupId);
        OSRuleResult<OSCombatEvent> enqueueResult = sessionController.EnqueuePickupEvent(pickupEvent);
        if (!enqueueResult.IsAccepted)
        {
            return OSRuleResult<OSPickupCollectResult>.Rejected(enqueueResult.Code, enqueueResult.ReasonKey);
        }

        isCollected = true;
        OSResultCode poolReturnCode = ReturnToPool("collected").Code;
        OSPickupCollectResult result = new OSPickupCollectResult(
            pickupId,
            eventId,
            pickupType,
            amount,
            poolReturnCode);

        PickupCollected?.Invoke(result);
        return OSRuleResult<OSPickupCollectResult>.Accept(result);
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other != null)
        {
            TryCollect(other.gameObject);
        }
    }

    private OSRuleResult<int> ValidateCollection(GameObject collector)
    {
        if (!isInitialized)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "pickup_not_initialized");
        }

        if (isCollected)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.Duplicate, "pickup_already_collected");
        }

        if (sessionController == null)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "pickup_session_missing");
        }

        if (collector == null || !IsHeadCollector(collector))
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "pickup_collector_not_head");
        }

        return OSRuleResult<int>.Accept(1);
    }

    public OSRuleResult<OSPickupReturnResult> ReturnToPool(string reasonKey = "returned")
    {
        if (hasReturned)
        {
            return OSRuleResult<OSPickupReturnResult>.Rejected(OSResultCode.Duplicate, "pickup_return_duplicate");
        }

        if (!isInitialized)
        {
            return OSRuleResult<OSPickupReturnResult>.Rejected(OSResultCode.RejectedState, "pickup_not_initialized");
        }

        OSResultCode poolReturnCode = OSResultCode.Accepted;
        if (poolRegistry == null)
        {
            gameObject.SetActive(false);
        }
        else
        {
            OSRuleResult<GameObject> returnResult = poolRegistry.Return(gameObject);
            poolReturnCode = returnResult.Code;
            if (!returnResult.IsAccepted)
            {
                gameObject.SetActive(false);
            }
        }

        hasReturned = true;
        OSPickupReturnResult result = new OSPickupReturnResult(
            pickupId,
            string.IsNullOrWhiteSpace(reasonKey) ? "returned" : reasonKey,
            poolReturnCode);

        if (poolReturnCode != OSResultCode.Accepted)
        {
            return OSRuleResult<OSPickupReturnResult>.Rejected(poolReturnCode, "pickup_pool_return_failed");
        }

        return OSRuleResult<OSPickupReturnResult>.Accept(result);
    }

    private OSPickupSnapshot CreateSnapshot()
    {
        return new OSPickupSnapshot(pickupId, pickupType, amount, isCollected);
    }

    private static bool IsHeadCollector(GameObject collector)
    {
        return collector.GetComponentInParent<OSPlayerController>() != null ||
            collector.GetComponentInParent<OSPlayerHealth>() != null;
    }

    private static bool IsValidPickupType(OSPickupType type)
    {
        switch (type)
        {
            case OSPickupType.Experience:
            case OSPickupType.BodyFragment:
            case OSPickupType.Heal:
                return true;
            default:
                return false;
        }
    }
}

public readonly struct OSPickupSnapshot
{
    public OSPickupSnapshot(string pickupId, OSPickupType pickupType, int amount, bool isCollected)
    {
        PickupId = pickupId ?? string.Empty;
        PickupType = pickupType;
        Amount = amount;
        IsCollected = isCollected;
    }

    public string PickupId { get; }
    public OSPickupType PickupType { get; }
    public int Amount { get; }
    public bool IsCollected { get; }
}

public readonly struct OSPickupCollectResult
{
    public OSPickupCollectResult(
        string pickupId,
        string eventId,
        OSPickupType pickupType,
        int amount,
        OSResultCode poolReturnCode)
    {
        PickupId = pickupId ?? string.Empty;
        EventId = eventId ?? string.Empty;
        PickupType = pickupType;
        Amount = amount;
        PoolReturnCode = poolReturnCode;
    }

    public string PickupId { get; }
    public string EventId { get; }
    public OSPickupType PickupType { get; }
    public int Amount { get; }
    public OSResultCode PoolReturnCode { get; }
}

public readonly struct OSPickupReturnResult
{
    public OSPickupReturnResult(string pickupId, string reasonKey, OSResultCode poolReturnCode)
    {
        PickupId = pickupId ?? string.Empty;
        ReasonKey = reasonKey ?? string.Empty;
        PoolReturnCode = poolReturnCode;
    }

    public string PickupId { get; }
    public string ReasonKey { get; }
    public OSResultCode PoolReturnCode { get; }
}
