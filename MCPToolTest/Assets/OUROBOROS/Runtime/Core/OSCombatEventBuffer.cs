using System;
using System.Collections.Generic;

public sealed class OSCombatEventBuffer
{
    private readonly List<OSCombatEvent> events = new List<OSCombatEvent>(32);
    private readonly List<int> orderedIndices = new List<int>(32);
    private readonly Dictionary<string, int> eventIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Comparison<int> indexComparison;

    public OSCombatEventBuffer()
    {
        indexComparison = CompareEventIndices;
        CurrentTick = -1;
    }

    public int CurrentTick { get; private set; }
    public int Count => events.Count;

    public void BeginTick()
    {
        BeginTick(CurrentTick + 1);
    }

    public void BeginTick(int tick)
    {
        Clear();
        CurrentTick = tick;
    }

    public OSRuleResult<OSCombatEvent> Enqueue(OSDamageEvent damageEvent)
    {
        return Enqueue(new OSCombatEvent(damageEvent));
    }

    public OSRuleResult<OSCombatEvent> Enqueue(OSPickupEvent pickupEvent)
    {
        return Enqueue(new OSCombatEvent(pickupEvent));
    }

    public OSRuleResult<OSCombatEvent> EnqueueExplosionCompleted(string eventId, string sourceId = "")
    {
        return Enqueue(OSCombatEvent.CreateExplosionCompleted(eventId, sourceId));
    }

    public OSRuleResult<OSCombatEvent> Enqueue(OSCombatEvent combatEvent)
    {
        if (!IsValidCombatEvent(combatEvent))
        {
            return OSRuleResult<OSCombatEvent>.Rejected(OSResultCode.ConfigurationError, "combat_event_invalid");
        }

        return AddOrReplace(combatEvent);
    }

    public int DrainInPriorityOrder(List<OSCombatEvent> destination)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        destination.Clear();
        orderedIndices.Clear();

        for (int i = 0; i < events.Count; i++)
        {
            orderedIndices.Add(i);
        }

        orderedIndices.Sort(indexComparison);

        for (int i = 0; i < orderedIndices.Count; i++)
        {
            destination.Add(events[orderedIndices[i]]);
        }

        int drainedCount = destination.Count;
        Clear();
        return drainedCount;
    }

    public void Clear()
    {
        events.Clear();
        orderedIndices.Clear();
        eventIndexById.Clear();
    }

    private OSRuleResult<OSCombatEvent> AddOrReplace(OSCombatEvent combatEvent)
    {
        if (eventIndexById.TryGetValue(combatEvent.EventId, out int existingIndex))
        {
            OSCombatEvent existingEvent = events[existingIndex];
            if (GetPriority(combatEvent.Type) < GetPriority(existingEvent.Type))
            {
                events[existingIndex] = combatEvent;
                return OSRuleResult<OSCombatEvent>.Accept(combatEvent);
            }

            return OSRuleResult<OSCombatEvent>.Rejected(OSResultCode.Duplicate, "event_duplicate");
        }

        eventIndexById.Add(combatEvent.EventId, events.Count);
        events.Add(combatEvent);
        return OSRuleResult<OSCombatEvent>.Accept(combatEvent);
    }

    private int CompareEventIndices(int leftIndex, int rightIndex)
    {
        OSCombatEvent left = events[leftIndex];
        OSCombatEvent right = events[rightIndex];

        int priorityComparison = GetPriority(left.Type).CompareTo(GetPriority(right.Type));
        if (priorityComparison != 0)
        {
            return priorityComparison;
        }

        return string.CompareOrdinal(left.EventId, right.EventId);
    }

    private static int GetPriority(OSCombatEventType eventType)
    {
        switch (eventType)
        {
            case OSCombatEventType.HeadDamage:
                return 0;
            case OSCombatEventType.BodyDamage:
                return 10;
            case OSCombatEventType.Pickup:
                return 20;
            case OSCombatEventType.ExplosionCompleted:
                return 30;
            default:
                return int.MaxValue;
        }
    }

    private static bool IsValidDamageEvent(OSDamageEvent damageEvent)
    {
        return !string.IsNullOrWhiteSpace(damageEvent.EventId) &&
            (damageEvent.Type == OSCombatEventType.HeadDamage || damageEvent.Type == OSCombatEventType.BodyDamage) &&
            damageEvent.Amount > 0f &&
            !float.IsNaN(damageEvent.Amount) &&
            !float.IsInfinity(damageEvent.Amount);
    }

    private static bool IsValidPickupEvent(OSPickupEvent pickupEvent)
    {
        return !string.IsNullOrWhiteSpace(pickupEvent.EventId) &&
            pickupEvent.Amount > 0 &&
            IsValidPickupType(pickupEvent.PickupType);
    }

    private static bool IsValidPickupType(OSPickupType pickupType)
    {
        switch (pickupType)
        {
            case OSPickupType.Experience:
            case OSPickupType.BodyFragment:
            case OSPickupType.Heal:
                return true;
            default:
                return false;
        }
    }

    private static bool IsValidCombatEvent(OSCombatEvent combatEvent)
    {
        if (string.IsNullOrWhiteSpace(combatEvent.EventId))
        {
            return false;
        }

        switch (combatEvent.Type)
        {
            case OSCombatEventType.HeadDamage:
            case OSCombatEventType.BodyDamage:
                return IsValidDamageEvent(combatEvent.DamageEvent);
            case OSCombatEventType.Pickup:
                return IsValidPickupEvent(combatEvent.PickupEvent);
            case OSCombatEventType.ExplosionCompleted:
                return true;
            default:
                return false;
        }
    }
}

public enum OSCombatEventType
{
    HeadDamage,
    BodyDamage,
    Pickup,
    ExplosionCompleted
}

public readonly struct OSCombatEvent
{
    public OSCombatEvent(OSDamageEvent damageEvent)
    {
        Type = damageEvent.Type;
        EventId = damageEvent.EventId;
        DamageEvent = damageEvent;
        PickupEvent = default;
        SourceId = damageEvent.SourceId;
    }

    public OSCombatEvent(OSPickupEvent pickupEvent)
    {
        Type = OSCombatEventType.Pickup;
        EventId = pickupEvent.EventId;
        DamageEvent = default;
        PickupEvent = pickupEvent;
        SourceId = pickupEvent.PickupId;
    }

    private OSCombatEvent(OSCombatEventType type, string eventId, string sourceId)
    {
        Type = type;
        EventId = eventId;
        DamageEvent = default;
        PickupEvent = default;
        SourceId = sourceId ?? string.Empty;
    }

    public OSCombatEventType Type { get; }
    public string EventId { get; }
    public OSDamageEvent DamageEvent { get; }
    public OSPickupEvent PickupEvent { get; }
    public string SourceId { get; }

    public static OSCombatEvent CreateExplosionCompleted(string eventId, string sourceId = "")
    {
        return new OSCombatEvent(OSCombatEventType.ExplosionCompleted, eventId, sourceId);
    }
}

public readonly struct OSDamageEvent
{
    public OSDamageEvent(
        string eventId,
        OSCombatEventType type,
        float amount,
        string sourceId = "",
        string targetId = "")
    {
        EventId = eventId;
        Type = type;
        Amount = amount;
        SourceId = sourceId ?? string.Empty;
        TargetId = targetId ?? string.Empty;
    }

    public OSDamageEvent(
        string eventId,
        bool isHeadDamage,
        float amount,
        string sourceId = "",
        string targetId = "")
        : this(eventId, isHeadDamage ? OSCombatEventType.HeadDamage : OSCombatEventType.BodyDamage, amount, sourceId, targetId)
    {
    }

    public string EventId { get; }
    public OSCombatEventType Type { get; }
    public float Amount { get; }
    public string SourceId { get; }
    public string TargetId { get; }
    public bool IsHeadDamage => Type == OSCombatEventType.HeadDamage;
}

public readonly struct OSPickupEvent
{
    public OSPickupEvent(
        string eventId,
        OSPickupType pickupType,
        int amount,
        string pickupId = "")
    {
        EventId = eventId;
        PickupType = pickupType;
        Amount = amount;
        PickupId = pickupId ?? string.Empty;
    }

    public string EventId { get; }
    public OSPickupType PickupType { get; }
    public int Amount { get; }
    public string PickupId { get; }
}
