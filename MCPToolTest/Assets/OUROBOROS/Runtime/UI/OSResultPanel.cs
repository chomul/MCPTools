using System;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class OSResultPanel : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private OSGameSessionController sessionController;
    [SerializeField] private OSInputRouter inputRouter;

    [Header("Labels")]
    [SerializeField] private Text titleLabel;
    [SerializeField] private Text survivalTimeLabel;
    [SerializeField] private Text levelLabel;
    [SerializeField] private Text bodyLabel;
    [SerializeField] private Text explosionLabel;
    [SerializeField] private Text milestoneLabel;
    [SerializeField] private Text reasonLabel;

    [Header("Buttons")]
    [SerializeField] private Button restartButton;
    [SerializeField] private Text restartButtonLabel;

    private OSSessionSummary latestSummary;
    private bool hasSummary;
    private bool isRestarting;

    public event Action<OSSessionSummary> ResultShown;
    public event Action RestartRequested;

    public bool IsVisible => panelRoot != null && panelRoot.activeSelf;
    public bool HasSummary => hasSummary;
    public OSSessionSummary LatestSummary => latestSummary;

    public void ConfigureForTests(
        GameObject root,
        OSGameSessionController session,
        OSInputRouter router,
        Text title,
        Text survivalTime,
        Text level,
        Text body,
        Text explosion,
        Text milestone,
        Text reason,
        Button restart,
        Text restartText = null)
    {
        UnregisterButtonCallbacks();
        Unsubscribe();

        panelRoot = root;
        sessionController = session;
        inputRouter = router;
        titleLabel = title;
        survivalTimeLabel = survivalTime;
        levelLabel = level;
        bodyLabel = body;
        explosionLabel = explosion;
        milestoneLabel = milestone;
        reasonLabel = reason;
        restartButton = restart;
        restartButtonLabel = restartText;

        ApplyEmptyView();
        Subscribe();
        RegisterButtonCallbacks();
    }

    public OSRuleResult<OSSessionSummary> Show(OSSessionSummary summary)
    {
        latestSummary = summary;
        hasSummary = true;
        isRestarting = false;
        ApplySummaryView(summary);
        SetPanelVisible(true);
        SetRestartInteractable(true);
        ResultShown?.Invoke(summary);
        return OSRuleResult<OSSessionSummary>.Accept(summary);
    }

    public void Hide()
    {
        hasSummary = false;
        isRestarting = false;
        SetPanelVisible(false);
        SetRestartInteractable(false);
    }

    public OSRuleResult<OSSessionRuntimeState> RequestRestart()
    {
        if (isRestarting)
        {
            return OSRuleResult<OSSessionRuntimeState>.Rejected(OSResultCode.Duplicate, "result_restart_already_requested");
        }

        if (sessionController == null)
        {
            return OSRuleResult<OSSessionRuntimeState>.Rejected(OSResultCode.ConfigurationError, "result_session_missing");
        }

        isRestarting = true;
        SetRestartInteractable(false);
        RestartRequested?.Invoke();

        OSRuleResult<OSSessionRuntimeState> restartResult = sessionController.RestartSession();
        if (!restartResult.IsAccepted)
        {
            isRestarting = false;
            SetRestartInteractable(true);
            return restartResult;
        }

        Hide();
        return restartResult;
    }

    private void Awake()
    {
        ApplyEmptyView();
        SetPanelVisible(false);
        SetRestartInteractable(false);
    }

    private void OnEnable()
    {
        Subscribe();
        RegisterButtonCallbacks();
    }

    private void OnDisable()
    {
        UnregisterButtonCallbacks();
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (sessionController != null)
        {
            sessionController.SessionEnded += OnSessionEnded;
            sessionController.SessionStarted += OnSessionStarted;
        }

        if (inputRouter != null)
        {
            inputRouter.UiSubmitPressed += OnSubmit;
        }
    }

    private void Unsubscribe()
    {
        if (sessionController != null)
        {
            sessionController.SessionEnded -= OnSessionEnded;
            sessionController.SessionStarted -= OnSessionStarted;
        }

        if (inputRouter != null)
        {
            inputRouter.UiSubmitPressed -= OnSubmit;
        }
    }

    private void RegisterButtonCallbacks()
    {
        if (restartButton != null)
        {
            restartButton.onClick.AddListener(OnRestartClicked);
        }
    }

    private void UnregisterButtonCallbacks()
    {
        if (restartButton != null)
        {
            restartButton.onClick.RemoveListener(OnRestartClicked);
        }
    }

    private void OnSessionEnded(OSSessionSummary summary)
    {
        Show(summary);
    }

    private void OnSessionStarted(OSSessionRuntimeState state)
    {
        Hide();
    }

    private void OnRestartClicked()
    {
        RequestRestart();
    }

    private void OnSubmit()
    {
        if (IsVisible)
        {
            RequestRestart();
        }
    }

    private void ApplyEmptyView()
    {
        SetText(titleLabel, "생존 종료");
        SetText(survivalTimeLabel, "생존 시간 00:00");
        SetText(levelLabel, "레벨 1");
        SetText(bodyLabel, "최대 몸통 0");
        SetText(explosionLabel, "폭발 처치 0");
        SetText(milestoneLabel, "마일스톤 기록 없음");
        SetText(reasonLabel, "사망 원인 -");
        SetText(restartButtonLabel, "다시 시작");
    }

    private void ApplySummaryView(OSSessionSummary summary)
    {
        SetText(titleLabel, summary.ResultCode == OSResultCode.Accepted ? "생존 기록" : "생존 종료");
        SetText(survivalTimeLabel, $"생존 시간 {FormatTime(summary.SurvivalTimeSeconds)}");
        SetText(levelLabel, $"레벨 {summary.Level} | 경험치 {summary.TotalExperienceCollected}");
        SetText(bodyLabel, $"최대 몸통 {summary.MaxActiveBodySegments} | 몸통 조각 {summary.TotalBodyFragmentsCollected}");
        SetText(explosionLabel, $"폭발 처치 {summary.ExplosionKillCount} | 업그레이드 {summary.UpgradesApplied}");
        SetText(milestoneLabel, summary.BossDefeated ? "마일스톤 첫 보스 처치" : "마일스톤 진행 중");
        SetText(reasonLabel, $"사망 원인 {FormatReason(summary.ReasonKey)}");
        SetText(restartButtonLabel, "다시 시작");
    }

    private void SetPanelVisible(bool visible)
    {
        if (panelRoot != null)
        {
            panelRoot.SetActive(visible);
        }
    }

    private void SetRestartInteractable(bool interactable)
    {
        if (restartButton != null)
        {
            restartButton.interactable = interactable;
        }
    }

    private static string FormatTime(float seconds)
    {
        int safeSeconds = Mathf.Max(0, Mathf.FloorToInt(seconds));
        int minutes = safeSeconds / 60;
        int remainderSeconds = safeSeconds % 60;
        return $"{minutes:00}:{remainderSeconds:00}";
    }

    private static string FormatReason(string reasonKey)
    {
        switch (reasonKey)
        {
            case "head_damage":
                return "머리 피격";
            case "manual_dead":
                return "수동 종료";
            case "dead":
                return "체력 소진";
            case "test_dead":
                return "테스트 종료";
            default:
                return string.IsNullOrWhiteSpace(reasonKey) ? "-" : reasonKey;
        }
    }

    private static void SetText(Text label, string text)
    {
        if (label != null)
        {
            label.text = text;
        }
    }
}
