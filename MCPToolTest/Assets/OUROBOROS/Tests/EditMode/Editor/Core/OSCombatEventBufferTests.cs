#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;

public sealed class OSCombatEventBufferTests
{
    [Test]
    public void BeginTickEnqueueDrainAndClear_AreImplemented()
    {
        OSCombatEventBuffer buffer = new OSCombatEventBuffer();
        List<OSCombatEvent> drained = new List<OSCombatEvent>();

        buffer.BeginTick(42);
        OSRuleResult<OSCombatEvent> damage = buffer.Enqueue(new OSDamageEvent("damage_02", OSCombatEventType.BodyDamage, 5f));
        OSRuleResult<OSCombatEvent> pickup = buffer.Enqueue(new OSPickupEvent("pickup_01", OSPickupType.Experience, 3));

        Assert.That(buffer.CurrentTick, Is.EqualTo(42));
        Assert.That(damage.IsAccepted, Is.True);
        Assert.That(pickup.IsAccepted, Is.True);
        Assert.That(buffer.Count, Is.EqualTo(2));

        int drainedCount = buffer.DrainInPriorityOrder(drained);

        Assert.That(drainedCount, Is.EqualTo(2));
        Assert.That(buffer.Count, Is.EqualTo(0));
        Assert.That(drained[0].Type, Is.EqualTo(OSCombatEventType.BodyDamage));
        Assert.That(drained[1].Type, Is.EqualTo(OSCombatEventType.Pickup));

        buffer.EnqueueExplosionCompleted("explosion_01", "bomb");
        buffer.Clear();

        Assert.That(buffer.Count, Is.EqualTo(0));
    }

    [Test]
    public void DuplicateEventId_IsConsumedOncePerTick()
    {
        OSCombatEventBuffer buffer = new OSCombatEventBuffer();
        List<OSCombatEvent> drained = new List<OSCombatEvent>();

        buffer.BeginTick();
        Assert.That(buffer.Enqueue(new OSPickupEvent("pickup_01", OSPickupType.BodyFragment, 1)).IsAccepted, Is.True);

        OSRuleResult<OSCombatEvent> duplicate = buffer.Enqueue(new OSPickupEvent("pickup_01", OSPickupType.BodyFragment, 5));

        Assert.That(duplicate.Code, Is.EqualTo(OSResultCode.Duplicate));
        Assert.That(buffer.DrainInPriorityOrder(drained), Is.EqualTo(1));
        Assert.That(drained[0].PickupEvent.Amount, Is.EqualTo(1));
        Assert.That(buffer.DrainInPriorityOrder(drained), Is.EqualTo(0));
    }

    [Test]
    public void SameAttackTouchingHeadAndBody_KeepsHeadDamageOnly()
    {
        OSCombatEventBuffer bodyFirst = new OSCombatEventBuffer();
        OSCombatEventBuffer headFirst = new OSCombatEventBuffer();
        List<OSCombatEvent> drained = new List<OSCombatEvent>();

        bodyFirst.BeginTick();
        bodyFirst.Enqueue(new OSDamageEvent("attack_01", OSCombatEventType.BodyDamage, 5f, "enemy", "player_body"));
        bodyFirst.Enqueue(new OSDamageEvent("attack_01", OSCombatEventType.HeadDamage, 8f, "enemy", "player_head"));

        Assert.That(bodyFirst.DrainInPriorityOrder(drained), Is.EqualTo(1));
        Assert.That(drained[0].Type, Is.EqualTo(OSCombatEventType.HeadDamage));
        Assert.That(drained[0].DamageEvent.TargetId, Is.EqualTo("player_head"));

        headFirst.BeginTick();
        headFirst.Enqueue(new OSDamageEvent("attack_01", OSCombatEventType.HeadDamage, 8f, "enemy", "player_head"));
        OSRuleResult<OSCombatEvent> duplicateBody = headFirst.Enqueue(
            new OSDamageEvent("attack_01", OSCombatEventType.BodyDamage, 5f, "enemy", "player_body"));

        Assert.That(duplicateBody.Code, Is.EqualTo(OSResultCode.Duplicate));
        Assert.That(headFirst.DrainInPriorityOrder(drained), Is.EqualTo(1));
        Assert.That(drained[0].Type, Is.EqualTo(OSCombatEventType.HeadDamage));
        Assert.That(drained[0].DamageEvent.TargetId, Is.EqualTo("player_head"));
    }

    [Test]
    public void DrainInPriorityOrder_IsStableRegardlessOfColliderCallOrder()
    {
        OSCombatEventBuffer first = new OSCombatEventBuffer();
        OSCombatEventBuffer second = new OSCombatEventBuffer();
        List<OSCombatEvent> firstDrain = new List<OSCombatEvent>();
        List<OSCombatEvent> secondDrain = new List<OSCombatEvent>();

        first.BeginTick();
        first.Enqueue(new OSPickupEvent("pickup_b", OSPickupType.Heal, 1));
        first.EnqueueExplosionCompleted("explosion_a");
        first.Enqueue(new OSDamageEvent("damage_b", OSCombatEventType.BodyDamage, 3f));
        first.Enqueue(new OSDamageEvent("damage_a", OSCombatEventType.HeadDamage, 7f));
        first.Enqueue(new OSPickupEvent("pickup_a", OSPickupType.Experience, 2));

        second.BeginTick();
        second.Enqueue(new OSPickupEvent("pickup_a", OSPickupType.Experience, 2));
        second.Enqueue(new OSDamageEvent("damage_a", OSCombatEventType.HeadDamage, 7f));
        second.Enqueue(new OSDamageEvent("damage_b", OSCombatEventType.BodyDamage, 3f));
        second.EnqueueExplosionCompleted("explosion_a");
        second.Enqueue(new OSPickupEvent("pickup_b", OSPickupType.Heal, 1));

        Assert.That(first.DrainInPriorityOrder(firstDrain), Is.EqualTo(5));
        Assert.That(second.DrainInPriorityOrder(secondDrain), Is.EqualTo(5));

        for (int i = 0; i < firstDrain.Count; i++)
        {
            Assert.That(secondDrain[i].Type, Is.EqualTo(firstDrain[i].Type));
            Assert.That(secondDrain[i].EventId, Is.EqualTo(firstDrain[i].EventId));
        }

        Assert.That(GetEventIds(firstDrain), Is.EqualTo("damage_a,damage_b,pickup_a,pickup_b,explosion_a"));
    }

    [Test]
    public void IncludedCombatEventTypes_AreUsable()
    {
        OSDamageEvent head = new OSDamageEvent("damage_01", true, 10f, "source", "target");
        OSDamageEvent body = new OSDamageEvent("damage_02", false, 4f);
        OSPickupEvent pickup = new OSPickupEvent("pickup_01", OSPickupType.Heal, 2, "heal_small");
        OSCombatEvent explosion = OSCombatEvent.CreateExplosionCompleted("explosion_01", "body_segment");

        Assert.That(head.Type, Is.EqualTo(OSCombatEventType.HeadDamage));
        Assert.That(head.IsHeadDamage, Is.True);
        Assert.That(body.Type, Is.EqualTo(OSCombatEventType.BodyDamage));
        Assert.That(pickup.PickupType, Is.EqualTo(OSPickupType.Heal));
        Assert.That(explosion.Type, Is.EqualTo(OSCombatEventType.ExplosionCompleted));
        Assert.That(explosion.SourceId, Is.EqualTo("body_segment"));
    }

    [Test]
    public void BeginTickAllowsSameEventIdOnNextTick()
    {
        OSCombatEventBuffer buffer = new OSCombatEventBuffer();
        List<OSCombatEvent> drained = new List<OSCombatEvent>();

        buffer.BeginTick(1);
        buffer.Enqueue(new OSPickupEvent("pickup_01", OSPickupType.Experience, 1));
        Assert.That(buffer.DrainInPriorityOrder(drained), Is.EqualTo(1));

        buffer.BeginTick(2);
        OSRuleResult<OSCombatEvent> sameIdNextTick = buffer.Enqueue(new OSPickupEvent("pickup_01", OSPickupType.Experience, 1));

        Assert.That(sameIdNextTick.IsAccepted, Is.True);
        Assert.That(buffer.DrainInPriorityOrder(drained), Is.EqualTo(1));
    }

    private static string GetEventIds(List<OSCombatEvent> events)
    {
        string result = string.Empty;
        for (int i = 0; i < events.Count; i++)
        {
            if (i > 0)
            {
                result += ",";
            }

            result += events[i].EventId;
        }

        return result;
    }
}
#endif
