#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class OSBodyChainTests
{
    private GameObject host;
    private OSBodyChain chain;
    private OSBodyBalanceData bodyBalance;

    [SetUp]
    public void SetUp()
    {
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        host = new GameObject("BodyChain");
        chain = host.AddComponent<OSBodyChain>();
        chain.ConfigureForTests(bodyBalance);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(host);
        Object.DestroyImmediate(bodyBalance);
    }

    [Test]
    public void EmptyChain_HasZeroSegmentsAndNoReservation()
    {
        OSBodyChainSnapshot snapshot = chain.CreateSnapshot();

        Assert.That(chain.ActiveSegmentCount, Is.EqualTo(0));
        Assert.That(snapshot.ActiveSegmentCount, Is.EqualTo(0));
        Assert.That(snapshot.Reservation.Count, Is.EqualTo(0));
    }

    [Test]
    public void AppendSegment_AddsRolesToTailAndKeepsStableOrder()
    {
        OSRuleResult<OSBodySegmentSnapshot> shield = chain.AppendSegment(OSBodyRoleType.Shield);
        OSRuleResult<OSBodySegmentSnapshot> attack = chain.AppendSegment(OSBodyRoleType.Attack);
        OSRuleResult<OSBodySegmentSnapshot> laser = chain.AppendSegment(OSBodyRoleType.Laser);
        OSRuleResult<OSBodySegmentSnapshot> control = chain.AppendSegment(OSBodyRoleType.Control);

        Assert.That(shield.IsAccepted, Is.True);
        Assert.That(attack.IsAccepted, Is.True);
        Assert.That(laser.IsAccepted, Is.True);
        Assert.That(control.IsAccepted, Is.True);
        Assert.That(chain.ActiveSegmentCount, Is.EqualTo(4));
        Assert.That(chain.GetSegmentAt(0).StableId, Is.EqualTo(1));
        Assert.That(chain.GetSegmentAt(1).StableId, Is.EqualTo(2));
        Assert.That(chain.GetSegmentAt(2).StableId, Is.EqualTo(3));
        Assert.That(chain.GetSegmentAt(3).StableId, Is.EqualTo(4));
        Assert.That(chain.GetSegmentAt(0).RoleType, Is.EqualTo(OSBodyRoleType.Shield));
        Assert.That(chain.GetSegmentAt(3).RoleType, Is.EqualTo(OSBodyRoleType.Control));
    }

    [Test]
    public void BoundaryCounts_AcceptUpToTechnicalLimitAndRejectSixtyFive()
    {
        int[] boundaries = { 0, 2, 3, 4, 5, 10, 20, 40, 63, 64 };
        int appended = 0;

        for (int i = 0; i < boundaries.Length; i++)
        {
            while (appended < boundaries[i])
            {
                OSRuleResult<OSBodySegmentSnapshot> result = chain.AppendSegment(OSBodyRoleType.Attack);
                Assert.That(result.IsAccepted, Is.True, $"append {appended + 1}");
                appended++;
            }

            Assert.That(chain.ActiveSegmentCount, Is.EqualTo(boundaries[i]));
        }

        OSRuleResult<OSBodySegmentSnapshot> overflow = chain.AppendSegment(OSBodyRoleType.Attack);

        Assert.That(overflow.Code, Is.EqualTo(OSResultCode.RejectedCapacity));
        Assert.That(chain.ActiveSegmentCount, Is.EqualTo(64));
    }

    [Test]
    public void TryCutFrom_RemovesHitIndexThroughTail()
    {
        AppendMany(5);

        OSRuleResult<OSBodyCutResult> result = chain.TryCutFrom(2);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.FirstRemovedStableId, Is.EqualTo(3));
        Assert.That(result.Payload.RemovedCount, Is.EqualTo(3));
        Assert.That(result.Payload.RemainingCount, Is.EqualTo(2));
        Assert.That(chain.ActiveSegmentCount, Is.EqualTo(2));
        Assert.That(chain.GetSegmentAt(0).StableId, Is.EqualTo(1));
        Assert.That(chain.GetSegmentAt(1).StableId, Is.EqualTo(2));
    }

    [Test]
    public void TryCutFromStableId_RemovesStableIdThroughTail()
    {
        AppendMany(5);
        int stableId = chain.GetSegmentAt(1).StableId;

        OSRuleResult<OSBodyCutResult> result = chain.TryCutFromStableId(stableId);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(result.Payload.FirstRemovedStableId, Is.EqualTo(stableId));
        Assert.That(result.Payload.RemovedCount, Is.EqualTo(4));
        Assert.That(result.Payload.RemainingCount, Is.EqualTo(1));
        Assert.That(chain.ActiveSegmentCount, Is.EqualTo(1));
    }

    [Test]
    public void AppendSegment_WithPrefabBindsBodySegmentCollider()
    {
        GameObject prefab = new GameObject("BodySegmentPrefab");
        try
        {
            chain.ConfigureForTests(bodyBalance, null, prefab);

            OSRuleResult<OSBodySegmentSnapshot> result = chain.AppendSegment(OSBodyRoleType.Control);

            Assert.That(result.IsAccepted, Is.True);
            Assert.That(host.transform.childCount, Is.EqualTo(1));
            OSBodySegmentCollider segmentCollider = host.transform.GetChild(0).GetComponent<OSBodySegmentCollider>();
            Assert.That(segmentCollider, Is.Not.Null);
            Assert.That(segmentCollider.IsBound, Is.True);
            Assert.That(segmentCollider.BodyChain, Is.SameAs(chain));
            Assert.That(segmentCollider.StableId, Is.EqualTo(result.Payload.StableId));
            Assert.That(segmentCollider.RoleType, Is.EqualTo(OSBodyRoleType.Control));
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }

    [Test]
    public void TryCutFrom_RejectsOutOfRangeWithoutChangingChain()
    {
        AppendMany(3);

        OSRuleResult<OSBodyCutResult> result = chain.TryCutFrom(3);

        Assert.That(result.Code, Is.EqualTo(OSResultCode.RejectedState));
        Assert.That(chain.ActiveSegmentCount, Is.EqualTo(3));
    }

    [Test]
    public void ReserveTailAndConsumeReservedTail_RemoveOnlyReservedStableIds()
    {
        AppendMany(5);

        OSRuleResult<OSBodyReservationSnapshot> reservation = chain.ReserveTail(3);
        OSRuleResult<OSBodyConsumeResult> consumed = chain.ConsumeReservedTail();

        Assert.That(reservation.IsAccepted, Is.True);
        Assert.That(reservation.Payload.StableIds, Is.EqualTo(new[] { 5, 4, 3 }));
        Assert.That(consumed.IsAccepted, Is.True);
        Assert.That(consumed.Payload.RemovedCount, Is.EqualTo(3));
        Assert.That(consumed.Payload.RemainingCount, Is.EqualTo(2));
        Assert.That(chain.GetSegmentAt(0).StableId, Is.EqualTo(1));
        Assert.That(chain.GetSegmentAt(1).StableId, Is.EqualTo(2));
        Assert.That(chain.ReservedTailCount, Is.EqualTo(0));
    }

    [Test]
    public void CutBeforeConsume_RemovesMissingReservationIdsBeforeExplosionConsumption()
    {
        AppendMany(5);

        chain.ReserveTail(3);
        OSRuleResult<OSBodyCutResult> cut = chain.TryCutFrom(3);
        OSRuleResult<OSBodyConsumeResult> consumed = chain.ConsumeReservedTail();

        Assert.That(cut.IsAccepted, Is.True);
        Assert.That(cut.Payload.RemovedCount, Is.EqualTo(2));
        Assert.That(consumed.IsAccepted, Is.True);
        Assert.That(consumed.Payload.RemovedCount, Is.EqualTo(1));
        Assert.That(consumed.Payload.RemainingCount, Is.EqualTo(2));
        Assert.That(chain.GetSegmentAt(0).StableId, Is.EqualTo(1));
        Assert.That(chain.GetSegmentAt(1).StableId, Is.EqualTo(2));
    }

    [Test]
    public void AppendingAfterCut_UsesNewStableId()
    {
        AppendMany(4);
        chain.TryCutFrom(2);

        OSRuleResult<OSBodySegmentSnapshot> appended = chain.AppendSegment(OSBodyRoleType.Laser);

        Assert.That(appended.IsAccepted, Is.True);
        Assert.That(appended.Payload.StableId, Is.EqualTo(5));
        Assert.That(chain.GetSegmentAt(2).StableId, Is.EqualTo(5));
    }

    [Test]
    public void RecordHeadPosition_UpdatesSegmentPositionsInHeadToTailOrder()
    {
        chain.RecordHeadPosition(Vector2.zero, Vector2.right);
        AppendMany(3);

        for (int i = 1; i <= 20; i++)
        {
            chain.RecordHeadPosition(new Vector2(i * 0.25f, 0f), Vector2.right);
        }

        OSBodySegmentSnapshot first = chain.GetSegmentAt(0);
        OSBodySegmentSnapshot second = chain.GetSegmentAt(1);
        OSBodySegmentSnapshot third = chain.GetSegmentAt(2);

        Assert.That(first.Position.x, Is.GreaterThan(second.Position.x));
        Assert.That(second.Position.x, Is.GreaterThan(third.Position.x));
        Assert.That(Mathf.Abs(first.Position.y), Is.LessThan(0.0001f));
        Assert.That(Mathf.Abs(second.Position.y), Is.LessThan(0.0001f));
    }

    [Test]
    public void Events_FireForAddRemoveAndFinalChainChange()
    {
        int added = 0;
        int removed = 0;
        int chainChanged = 0;
        chain.SegmentAdded += _ => added++;
        chain.SegmentRemoved += _ => removed++;
        chain.ChainChanged += _ => chainChanged++;

        chain.AppendSegment(OSBodyRoleType.Shield);
        chain.AppendSegment(OSBodyRoleType.Attack);
        chain.TryCutFrom(1);

        Assert.That(added, Is.EqualTo(2));
        Assert.That(removed, Is.EqualTo(1));
        Assert.That(chainChanged, Is.EqualTo(3));
    }

    private void AppendMany(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.That(chain.AppendSegment(OSBodyRoleType.Attack).IsAccepted, Is.True);
        }
    }
}
#endif
