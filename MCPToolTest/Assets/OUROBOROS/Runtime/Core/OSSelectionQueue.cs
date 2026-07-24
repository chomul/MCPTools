using System;
using System.Collections.Generic;

public sealed class OSSelectionQueue
{
    private readonly Queue<OSSelectionRequest> bodyRequests = new Queue<OSSelectionRequest>(4);
    private readonly Queue<OSSelectionRequest> levelUpRequests = new Queue<OSSelectionRequest>(4);
    private OSSelectionRequest currentRequest;
    private bool hasCurrentRequest;
    private int nextRequestNumber;
    private string lastCompletedRequestId = string.Empty;

    public int PendingBodyCount => bodyRequests.Count;
    public int PendingLevelUpCount => levelUpRequests.Count;
    public int PendingCount => bodyRequests.Count + levelUpRequests.Count;
    public bool HasCurrentRequest => hasCurrentRequest;
    public OSSelectionRequest CurrentRequest => currentRequest;

    public OSRuleResult<OSSelectionRequest> EnqueueBody()
    {
        return EnqueueBody(CreateRequestId(OSSelectionKind.Body));
    }

    public OSRuleResult<OSSelectionRequest> EnqueueBody(string requestId)
    {
        if (IsBlank(requestId))
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.ConfigurationError, "selection_request_id_invalid");
        }

        if (ContainsRequestId(requestId))
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.Duplicate, "selection_request_duplicate");
        }

        OSSelectionRequest request = OSSelectionRequest.CreateBody(requestId);
        bodyRequests.Enqueue(request);
        lastCompletedRequestId = string.Empty;
        return OSRuleResult<OSSelectionRequest>.Accept(request);
    }

    public OSRuleResult<OSSelectionRequest> EnqueueLevelUp(
        string firstUpgradeId,
        string secondUpgradeId,
        string thirdUpgradeId)
    {
        return EnqueueLevelUp(
            CreateRequestId(OSSelectionKind.LevelUp),
            firstUpgradeId,
            secondUpgradeId,
            thirdUpgradeId);
    }

    public OSRuleResult<OSSelectionRequest> EnqueueLevelUp(OSUpgradeDefinitionSnapshot[] candidates)
    {
        return EnqueueLevelUp(CreateRequestId(OSSelectionKind.LevelUp), candidates);
    }

    public OSRuleResult<OSSelectionRequest> EnqueueLevelUp(
        string requestId,
        OSUpgradeDefinitionSnapshot[] candidates)
    {
        if (candidates == null || candidates.Length != OSSelectionRequest.LevelUpOptionCount)
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.ConfigurationError, "level_up_options_invalid");
        }

        return EnqueueLevelUp(requestId, candidates[0].Id, candidates[1].Id, candidates[2].Id);
    }

    public OSRuleResult<OSSelectionRequest> EnqueueLevelUp(
        string requestId,
        string firstUpgradeId,
        string secondUpgradeId,
        string thirdUpgradeId)
    {
        if (IsBlank(requestId))
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.ConfigurationError, "selection_request_id_invalid");
        }

        if (ContainsRequestId(requestId))
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.Duplicate, "selection_request_duplicate");
        }

        if (!AreValidLevelUpOptions(firstUpgradeId, secondUpgradeId, thirdUpgradeId))
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.ConfigurationError, "level_up_options_invalid");
        }

        OSSelectionRequest request = OSSelectionRequest.CreateLevelUp(
            requestId,
            firstUpgradeId,
            secondUpgradeId,
            thirdUpgradeId);

        levelUpRequests.Enqueue(request);
        lastCompletedRequestId = string.Empty;
        return OSRuleResult<OSSelectionRequest>.Accept(request);
    }

    public bool TryOpenNext(out OSSelectionRequest request)
    {
        request = default;
        if (hasCurrentRequest)
        {
            return false;
        }

        if (bodyRequests.Count > 0)
        {
            currentRequest = bodyRequests.Dequeue();
        }
        else if (levelUpRequests.Count > 0)
        {
            currentRequest = levelUpRequests.Dequeue();
        }
        else
        {
            return false;
        }

        hasCurrentRequest = true;
        request = currentRequest;
        return true;
    }

    public OSRuleResult<OSSelectionRequest> CompleteCurrent(string requestId, int selectedOptionIndex)
    {
        if (!hasCurrentRequest)
        {
            if (!IsBlank(requestId) && requestId == lastCompletedRequestId)
            {
                return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.Duplicate, "selection_request_stale");
            }

            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.RejectedState, "selection_request_missing");
        }

        if (currentRequest.RequestId != requestId)
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.Duplicate, "selection_request_stale");
        }

        if (!currentRequest.IsValidOptionIndex(selectedOptionIndex))
        {
            return OSRuleResult<OSSelectionRequest>.Rejected(OSResultCode.ConfigurationError, "selection_option_invalid");
        }

        OSSelectionRequest completedRequest = currentRequest;
        currentRequest = default;
        hasCurrentRequest = false;
        lastCompletedRequestId = completedRequest.RequestId;

        return OSRuleResult<OSSelectionRequest>.Accept(completedRequest);
    }

    public void CancelAll()
    {
        bodyRequests.Clear();
        levelUpRequests.Clear();
        currentRequest = default;
        hasCurrentRequest = false;
        lastCompletedRequestId = string.Empty;
    }

    private string CreateRequestId(OSSelectionKind kind)
    {
        nextRequestNumber++;
        return kind == OSSelectionKind.Body
            ? $"body_{nextRequestNumber}"
            : $"levelup_{nextRequestNumber}";
    }

    private bool ContainsRequestId(string requestId)
    {
        if (hasCurrentRequest && currentRequest.RequestId == requestId)
        {
            return true;
        }

        foreach (OSSelectionRequest request in bodyRequests)
        {
            if (request.RequestId == requestId)
            {
                return true;
            }
        }

        foreach (OSSelectionRequest request in levelUpRequests)
        {
            if (request.RequestId == requestId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AreValidLevelUpOptions(
        string firstUpgradeId,
        string secondUpgradeId,
        string thirdUpgradeId)
    {
        return !IsBlank(firstUpgradeId) &&
            !IsBlank(secondUpgradeId) &&
            !IsBlank(thirdUpgradeId) &&
            firstUpgradeId != secondUpgradeId &&
            firstUpgradeId != thirdUpgradeId &&
            secondUpgradeId != thirdUpgradeId;
    }

    private static bool IsBlank(string value)
    {
        return string.IsNullOrWhiteSpace(value);
    }
}

public enum OSSelectionKind
{
    Body,
    LevelUp
}

public enum OSBodyRoleType
{
    Shield,
    Attack,
    Laser,
    Control
}

public readonly struct OSSelectionRequest
{
    public const int BodyOptionCount = 4;
    public const int LevelUpOptionCount = 3;

    private OSSelectionRequest(
        string requestId,
        OSSelectionKind kind,
        string firstUpgradeId,
        string secondUpgradeId,
        string thirdUpgradeId)
    {
        RequestId = requestId;
        Kind = kind;
        FirstUpgradeId = firstUpgradeId ?? string.Empty;
        SecondUpgradeId = secondUpgradeId ?? string.Empty;
        ThirdUpgradeId = thirdUpgradeId ?? string.Empty;
    }

    public string RequestId { get; }
    public OSSelectionKind Kind { get; }
    public string FirstUpgradeId { get; }
    public string SecondUpgradeId { get; }
    public string ThirdUpgradeId { get; }
    public int OptionCount => Kind == OSSelectionKind.Body ? BodyOptionCount : LevelUpOptionCount;

    public bool IsBody => Kind == OSSelectionKind.Body;
    public bool IsLevelUp => Kind == OSSelectionKind.LevelUp;

    public OSBodyRoleType GetBodyRoleOption(int index)
    {
        if (Kind != OSSelectionKind.Body)
        {
            throw new InvalidOperationException("selection_request_not_body");
        }

        switch (index)
        {
            case 0:
                return OSBodyRoleType.Shield;
            case 1:
                return OSBodyRoleType.Attack;
            case 2:
                return OSBodyRoleType.Laser;
            case 3:
                return OSBodyRoleType.Control;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public string GetLevelUpOptionId(int index)
    {
        if (Kind != OSSelectionKind.LevelUp)
        {
            throw new InvalidOperationException("selection_request_not_level_up");
        }

        switch (index)
        {
            case 0:
                return FirstUpgradeId;
            case 1:
                return SecondUpgradeId;
            case 2:
                return ThirdUpgradeId;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public bool IsValidOptionIndex(int index)
    {
        return index >= 0 && index < OptionCount;
    }

    internal static OSSelectionRequest CreateBody(string requestId)
    {
        return new OSSelectionRequest(requestId, OSSelectionKind.Body, string.Empty, string.Empty, string.Empty);
    }

    internal static OSSelectionRequest CreateLevelUp(
        string requestId,
        string firstUpgradeId,
        string secondUpgradeId,
        string thirdUpgradeId)
    {
        return new OSSelectionRequest(requestId, OSSelectionKind.LevelUp, firstUpgradeId, secondUpgradeId, thirdUpgradeId);
    }
}
