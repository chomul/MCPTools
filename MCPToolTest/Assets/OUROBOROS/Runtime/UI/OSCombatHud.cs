using System;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class OSCombatHud : MonoBehaviour
{
    private const int FallbackBodyLimit = 64;

    [Header("References")]
    [SerializeField] private OSGameSessionController sessionController;
    [SerializeField] private OSPlayerHealth playerHealth;
    [SerializeField] private OSBodyChain bodyChain;
    [SerializeField] private OSExplosionController explosionController;
    [SerializeField] private OSWaveDirector waveDirector;

    [Header("Bars")]
    [SerializeField] private Slider hpSlider;
    [SerializeField] private Slider xpSlider;
    [SerializeField] private Slider bodySlider;

    [Header("Labels")]
    [SerializeField] private Text hpText;
    [SerializeField] private Text xpLevelText;
    [SerializeField] private Text bodyCountText;
    [SerializeField] private Text timerText;
    [SerializeField] private Text stateText;
    [SerializeField] private Text currentRoleText;
    [SerializeField] private Text explosionStatusText;
    [SerializeField] private Text bossWarningText;

    [Header("Status Images")]
    [SerializeField] private Image currentRoleImage;
    [SerializeField] private Image explosionReadyImage;
    [SerializeField] private Image bossWarningImage;

    private OSSessionRuntimeState observedRuntimeState;
    private bool isSubscribed;
    private OSWaveEvent lastWaveEvent;
    private bool hasBossWarning;

    public event Action<OSCombatHudViewModel> HudRefreshed;

    public OSCombatHudViewModel LastViewModel { get; private set; }
    public bool IsBound => sessionController != null || playerHealth != null || bodyChain != null ||
        explosionController != null || waveDirector != null;

    public void ConfigureForTests(
        OSGameSessionController session,
        OSPlayerHealth health,
        OSBodyChain chain,
        OSExplosionController explosion,
        OSWaveDirector wave,
        Text hpLabel,
        Text xpLabel,
        Text bodyLabel,
        Text timerLabel,
        Text stateLabel,
        Text roleLabel,
        Text explosionLabel,
        Text bossLabel,
        Slider hpBar = null,
        Slider xpBar = null,
        Slider bodyBar = null,
        Image roleImage = null,
        Image explosionImage = null,
        Image bossImage = null)
    {
        Unbind();

        sessionController = session;
        playerHealth = health;
        bodyChain = chain;
        explosionController = explosion;
        waveDirector = wave;
        hpText = hpLabel;
        xpLevelText = xpLabel;
        bodyCountText = bodyLabel;
        timerText = timerLabel;
        stateText = stateLabel;
        currentRoleText = roleLabel;
        explosionStatusText = explosionLabel;
        bossWarningText = bossLabel;
        hpSlider = hpBar;
        xpSlider = xpBar;
        bodySlider = bodyBar;
        currentRoleImage = roleImage;
        explosionReadyImage = explosionImage;
        bossWarningImage = bossImage;

        Subscribe();
        Refresh();
    }

    public void Bind(
        OSGameSessionController session,
        OSPlayerHealth health = null,
        OSBodyChain chain = null,
        OSExplosionController explosion = null,
        OSWaveDirector wave = null)
    {
        Unbind();

        sessionController = session;
        playerHealth = health;
        bodyChain = chain;
        explosionController = explosion;
        waveDirector = wave;

        Subscribe();
        Refresh();
    }

    public void Unbind()
    {
        Unsubscribe();
        observedRuntimeState = null;
        lastWaveEvent = default;
        hasBossWarning = false;
    }

    public void Refresh()
    {
        Refresh(CreateCurrentSnapshot());
    }

    public void Refresh(OSCombatHudSnapshot snapshot)
    {
        OSCombatHudViewModel viewModel = BuildViewModel(snapshot);
        LastViewModel = viewModel;
        ApplyViewModel(viewModel);
        HudRefreshed?.Invoke(viewModel);
    }

    public static OSCombatHudViewModel BuildViewModel(OSCombatHudSnapshot snapshot)
    {
        float hpMax = Mathf.Max(1f, snapshot.MaxHp);
        float currentHp = Mathf.Clamp(snapshot.CurrentHp, 0f, hpMax);
        int bodyLimit = Mathf.Max(1, snapshot.BodySegmentLimit);
        int bodyCount = Mathf.Max(0, snapshot.BodySegmentCount);
        int displayedBodyCount = Mathf.Min(bodyCount, bodyLimit);
        int bodyFragmentRequirement = Mathf.Max(1, snapshot.BodyFragmentsPerSegment);
        int bodyFragments = Mathf.Clamp(snapshot.BodyFragments, 0, bodyFragmentRequirement);
        int xpRequirement = Mathf.Max(1, snapshot.ExperienceToNextLevel);
        int experience = Mathf.Clamp(snapshot.Experience, 0, xpRequirement);
        bool bossWarningVisible = snapshot.BossWarningActive || snapshot.BossSpawned;
        bool explosionReady = !snapshot.IsExplosionPending && bodyCount >= snapshot.MinimumExplosionSegments;

        return new OSCombatHudViewModel(
            $"체력 {Mathf.CeilToInt(currentHp)}/{Mathf.CeilToInt(hpMax)}",
            currentHp / hpMax,
            $"레벨 {Mathf.Max(1, snapshot.Level)}  {experience}/{xpRequirement}",
            (float)experience / xpRequirement,
            bodyCount > bodyLimit
                ? $"몸통 {bodyLimit}+/{bodyLimit} | 성장 {bodyFragments}/{bodyFragmentRequirement}"
                : $"몸통 {displayedBodyCount}/{bodyLimit} | 성장 {bodyFragments}/{bodyFragmentRequirement}",
            (float)bodyFragments / bodyFragmentRequirement,
            FormatTimer(snapshot.ElapsedSeconds),
            FormatState(snapshot.SessionState),
            snapshot.HasCurrentRole ? $"역할 {FormatRole(snapshot.CurrentRole)}" : "역할 없음",
            FormatExplosion(snapshot, explosionReady),
            snapshot.BossSpawned ? "보스 등장" : bossWarningVisible ? "보스 접근" : string.Empty,
            snapshot.HasCurrentRole,
            explosionReady || snapshot.IsExplosionPending,
            bossWarningVisible);
    }

    private void Awake()
    {
        Refresh();
    }

    private void OnEnable()
    {
        Subscribe();
        Refresh();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private void Update()
    {
        Refresh();
    }

    private void Subscribe()
    {
        if (isSubscribed)
        {
            return;
        }

        if (sessionController != null)
        {
            sessionController.SessionStarted += OnSessionStarted;
            sessionController.SessionEnded += OnSessionEnded;
            sessionController.StateChanged += OnSessionStateChanged;
            sessionController.SelectionOpened += OnSelectionOpened;
            sessionController.SelectionCompleted += OnSelectionCompleted;
            ObserveRuntimeState(sessionController.RuntimeState);
        }

        if (playerHealth != null)
        {
            playerHealth.HealthChanged += OnHealthChanged;
            playerHealth.InvulnerabilityChanged += OnInvulnerabilityChanged;
        }

        if (bodyChain != null)
        {
            bodyChain.ChainChanged += OnChainChanged;
        }

        if (explosionController != null)
        {
            explosionController.ExplosionTelegraphStarted += OnExplosionTelegraphStarted;
            explosionController.ExplosionCompleted += OnExplosionCompleted;
        }

        if (waveDirector != null)
        {
            waveDirector.WaveEventRaised += OnWaveEventRaised;
        }

        isSubscribed = true;
    }

    private void Unsubscribe()
    {
        if (!isSubscribed)
        {
            return;
        }

        if (sessionController != null)
        {
            sessionController.SessionStarted -= OnSessionStarted;
            sessionController.SessionEnded -= OnSessionEnded;
            sessionController.StateChanged -= OnSessionStateChanged;
            sessionController.SelectionOpened -= OnSelectionOpened;
            sessionController.SelectionCompleted -= OnSelectionCompleted;
        }

        if (observedRuntimeState != null)
        {
            observedRuntimeState.RuntimeStateChanged -= OnRuntimeStateChanged;
            observedRuntimeState = null;
        }

        if (playerHealth != null)
        {
            playerHealth.HealthChanged -= OnHealthChanged;
            playerHealth.InvulnerabilityChanged -= OnInvulnerabilityChanged;
        }

        if (bodyChain != null)
        {
            bodyChain.ChainChanged -= OnChainChanged;
        }

        if (explosionController != null)
        {
            explosionController.ExplosionTelegraphStarted -= OnExplosionTelegraphStarted;
            explosionController.ExplosionCompleted -= OnExplosionCompleted;
        }

        if (waveDirector != null)
        {
            waveDirector.WaveEventRaised -= OnWaveEventRaised;
        }

        isSubscribed = false;
    }

    private OSCombatHudSnapshot CreateCurrentSnapshot()
    {
        OSSessionRuntimeState state = sessionController != null ? sessionController.RuntimeState : observedRuntimeState;
        OSBodyChainSnapshot chainSnapshot = bodyChain != null ? bodyChain.CreateSnapshot() : default;
        OSExplosionSnapshot explosionSnapshot = explosionController != null ? explosionController.CurrentSnapshot : default;
        int bodyLimit = state != null ? state.BodyBalance.TechnicalSegmentLimit : FallbackBodyLimit;
        int minimumExplosionSegments = state != null ? state.BodyBalance.MinimumExplosionSegments : 4;
        bool hasRole = chainSnapshot.Segments != null && chainSnapshot.Segments.Length > 0;
        OSBodyRoleType role = hasRole
            ? chainSnapshot.Segments[chainSnapshot.Segments.Length - 1].RoleType
            : default;

        return new OSCombatHudSnapshot(
            playerHealth != null && playerHealth.MaxHp > 0f ? playerHealth.CurrentHp : state?.CurrentHp ?? 0f,
            playerHealth != null && playerHealth.MaxHp > 0f ? playerHealth.MaxHp : state?.MaxHp ?? 0f,
            chainSnapshot.Segments == null ? 0 : chainSnapshot.ActiveSegmentCount,
            bodyLimit,
            state?.BodyFragments ?? 0,
            state != null ? state.BodyBalance.BodyFragmentsPerSegment : 12,
            state?.Experience ?? 0,
            state?.ExperienceToNextLevel ?? 1,
            state?.Level ?? 1,
            waveDirector != null ? waveDirector.ElapsedCombatSeconds : Time.timeSinceLevelLoad,
            state?.State ?? OSSessionState.Boot,
            role,
            hasRole,
            minimumExplosionSegments,
            explosionSnapshot.IsPending,
            explosionSnapshot.TelegraphRemaining,
            hasBossWarning && !IsBossWarningExpired(),
            waveDirector != null && waveDirector.BossSpawned);
    }

    private void ApplyViewModel(OSCombatHudViewModel viewModel)
    {
        SetText(hpText, viewModel.HpText);
        SetText(xpLevelText, viewModel.XpLevelText);
        SetText(bodyCountText, viewModel.BodyCountText);
        SetText(timerText, viewModel.TimerText);
        SetText(stateText, viewModel.StateText);
        SetText(currentRoleText, viewModel.CurrentRoleText);
        SetText(explosionStatusText, viewModel.ExplosionStatusText);
        SetText(bossWarningText, viewModel.BossWarningText);

        SetSlider(hpSlider, viewModel.HpNormalized);
        SetSlider(xpSlider, viewModel.XpNormalized);
        SetSlider(bodySlider, viewModel.BodyNormalized);

        SetImageEnabled(currentRoleImage, viewModel.RoleVisible);
        SetImageEnabled(explosionReadyImage, viewModel.ExplosionVisible);
        SetImageEnabled(bossWarningImage, viewModel.BossWarningVisible);
    }

    private void ObserveRuntimeState(OSSessionRuntimeState state)
    {
        if (observedRuntimeState == state)
        {
            return;
        }

        if (observedRuntimeState != null)
        {
            observedRuntimeState.RuntimeStateChanged -= OnRuntimeStateChanged;
        }

        observedRuntimeState = state;
        if (observedRuntimeState != null)
        {
            observedRuntimeState.RuntimeStateChanged += OnRuntimeStateChanged;
        }
    }

    private void OnSessionStarted(OSSessionRuntimeState state)
    {
        ObserveRuntimeState(state);
        hasBossWarning = false;
        lastWaveEvent = default;
        Refresh();
    }

    private void OnSessionEnded(OSSessionSummary summary)
    {
        Refresh();
    }

    private void OnRuntimeStateChanged(OSSessionRuntimeState state)
    {
        Refresh();
    }

    private void OnSessionStateChanged(OSSessionState state)
    {
        Refresh();
    }

    private void OnSelectionOpened(OSSelectionRequest request)
    {
        Refresh();
    }

    private void OnSelectionCompleted(OSSelectionRequest request)
    {
        Refresh();
    }

    private void OnHealthChanged(OSPlayerHealthSnapshot snapshot)
    {
        Refresh();
    }

    private void OnInvulnerabilityChanged(OSPlayerInvulnerabilitySnapshot snapshot)
    {
        Refresh();
    }

    private void OnChainChanged(OSBodyChainSnapshot snapshot)
    {
        Refresh();
    }

    private void OnExplosionTelegraphStarted(OSExplosionSnapshot snapshot)
    {
        Refresh();
    }

    private void OnExplosionCompleted(OSExplosionCompletionResult result)
    {
        Refresh();
    }

    private void OnWaveEventRaised(OSWaveEvent waveEvent)
    {
        lastWaveEvent = waveEvent;
        hasBossWarning = waveEvent.EventType == OSWaveEventType.BossWarning ||
            waveEvent.EventType == OSWaveEventType.BossSpawned;
        Refresh();
    }

    private bool IsBossWarningExpired()
    {
        if (waveDirector == null || lastWaveEvent.EventType == OSWaveEventType.BossSpawned)
        {
            return false;
        }

        return waveDirector.ElapsedCombatSeconds - lastWaveEvent.ElapsedSeconds > 5f;
    }

    private static void SetText(Text label, string value)
    {
        if (label != null)
        {
            label.text = value;
        }
    }

    private static void SetSlider(Slider slider, float value)
    {
        if (slider == null)
        {
            return;
        }

        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.value = Mathf.Clamp01(value);
    }

    private static void SetImageEnabled(Image image, bool enabled)
    {
        if (image != null)
        {
            image.enabled = enabled;
        }
    }

    private static string FormatTimer(float seconds)
    {
        float clampedSeconds = Mathf.Max(0f, seconds);
        int totalSeconds = Mathf.FloorToInt(clampedSeconds);
        int minutes = totalSeconds / 60;
        int remainder = totalSeconds % 60;
        return $"{minutes:00}:{remainder:00}";
    }

    private static string FormatState(OSSessionState state)
    {
        switch (state)
        {
            case OSSessionState.BodyRoleSelection:
                return "몸통 선택";
            case OSSessionState.Combat:
                return "전투";
            case OSSessionState.ExplosionTelegraph:
                return "폭발";
            case OSSessionState.LevelUpSelection:
                return "레벨업";
            case OSSessionState.Dead:
                return "사망";
            default:
                return "준비";
        }
    }

    private static string FormatRole(OSBodyRoleType role)
    {
        switch (role)
        {
            case OSBodyRoleType.Shield:
                return "방패";
            case OSBodyRoleType.Attack:
                return "공격";
            case OSBodyRoleType.Laser:
                return "레이저";
            case OSBodyRoleType.Control:
                return "제어";
            default:
                return "없음";
        }
    }

    private static string FormatExplosion(OSCombatHudSnapshot snapshot, bool explosionReady)
    {
        if (snapshot.IsExplosionPending)
        {
            return $"폭발 {Mathf.Max(0f, snapshot.ExplosionTelegraphRemaining):0.0}s";
        }

        if (explosionReady)
        {
            return "폭발 가능";
        }

        return $"폭발 {Mathf.Max(0, snapshot.BodySegmentCount)}/{Mathf.Max(1, snapshot.MinimumExplosionSegments)}";
    }
}

public readonly struct OSCombatHudSnapshot
{
    public OSCombatHudSnapshot(
        float currentHp,
        float maxHp,
        int bodySegmentCount,
        int bodySegmentLimit,
        int bodyFragments,
        int bodyFragmentsPerSegment,
        int experience,
        int experienceToNextLevel,
        int level,
        float elapsedSeconds,
        OSSessionState sessionState,
        OSBodyRoleType currentRole,
        bool hasCurrentRole,
        int minimumExplosionSegments,
        bool isExplosionPending,
        float explosionTelegraphRemaining,
        bool bossWarningActive,
        bool bossSpawned)
    {
        CurrentHp = currentHp;
        MaxHp = maxHp;
        BodySegmentCount = bodySegmentCount;
        BodySegmentLimit = bodySegmentLimit;
        BodyFragments = bodyFragments;
        BodyFragmentsPerSegment = bodyFragmentsPerSegment;
        Experience = experience;
        ExperienceToNextLevel = experienceToNextLevel;
        Level = level;
        ElapsedSeconds = elapsedSeconds;
        SessionState = sessionState;
        CurrentRole = currentRole;
        HasCurrentRole = hasCurrentRole;
        MinimumExplosionSegments = minimumExplosionSegments;
        IsExplosionPending = isExplosionPending;
        ExplosionTelegraphRemaining = explosionTelegraphRemaining;
        BossWarningActive = bossWarningActive;
        BossSpawned = bossSpawned;
    }

    public float CurrentHp { get; }
    public float MaxHp { get; }
    public int BodySegmentCount { get; }
    public int BodySegmentLimit { get; }
    public int BodyFragments { get; }
    public int BodyFragmentsPerSegment { get; }
    public int Experience { get; }
    public int ExperienceToNextLevel { get; }
    public int Level { get; }
    public float ElapsedSeconds { get; }
    public OSSessionState SessionState { get; }
    public OSBodyRoleType CurrentRole { get; }
    public bool HasCurrentRole { get; }
    public int MinimumExplosionSegments { get; }
    public bool IsExplosionPending { get; }
    public float ExplosionTelegraphRemaining { get; }
    public bool BossWarningActive { get; }
    public bool BossSpawned { get; }
}

public readonly struct OSCombatHudViewModel
{
    public OSCombatHudViewModel(
        string hpText,
        float hpNormalized,
        string xpLevelText,
        float xpNormalized,
        string bodyCountText,
        float bodyNormalized,
        string timerText,
        string stateText,
        string currentRoleText,
        string explosionStatusText,
        string bossWarningText,
        bool roleVisible,
        bool explosionVisible,
        bool bossWarningVisible)
    {
        HpText = hpText ?? string.Empty;
        HpNormalized = hpNormalized;
        XpLevelText = xpLevelText ?? string.Empty;
        XpNormalized = xpNormalized;
        BodyCountText = bodyCountText ?? string.Empty;
        BodyNormalized = bodyNormalized;
        TimerText = timerText ?? string.Empty;
        StateText = stateText ?? string.Empty;
        CurrentRoleText = currentRoleText ?? string.Empty;
        ExplosionStatusText = explosionStatusText ?? string.Empty;
        BossWarningText = bossWarningText ?? string.Empty;
        RoleVisible = roleVisible;
        ExplosionVisible = explosionVisible;
        BossWarningVisible = bossWarningVisible;
    }

    public string HpText { get; }
    public float HpNormalized { get; }
    public string XpLevelText { get; }
    public float XpNormalized { get; }
    public string BodyCountText { get; }
    public float BodyNormalized { get; }
    public string TimerText { get; }
    public string StateText { get; }
    public string CurrentRoleText { get; }
    public string ExplosionStatusText { get; }
    public string BossWarningText { get; }
    public bool RoleVisible { get; }
    public bool ExplosionVisible { get; }
    public bool BossWarningVisible { get; }
}
