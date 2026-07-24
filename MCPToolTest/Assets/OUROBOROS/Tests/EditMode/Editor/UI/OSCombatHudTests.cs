#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public sealed class OSCombatHudTests
{
    private GameObject hudHost;
    private GameObject chainHost;
    private GameObject sessionHost;
    private OSCombatHud hud;
    private OSBodyChain bodyChain;
    private OSGameSessionController sessionController;
    private OSPlayerBalanceData playerBalance;
    private OSBodyBalanceData bodyBalance;
    private OSEncounterBalanceData encounterBalance;
    private OSUpgradeCatalog upgradeCatalog;
    private Text hpText;
    private Text xpText;
    private Text bodyText;
    private Text timerText;
    private Text stateText;
    private Text roleText;
    private Text explosionText;
    private Text bossText;
    private Slider hpSlider;
    private Slider xpSlider;
    private Slider bodySlider;
    private Image roleImage;
    private Image explosionImage;
    private Image bossImage;
    private float previousTimeScale;

    [SetUp]
    public void SetUp()
    {
        previousTimeScale = Time.timeScale;
        Time.timeScale = 1f;

        playerBalance = ScriptableObject.CreateInstance<OSPlayerBalanceData>();
        bodyBalance = ScriptableObject.CreateInstance<OSBodyBalanceData>();
        encounterBalance = ScriptableObject.CreateInstance<OSEncounterBalanceData>();
        upgradeCatalog = ScriptableObject.CreateInstance<OSUpgradeCatalog>();

        chainHost = new GameObject("BodyChain");
        bodyChain = chainHost.AddComponent<OSBodyChain>();
        bodyChain.ConfigureForTests(bodyBalance);

        sessionHost = new GameObject("GameSession");
        sessionController = sessionHost.AddComponent<OSGameSessionController>();
        sessionController.ConfigureForTests(playerBalance, bodyBalance, encounterBalance, upgradeCatalog);

        hudHost = new GameObject("CombatHud");
        hud = hudHost.AddComponent<OSCombatHud>();
        hpText = CreateText("HpText");
        xpText = CreateText("XpText");
        bodyText = CreateText("BodyText");
        timerText = CreateText("TimerText");
        stateText = CreateText("StateText");
        roleText = CreateText("RoleText");
        explosionText = CreateText("ExplosionText");
        bossText = CreateText("BossText");
        hpSlider = CreateSlider("HpSlider");
        xpSlider = CreateSlider("XpSlider");
        bodySlider = CreateSlider("BodySlider");
        roleImage = CreateImage("RoleImage");
        explosionImage = CreateImage("ExplosionImage");
        bossImage = CreateImage("BossImage");

        hud.ConfigureForTests(
            null,
            null,
            bodyChain,
            null,
            null,
            hpText,
            xpText,
            bodyText,
            timerText,
            stateText,
            roleText,
            explosionText,
            bossText,
            hpSlider,
            xpSlider,
            bodySlider,
            roleImage,
            explosionImage,
            bossImage);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(hudHost);
        Object.DestroyImmediate(chainHost);
        Object.DestroyImmediate(sessionHost);
        Object.DestroyImmediate(playerBalance);
        Object.DestroyImmediate(bodyBalance);
        Object.DestroyImmediate(encounterBalance);
        Object.DestroyImmediate(upgradeCatalog);
        Time.timeScale = previousTimeScale;
    }

    [Test]
    public void BuildViewModel_ClampsBodyDisplayAtTechnicalLimit()
    {
        Assert.That(BuildBodyText(0), Is.EqualTo("몸통 0/64 | 성장 5/12"));
        Assert.That(BuildBodyText(20), Is.EqualTo("몸통 20/64 | 성장 5/12"));
        Assert.That(BuildBodyText(40), Is.EqualTo("몸통 40/64 | 성장 5/12"));
        Assert.That(BuildBodyText(64), Is.EqualTo("몸통 64/64 | 성장 5/12"));
        Assert.That(BuildBodyText(80), Is.EqualTo("몸통 64+/64 | 성장 5/12"));
    }

    [Test]
    public void Refresh_AppliesHpXpTimerAndStateToUgui()
    {
        OSCombatHudSnapshot snapshot = new OSCombatHudSnapshot(
            37f,
            100f,
            20,
            64,
            6,
            12,
            8,
            20,
            3,
            65.9f,
            OSSessionState.Combat,
            OSBodyRoleType.Attack,
            true,
            4,
            false,
            0f,
            false,
            false);

        hud.Refresh(snapshot);

        Assert.That(hpText.text, Is.EqualTo("체력 37/100"));
        Assert.That(hpSlider.value, Is.EqualTo(0.37f).Within(0.0001f));
        Assert.That(xpText.text, Is.EqualTo("레벨 3  8/20"));
        Assert.That(xpSlider.value, Is.EqualTo(0.4f).Within(0.0001f));
        Assert.That(bodyText.text, Is.EqualTo("몸통 20/64 | 성장 6/12"));
        Assert.That(bodySlider.value, Is.EqualTo(0.5f).Within(0.0001f));
        Assert.That(timerText.text, Is.EqualTo("01:05"));
        Assert.That(stateText.text, Is.EqualTo("전투"));
    }

    [Test]
    public void Refresh_ShowsRoleExplosionAndBossStatus()
    {
        OSCombatHudSnapshot snapshot = new OSCombatHudSnapshot(
            100f,
            100f,
            4,
            64,
            3,
            12,
            0,
            20,
            1,
            0f,
            OSSessionState.ExplosionTelegraph,
            OSBodyRoleType.Laser,
            true,
            4,
            true,
            0.24f,
            true,
            false);

        hud.Refresh(snapshot);

        Assert.That(roleText.text, Is.EqualTo("역할 레이저"));
        Assert.That(roleImage.enabled, Is.True);
        Assert.That(explosionText.text, Is.EqualTo("폭발 0.2s"));
        Assert.That(explosionImage.enabled, Is.True);
        Assert.That(bossText.text, Is.EqualTo("보스 접근"));
        Assert.That(bossImage.enabled, Is.True);
    }

    [Test]
    public void RepeatedBind_DoesNotDuplicateChainChangedRefresh()
    {
        hud.Bind(null, null, bodyChain, null, null);
        hud.Bind(null, null, bodyChain, null, null);
        int refreshCount = 0;
        hud.HudRefreshed += _ => refreshCount++;

        Assert.That(bodyChain.AppendSegment(OSBodyRoleType.Attack).IsAccepted, Is.True);

        Assert.That(refreshCount, Is.EqualTo(1));
        Assert.That(hud.LastViewModel.BodyCountText, Is.EqualTo("몸통 1/64 | 성장 0/12"));
        Assert.That(hud.LastViewModel.CurrentRoleText, Is.EqualTo("역할 공격"));
    }

    [Test]
    public void RepeatedBind_DoesNotDuplicateSelectionTransitionRefresh()
    {
        hud.Bind(sessionController, null, bodyChain, null, null);
        hud.Bind(sessionController, null, bodyChain, null, null);
        int refreshCount = 0;
        hud.HudRefreshed += _ => refreshCount++;

        Assert.That(sessionController.StartSession().IsAccepted, Is.True);

        Assert.That(sessionController.CurrentState, Is.EqualTo(OSSessionState.BodyRoleSelection));
        Assert.That(refreshCount, Is.EqualTo(4));
        Assert.That(hud.LastViewModel.StateText, Is.EqualTo("몸통 선택"));
    }

    private string BuildBodyText(int count)
    {
        OSCombatHudSnapshot snapshot = new OSCombatHudSnapshot(
            10f,
            10f,
            count,
            64,
            5,
            12,
            0,
            1,
            1,
            0f,
            OSSessionState.Combat,
            OSBodyRoleType.Attack,
            count > 0,
            4,
            false,
            0f,
            false,
            false);

        return OSCombatHud.BuildViewModel(snapshot).BodyCountText;
    }

    private Text CreateText(string name)
    {
        GameObject target = new GameObject(name);
        target.transform.SetParent(hudHost.transform);
        return target.AddComponent<Text>();
    }

    private Slider CreateSlider(string name)
    {
        GameObject target = new GameObject(name);
        target.transform.SetParent(hudHost.transform);
        return target.AddComponent<Slider>();
    }

    private Image CreateImage(string name)
    {
        GameObject target = new GameObject(name);
        target.transform.SetParent(hudHost.transform);
        return target.AddComponent<Image>();
    }
}
#endif
