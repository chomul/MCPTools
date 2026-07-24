#if UNITY_EDITOR
using NUnit.Framework;

public sealed class OSSelectionQueueTests
{
    [Test]
    public void BodyAndLevelUpRequests_AreQueuedSeparately()
    {
        OSSelectionQueue queue = new OSSelectionQueue();

        OSRuleResult<OSSelectionRequest> body = queue.EnqueueBody("body_01");
        OSRuleResult<OSSelectionRequest> levelUp = queue.EnqueueLevelUp(
            "level_01",
            "head_damage_boost",
            "body_fragment_discount",
            "max_hp_boost");

        Assert.That(body.IsAccepted, Is.True);
        Assert.That(levelUp.IsAccepted, Is.True);
        Assert.That(queue.PendingBodyCount, Is.EqualTo(1));
        Assert.That(queue.PendingLevelUpCount, Is.EqualTo(1));
        Assert.That(queue.PendingCount, Is.EqualTo(2));
    }

    [Test]
    public void BodyRequestsOpenBeforeLevelUpRequests()
    {
        OSSelectionQueue queue = new OSSelectionQueue();
        queue.EnqueueLevelUp("level_01", "upgrade_a", "upgrade_b", "upgrade_c");
        queue.EnqueueBody("body_01");

        Assert.That(queue.TryOpenNext(out OSSelectionRequest first), Is.True);

        Assert.That(first.Kind, Is.EqualTo(OSSelectionKind.Body));
        Assert.That(first.RequestId, Is.EqualTo("body_01"));
        Assert.That(queue.CompleteCurrent("body_01", 0).IsAccepted, Is.True);

        Assert.That(queue.TryOpenNext(out OSSelectionRequest second), Is.True);

        Assert.That(second.Kind, Is.EqualTo(OSSelectionKind.LevelUp));
        Assert.That(second.RequestId, Is.EqualTo("level_01"));
    }

    [Test]
    public void TwoBodyAndTwoLevelUpRequests_OpenInRequiredOrder()
    {
        OSSelectionQueue queue = new OSSelectionQueue();
        string order = string.Empty;

        queue.EnqueueLevelUp("level_01", "upgrade_a", "upgrade_b", "upgrade_c");
        queue.EnqueueBody("body_01");
        queue.EnqueueLevelUp("level_02", "upgrade_d", "upgrade_e", "upgrade_f");
        queue.EnqueueBody("body_02");

        while (queue.TryOpenNext(out OSSelectionRequest request))
        {
            if (order.Length > 0)
            {
                order += ",";
            }

            order += request.Kind == OSSelectionKind.Body ? "Body" : "LevelUp";
            Assert.That(queue.CompleteCurrent(request.RequestId, 0).IsAccepted, Is.True);
        }

        Assert.That(order, Is.EqualTo("Body,Body,LevelUp,LevelUp"));
    }

    [Test]
    public void StaleRequestId_IsRejectedAsDuplicate()
    {
        OSSelectionQueue queue = new OSSelectionQueue();

        queue.EnqueueBody("body_01");
        queue.EnqueueBody("body_02");
        Assert.That(queue.TryOpenNext(out OSSelectionRequest first), Is.True);
        Assert.That(queue.CompleteCurrent(first.RequestId, 0).IsAccepted, Is.True);
        Assert.That(queue.TryOpenNext(out OSSelectionRequest second), Is.True);

        OSRuleResult<OSSelectionRequest> stale = queue.CompleteCurrent(first.RequestId, 1);

        Assert.That(second.RequestId, Is.EqualTo("body_02"));
        Assert.That(stale.Code, Is.EqualTo(OSResultCode.Duplicate));
        Assert.That(queue.HasCurrentRequest, Is.True);
        Assert.That(queue.CompleteCurrent(second.RequestId, 1).IsAccepted, Is.True);
    }

    [Test]
    public void CurrentRequestIdAndOptionIndex_AreValidated()
    {
        OSSelectionQueue queue = new OSSelectionQueue();
        queue.EnqueueLevelUp("level_01", "upgrade_a", "upgrade_b", "upgrade_c");

        Assert.That(queue.TryOpenNext(out OSSelectionRequest request), Is.True);

        OSRuleResult<OSSelectionRequest> wrongId = queue.CompleteCurrent("missing", 1);
        OSRuleResult<OSSelectionRequest> wrongOption = queue.CompleteCurrent(request.RequestId, 3);

        Assert.That(wrongId.Code, Is.EqualTo(OSResultCode.Duplicate));
        Assert.That(wrongOption.Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(queue.CompleteCurrent(request.RequestId, 2).IsAccepted, Is.True);
    }

    [Test]
    public void IncludedSelectionTypes_AreUsable()
    {
        OSSelectionQueue queue = new OSSelectionQueue();
        OSUpgradeDefinitionSnapshot[] candidates =
        {
            CreateUpgrade("head_damage_boost"),
            CreateUpgrade("body_fragment_discount"),
            CreateUpgrade("max_hp_boost")
        };

        queue.EnqueueBody("body_01");
        queue.EnqueueLevelUp("level_01", candidates);

        Assert.That(queue.TryOpenNext(out OSSelectionRequest body), Is.True);
        Assert.That(body.OptionCount, Is.EqualTo(OSSelectionRequest.BodyOptionCount));
        Assert.That(body.GetBodyRoleOption(0), Is.EqualTo(OSBodyRoleType.Shield));
        Assert.That(body.GetBodyRoleOption(1), Is.EqualTo(OSBodyRoleType.Attack));
        Assert.That(body.GetBodyRoleOption(2), Is.EqualTo(OSBodyRoleType.Laser));
        Assert.That(body.GetBodyRoleOption(3), Is.EqualTo(OSBodyRoleType.Control));
        queue.CompleteCurrent(body.RequestId, 3);

        Assert.That(queue.TryOpenNext(out OSSelectionRequest levelUp), Is.True);
        Assert.That(levelUp.OptionCount, Is.EqualTo(OSSelectionRequest.LevelUpOptionCount));
        Assert.That(levelUp.GetLevelUpOptionId(0), Is.EqualTo("head_damage_boost"));
        Assert.That(levelUp.GetLevelUpOptionId(1), Is.EqualTo("body_fragment_discount"));
        Assert.That(levelUp.GetLevelUpOptionId(2), Is.EqualTo("max_hp_boost"));
    }

    [Test]
    public void DuplicateRequestIdsAndInvalidLevelUpOptions_AreRejected()
    {
        OSSelectionQueue queue = new OSSelectionQueue();

        Assert.That(queue.EnqueueBody("body_01").IsAccepted, Is.True);
        Assert.That(queue.EnqueueBody("body_01").Code, Is.EqualTo(OSResultCode.Duplicate));
        Assert.That(queue.EnqueueBody(" ").Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(queue.EnqueueLevelUp("level_01", "upgrade_a", "upgrade_a", "upgrade_c").Code, Is.EqualTo(OSResultCode.ConfigurationError));
        Assert.That(queue.EnqueueLevelUp("level_02", "upgrade_a", string.Empty, "upgrade_c").Code, Is.EqualTo(OSResultCode.ConfigurationError));
    }

    [Test]
    public void CancelAllClearsPendingAndCurrentRequests()
    {
        OSSelectionQueue queue = new OSSelectionQueue();
        queue.EnqueueBody("body_01");
        queue.EnqueueLevelUp("level_01", "upgrade_a", "upgrade_b", "upgrade_c");

        Assert.That(queue.TryOpenNext(out OSSelectionRequest request), Is.True);

        queue.CancelAll();

        Assert.That(queue.PendingBodyCount, Is.EqualTo(0));
        Assert.That(queue.PendingLevelUpCount, Is.EqualTo(0));
        Assert.That(queue.HasCurrentRequest, Is.False);
        Assert.That(queue.TryOpenNext(out _), Is.False);
        Assert.That(queue.CompleteCurrent(request.RequestId, 0).Code, Is.EqualTo(OSResultCode.RejectedState));
    }

    private static OSUpgradeDefinitionSnapshot CreateUpgrade(string id)
    {
        return new OSUpgradeDefinitionSnapshot(
            id,
            OSUpgradeFamily.Firepower,
            OSUpgradeOperation.HeadDamageMultiplier,
            1,
            0.1f,
            true,
            1);
    }
}
#endif
