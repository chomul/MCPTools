#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public sealed class OSResultPanelTests
{
    private GameObject sessionHost;
    private GameObject inputHost;
    private GameObject panelHost;
    private GameObject panelRoot;
    private OSGameSessionController sessionController;
    private OSInputRouter inputRouter;
    private OSResultPanel panel;
    private InputActionAsset inputAsset;
    private OSPlayerBalanceData playerBalance;
    private OSBodyBalanceData bodyBalance;
    private OSEncounterBalanceData encounterBalance;
    private OSUpgradeCatalog upgradeCatalog;
    private Text titleLabel;
    private Text survivalTimeLabel;
    private Text levelLabel;
    private Text bodyLabel;
    private Text explosionLabel;
    private Text milestoneLabel;
    private Text reasonLabel;
    private Text restartLabel;
    private Button restartButton;

    [SetUp]
    public void SetUp()
    {
        playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        encounterBalance = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        upgradeCatalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        inputHost = new GameObject("InputRouter");
        inputRouter = inputHost.AddComponent<OSInputRouter>();
        ConfigureInputRouter();

        sessionHost = new GameObject("GameSession");
        sessionController = sessionHost.AddComponent<OSGameSessionController>();
        sessionController.ConfigureForTests(playerBalance, bodyBalance, encounterBalance, upgradeCatalog, inputRouter);

        panelHost = new GameObject("ResultPanel");
        panel = panelHost.AddComponent<OSResultPanel>();
        panelRoot = new GameObject("PanelRoot");
        panelRoot.transform.SetParent(panelHost.transform);
        titleLabel = CreateText("Title");
        survivalTimeLabel = CreateText("SurvivalTime");
        levelLabel = CreateText("Level");
        bodyLabel = CreateText("Body");
        explosionLabel = CreateText("Explosion");
        milestoneLabel = CreateText("Milestone");
        reasonLabel = CreateText("Reason");
        restartButton = CreateButton("Restart", out restartLabel);

        panel.ConfigureForTests(
            panelRoot,
            sessionController,
            inputRouter,
            titleLabel,
            survivalTimeLabel,
            levelLabel,
            bodyLabel,
            explosionLabel,
            milestoneLabel,
            reasonLabel,
            restartButton,
            restartLabel);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(panelHost);
        Object.DestroyImmediate(sessionHost);
        Object.DestroyImmediate(inputHost);
        Object.DestroyImmediate(inputAsset);
        Object.DestroyImmediate(playerBalance);
        Object.DestroyImmediate(bodyBalance);
        Object.DestroyImmediate(encounterBalance);
        Object.DestroyImmediate(upgradeCatalog);
        Time.timeScale = 1f;
    }

    [Test]
    public void Show_UsesProvidedSummaryWithoutRecalculation()
    {
        OSSessionSummary summary = new OSSessionSummary(
            OSResultCode.RejectedState,
            "head_damage",
            125.9f,
            7,
            0f,
            120f,
            18,
            42,
            true,
            345,
            67,
            5);

        OSRuleResult<OSSessionSummary> result = panel.Show(summary);

        Assert.That(result.IsAccepted, Is.True);
        Assert.That(panel.IsVisible, Is.True);
        Assert.That(panel.HasSummary, Is.True);
        Assert.That(panel.LatestSummary.SurvivalTimeSeconds, Is.EqualTo(125.9f));
        Assert.That(survivalTimeLabel.text, Is.EqualTo("생존 시간 02:05"));
        Assert.That(levelLabel.text, Is.EqualTo("레벨 7 | 경험치 345"));
        Assert.That(bodyLabel.text, Is.EqualTo("최대 몸통 18 | 몸통 조각 67"));
        Assert.That(explosionLabel.text, Is.EqualTo("폭발 처치 42 | 업그레이드 5"));
        Assert.That(milestoneLabel.text, Is.EqualTo("마일스톤 첫 보스 처치"));
        Assert.That(reasonLabel.text, Is.EqualTo("사망 원인 머리 피격"));
    }

    [Test]
    public void SessionEnded_OpensPanelFromSummary()
    {
        StartCombatSession();

        sessionController.RequestDeath("manual_dead");

        Assert.That(panel.IsVisible, Is.True);
        Assert.That(panel.HasSummary, Is.True);
        Assert.That(reasonLabel.text, Is.EqualTo("사망 원인 수동 종료"));
        Assert.That(restartButton.interactable, Is.True);
    }

    [Test]
    public void RestartButton_RestartsSessionAndHidesPanel()
    {
        StartCombatSession();
        OSSessionRuntimeState previousState = sessionController.RuntimeState;
        sessionController.RequestDeath("manual_dead");

        OSRuleResult<OSSessionRuntimeState> restart = panel.RequestRestart();

        Assert.That(restart.IsAccepted, Is.True);
        Assert.That(panel.IsVisible, Is.False);
        Assert.That(panel.HasSummary, Is.False);
        Assert.That(sessionController.RuntimeState, Is.Not.SameAs(previousState));
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
    }

    [Test]
    public void RestartButtonClick_RestartsSessionOnce()
    {
        StartCombatSession();
        sessionController.RequestDeath("manual_dead");

        restartButton.onClick.Invoke();

        Assert.That(panel.IsVisible, Is.False);
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
    }

    private void StartCombatSession()
    {
        Assert.That(sessionController.StartSession().IsAccepted, Is.True);
        Assert.That(sessionController.CompleteCurrentSelection(0).IsAccepted, Is.True);
        Assert.That(sessionController.CompleteCurrentSelection(1).IsAccepted, Is.True);
        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.Combat));
    }

    private void ConfigureInputRouter()
    {
        inputAsset = ScriptableObject.CreateInstance<InputActionAsset>();
        InputActionMap playerMap = inputAsset.AddActionMap("Player");
        InputActionMap uiMap = inputAsset.AddActionMap("UI");

        InputAction move = playerMap.AddAction("Move", InputActionType.Value);
        InputAction explosion = playerMap.AddAction("Explosion", InputActionType.Button);
        InputAction point = uiMap.AddAction("Point", InputActionType.Value);
        InputAction click = uiMap.AddAction("Click", InputActionType.Button);
        InputAction navigate = uiMap.AddAction("Navigate", InputActionType.Value);
        InputAction submit = uiMap.AddAction("Submit", InputActionType.Button);

        inputRouter.ConfigureForTests(move, explosion, point, click, navigate, submit);
    }

    private Text CreateText(string name)
    {
        GameObject textObject = new GameObject(name);
        textObject.transform.SetParent(panelRoot.transform);
        return textObject.AddComponent<Text>();
    }

    private Button CreateButton(string name, out Text label)
    {
        GameObject buttonObject = new GameObject(name);
        buttonObject.transform.SetParent(panelRoot.transform);
        Button button = buttonObject.AddComponent<Button>();
        GameObject textObject = new GameObject($"{name}Label");
        textObject.transform.SetParent(buttonObject.transform);
        label = textObject.AddComponent<Text>();
        return button;
    }
}
#endif
