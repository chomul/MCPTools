using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class OSPlayerHealth : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private OSPlayerBalanceData playerBalance;
    [SerializeField] private OSBodyBalanceData bodyBalance;
    [SerializeField] private OSGameSessionController sessionController;

    private OSSessionRuntimeState runtimeState;
    private Func<float> timeProvider;
    private float headHitInvulnerableUntil;
    private float explosionInvulnerableUntil;
    private bool hasRuntimeState;
    private bool playerDiedRaised;

    public event Action<OSPlayerHealthSnapshot> HealthChanged;
    public event Action<OSPlayerInvulnerabilitySnapshot> InvulnerabilityChanged;
    public event Action<OSPlayerDeathResult> PlayerDied;

    public float CurrentHp => hasRuntimeState ? runtimeState.CurrentHp : 0f;
    public float MaxHp => hasRuntimeState ? runtimeState.MaxHp : 0f;
    public bool IsDead => hasRuntimeState && runtimeState.State == OSSessionState.Dead;
    public bool IsHeadHitInvulnerable => GetTime() < headHitInvulnerableUntil;
    public bool IsExplosionInvulnerable => GetTime() < explosionInvulnerableUntil;
    public bool IsHeadDamageInvulnerable => IsHeadHitInvulnerable || IsExplosionInvulnerable;
    public float HeadHitInvulnerableUntil => headHitInvulnerableUntil;
    public float ExplosionInvulnerableUntil => explosionInvulnerableUntil;

    public void ConfigureForTests(
        OSPlayerBalanceData player,
        OSBodyBalanceData body,
        OSGameSessionController session = null,
        Func<float> clock = null)
    {
        playerBalance = player;
        bodyBalance = body;
        sessionController = session;
        timeProvider = clock;
    }

    public OSRuleResult<OSPlayerHealthSnapshot> BindRuntimeState(
        OSSessionRuntimeState state,
        OSGameSessionController session = null)
    {
        if (state == null)
        {
            return OSRuleResult<OSPlayerHealthSnapshot>.Rejected(OSResultCode.ConfigurationError, "runtime_state_missing");
        }

        if (playerBalance == null ||
            bodyBalance == null ||
            playerBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            bodyBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted)
        {
            return OSRuleResult<OSPlayerHealthSnapshot>.Rejected(OSResultCode.ConfigurationError, "player_health_configuration_invalid");
        }

        runtimeState = state;
        if (session != null)
        {
            sessionController = session;
        }

        hasRuntimeState = true;
        playerDiedRaised = state.State == OSSessionState.Dead;
        headHitInvulnerableUntil = 0f;
        explosionInvulnerableUntil = 0f;

        OSPlayerHealthSnapshot snapshot = CreateSnapshot();
        HealthChanged?.Invoke(snapshot);
        InvulnerabilityChanged?.Invoke(CreateInvulnerabilitySnapshot());
        return OSRuleResult<OSPlayerHealthSnapshot>.Accept(snapshot);
    }

    public OSRuleResult<OSPlayerHealthSnapshot> TryApplyHeadHit(OSDamageEvent damageEvent)
    {
        return TryApplyHeadHit(damageEvent, false);
    }

    public OSRuleResult<OSPlayerHealthSnapshot> TryApplyHeadHit(
        OSDamageEvent damageEvent,
        bool isShieldBlocked)
    {
        OSRuleResult<int> validation = ValidateDamageEvent(damageEvent);
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSPlayerHealthSnapshot>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (isShieldBlocked)
        {
            return OSRuleResult<OSPlayerHealthSnapshot>.Rejected(OSResultCode.RejectedState, "head_hit_shield_blocked");
        }

        if (IsHeadDamageInvulnerable)
        {
            return OSRuleResult<OSPlayerHealthSnapshot>.Rejected(OSResultCode.RejectedState, "player_head_invulnerable");
        }

        OSRuleResult<float> damageResult = runtimeState.ApplyHeadDamage(damageEvent.Amount);
        if (!damageResult.IsAccepted)
        {
            return OSRuleResult<OSPlayerHealthSnapshot>.Rejected(damageResult.Code, damageResult.ReasonKey);
        }

        headHitInvulnerableUntil = GetTime() + playerBalance.InvulnerabilityDuration;
        OSPlayerHealthSnapshot snapshot = CreateSnapshot();
        HealthChanged?.Invoke(snapshot);
        InvulnerabilityChanged?.Invoke(CreateInvulnerabilitySnapshot());

        if (runtimeState.CurrentHp <= 0f)
        {
            RaisePlayerDied("head_damage");
        }

        return OSRuleResult<OSPlayerHealthSnapshot>.Accept(snapshot);
    }

    public OSRuleResult<OSPlayerInvulnerabilitySnapshot> ApplyExplosionInvulnerability()
    {
        OSRuleResult<int> validation = ValidateRuntimeState();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSPlayerInvulnerabilitySnapshot>.Rejected(validation.Code, validation.ReasonKey);
        }

        float nextUntil = GetTime() + bodyBalance.ExplosionHeadInvulnerabilityDuration;
        if (nextUntil > explosionInvulnerableUntil)
        {
            explosionInvulnerableUntil = nextUntil;
        }

        OSPlayerInvulnerabilitySnapshot snapshot = CreateInvulnerabilitySnapshot();
        InvulnerabilityChanged?.Invoke(snapshot);
        return OSRuleResult<OSPlayerInvulnerabilitySnapshot>.Accept(snapshot);
    }

    public OSRuleResult<OSPlayerHealthSnapshot> ApplyHeal(int amount)
    {
        OSRuleResult<int> validation = ValidateRuntimeState();
        if (!validation.IsAccepted)
        {
            return OSRuleResult<OSPlayerHealthSnapshot>.Rejected(validation.Code, validation.ReasonKey);
        }

        if (amount <= 0)
        {
            return OSRuleResult<OSPlayerHealthSnapshot>.Rejected(OSResultCode.ConfigurationError, "heal_amount_invalid");
        }

        OSRuleResult<float> healResult = runtimeState.ApplyHeal(amount);
        if (!healResult.IsAccepted)
        {
            return OSRuleResult<OSPlayerHealthSnapshot>.Rejected(healResult.Code, healResult.ReasonKey);
        }

        OSPlayerHealthSnapshot snapshot = CreateSnapshot();
        HealthChanged?.Invoke(snapshot);
        return OSRuleResult<OSPlayerHealthSnapshot>.Accept(snapshot);
    }

    private void OnEnable()
    {
        if (sessionController != null)
        {
            sessionController.SessionStarted += OnSessionStarted;
        }
    }

    private void OnDisable()
    {
        if (sessionController != null)
        {
            sessionController.SessionStarted -= OnSessionStarted;
        }
    }

    private void OnSessionStarted(OSSessionRuntimeState state)
    {
        BindRuntimeState(state, sessionController);
    }

    private OSRuleResult<int> ValidateDamageEvent(OSDamageEvent damageEvent)
    {
        OSRuleResult<int> runtimeValidation = ValidateRuntimeState();
        if (!runtimeValidation.IsAccepted)
        {
            return runtimeValidation;
        }

        if (!damageEvent.IsHeadDamage ||
            string.IsNullOrWhiteSpace(damageEvent.EventId) ||
            damageEvent.Amount <= 0f ||
            float.IsNaN(damageEvent.Amount) ||
            float.IsInfinity(damageEvent.Amount))
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "head_damage_event_invalid");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private OSRuleResult<int> ValidateRuntimeState()
    {
        if (!hasRuntimeState || runtimeState == null)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "runtime_state_missing");
        }

        if (runtimeState.State == OSSessionState.Dead)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.RejectedState, "session_dead");
        }

        if (playerBalance == null ||
            bodyBalance == null ||
            playerBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted ||
            bodyBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "player_health_configuration_invalid");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private void RaisePlayerDied(string reasonKey)
    {
        if (playerDiedRaised)
        {
            return;
        }

        playerDiedRaised = true;
        sessionController?.RequestDeath(reasonKey);

        OSPlayerDeathResult deathResult = new OSPlayerDeathResult(
            string.IsNullOrWhiteSpace(reasonKey) ? "dead" : reasonKey,
            CurrentHp,
            MaxHp);

        PlayerDied?.Invoke(deathResult);
    }

    private OSPlayerHealthSnapshot CreateSnapshot()
    {
        return new OSPlayerHealthSnapshot(
            CurrentHp,
            MaxHp,
            IsDead,
            IsHeadHitInvulnerable,
            IsExplosionInvulnerable);
    }

    private OSPlayerInvulnerabilitySnapshot CreateInvulnerabilitySnapshot()
    {
        return new OSPlayerInvulnerabilitySnapshot(
            headHitInvulnerableUntil,
            explosionInvulnerableUntil,
            IsHeadHitInvulnerable,
            IsExplosionInvulnerable);
    }

    private float GetTime()
    {
        return timeProvider != null ? timeProvider() : Time.time;
    }
}

public readonly struct OSPlayerHealthSnapshot
{
    public OSPlayerHealthSnapshot(
        float currentHp,
        float maxHp,
        bool isDead,
        bool isHeadHitInvulnerable,
        bool isExplosionInvulnerable)
    {
        CurrentHp = currentHp;
        MaxHp = maxHp;
        IsDead = isDead;
        IsHeadHitInvulnerable = isHeadHitInvulnerable;
        IsExplosionInvulnerable = isExplosionInvulnerable;
    }

    public float CurrentHp { get; }
    public float MaxHp { get; }
    public bool IsDead { get; }
    public bool IsHeadHitInvulnerable { get; }
    public bool IsExplosionInvulnerable { get; }
}

public readonly struct OSPlayerInvulnerabilitySnapshot
{
    public OSPlayerInvulnerabilitySnapshot(
        float headHitInvulnerableUntil,
        float explosionInvulnerableUntil,
        bool isHeadHitInvulnerable,
        bool isExplosionInvulnerable)
    {
        HeadHitInvulnerableUntil = headHitInvulnerableUntil;
        ExplosionInvulnerableUntil = explosionInvulnerableUntil;
        IsHeadHitInvulnerable = isHeadHitInvulnerable;
        IsExplosionInvulnerable = isExplosionInvulnerable;
    }

    public float HeadHitInvulnerableUntil { get; }
    public float ExplosionInvulnerableUntil { get; }
    public bool IsHeadHitInvulnerable { get; }
    public bool IsExplosionInvulnerable { get; }
}

public readonly struct OSPlayerDeathResult
{
    public OSPlayerDeathResult(string reasonKey, float currentHp, float maxHp)
    {
        ReasonKey = reasonKey ?? string.Empty;
        CurrentHp = currentHp;
        MaxHp = maxHp;
    }

    public string ReasonKey { get; }
    public float CurrentHp { get; }
    public float MaxHp { get; }
}
