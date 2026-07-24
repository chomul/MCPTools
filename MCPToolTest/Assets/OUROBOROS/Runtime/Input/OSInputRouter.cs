using System;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class OSInputRouter : MonoBehaviour
{
    [Header("Player")]
    [SerializeField] private InputActionReference moveAction;
    [SerializeField] private InputActionReference explosionAction;

    [Header("UI")]
    [SerializeField] private InputActionReference pointAction;
    [SerializeField] private InputActionReference clickAction;
    [SerializeField] private InputActionReference navigateAction;
    [SerializeField] private InputActionReference submitAction;

    private InputAction playerMove;
    private InputAction playerExplosion;
    private InputAction uiPoint;
    private InputAction uiClick;
    private InputAction uiNavigate;
    private InputAction uiSubmit;
    private InputActionMap playerActionMap;
    private InputActionMap uiActionMap;
    private bool isSubscribed;
    private bool isPlayerMapActive;
    private Vector2 latestMove;
    private Vector2 latestPoint;
    private Vector2 latestNavigate;

    public event Action<Vector2> MoveChanged;
    public event Action ExplosionPressed;
    public event Action<Vector2> UiPointChanged;
    public event Action UiClickPressed;
    public event Action<Vector2> UiNavigateChanged;
    public event Action UiSubmitPressed;

    public Vector2 LatestMove => latestMove;
    public Vector2 LatestPoint => latestPoint;
    public Vector2 LatestNavigate => latestNavigate;
    public bool IsSubscribed => isSubscribed;
    public bool IsPlayerMapActive => isPlayerMapActive;

    public void ConfigureForTests(
        InputAction move,
        InputAction explosion,
        InputAction point,
        InputAction click,
        InputAction navigate,
        InputAction submit)
    {
        if (isSubscribed)
        {
            Unsubscribe();
        }

        playerMove = move;
        playerExplosion = explosion;
        uiPoint = point;
        uiClick = click;
        uiNavigate = navigate;
        uiSubmit = submit;
        ResolveActionMaps();
    }

    public void ActivatePlayerMap()
    {
        ResolveActions();
        SetActionMapState(playerActionMap, true);
        SetActionMapState(uiActionMap, false);
        latestMove = Vector2.zero;
        latestNavigate = Vector2.zero;
        isPlayerMapActive = true;
    }

    public void ActivateUiMap()
    {
        ResolveActions();
        SetActionMapState(playerActionMap, false);
        SetActionMapState(uiActionMap, true);
        latestMove = Vector2.zero;
        latestNavigate = Vector2.zero;
        isPlayerMapActive = false;
    }

    public OSRuleResult<int> ValidateConfiguration()
    {
        ResolveActionsFromReferences();
        if (playerMove == null ||
            playerExplosion == null ||
            uiPoint == null ||
            uiClick == null ||
            uiNavigate == null ||
            uiSubmit == null ||
            playerActionMap == null ||
            uiActionMap == null ||
            playerActionMap == uiActionMap)
        {
            return OSRuleResult<int>.Rejected(OSResultCode.ConfigurationError, "input_actions_invalid");
        }

        return OSRuleResult<int>.Accept(1);
    }

    private void OnEnable()
    {
        ResolveActions();
        Subscribe();
        ActivatePlayerMap();
    }

    private void OnDisable()
    {
        Unsubscribe();
        DisableActionMap(playerActionMap);
        DisableActionMap(uiActionMap);
        latestMove = Vector2.zero;
        latestPoint = Vector2.zero;
        latestNavigate = Vector2.zero;
    }

    private void ResolveActions()
    {
        playerMove = playerMove ?? moveAction?.action;
        playerExplosion = playerExplosion ?? explosionAction?.action;
        uiPoint = uiPoint ?? pointAction?.action;
        uiClick = uiClick ?? clickAction?.action;
        uiNavigate = uiNavigate ?? navigateAction?.action;
        uiSubmit = uiSubmit ?? submitAction?.action;
        ResolveActionMaps();
    }

    private void ResolveActionsFromReferences()
    {
        playerMove = moveAction?.action ?? playerMove;
        playerExplosion = explosionAction?.action ?? playerExplosion;
        uiPoint = pointAction?.action ?? uiPoint;
        uiClick = clickAction?.action ?? uiClick;
        uiNavigate = navigateAction?.action ?? uiNavigate;
        uiSubmit = submitAction?.action ?? uiSubmit;
        ResolveActionMaps();
    }

    private void ResolveActionMaps()
    {
        playerActionMap = playerMove?.actionMap ?? playerExplosion?.actionMap;
        uiActionMap = uiPoint?.actionMap ?? uiClick?.actionMap ?? uiNavigate?.actionMap ?? uiSubmit?.actionMap;
    }

    private void Subscribe()
    {
        if (isSubscribed)
        {
            return;
        }

        if (playerMove != null)
        {
            playerMove.performed += OnMovePerformed;
            playerMove.canceled += OnMoveCanceled;
        }

        if (playerExplosion != null)
        {
            playerExplosion.performed += OnExplosionPerformed;
        }

        if (uiPoint != null)
        {
            uiPoint.performed += OnPointPerformed;
            uiPoint.canceled += OnPointCanceled;
        }

        if (uiClick != null)
        {
            uiClick.performed += OnClickPerformed;
        }

        if (uiNavigate != null)
        {
            uiNavigate.performed += OnNavigatePerformed;
            uiNavigate.canceled += OnNavigateCanceled;
        }

        if (uiSubmit != null)
        {
            uiSubmit.performed += OnSubmitPerformed;
        }

        isSubscribed = true;
    }

    private void Unsubscribe()
    {
        if (!isSubscribed)
        {
            return;
        }

        if (playerMove != null)
        {
            playerMove.performed -= OnMovePerformed;
            playerMove.canceled -= OnMoveCanceled;
        }

        if (playerExplosion != null)
        {
            playerExplosion.performed -= OnExplosionPerformed;
        }

        if (uiPoint != null)
        {
            uiPoint.performed -= OnPointPerformed;
            uiPoint.canceled -= OnPointCanceled;
        }

        if (uiClick != null)
        {
            uiClick.performed -= OnClickPerformed;
        }

        if (uiNavigate != null)
        {
            uiNavigate.performed -= OnNavigatePerformed;
            uiNavigate.canceled -= OnNavigateCanceled;
        }

        if (uiSubmit != null)
        {
            uiSubmit.performed -= OnSubmitPerformed;
        }

        isSubscribed = false;
    }

    private void OnMovePerformed(InputAction.CallbackContext context)
    {
        latestMove = context.ReadValue<Vector2>();
        MoveChanged?.Invoke(latestMove);
    }

    private void OnMoveCanceled(InputAction.CallbackContext context)
    {
        latestMove = Vector2.zero;
        MoveChanged?.Invoke(latestMove);
    }

    private void OnExplosionPerformed(InputAction.CallbackContext context)
    {
        if (isPlayerMapActive)
        {
            ExplosionPressed?.Invoke();
        }
    }

    private void OnPointPerformed(InputAction.CallbackContext context)
    {
        latestPoint = context.ReadValue<Vector2>();
        UiPointChanged?.Invoke(latestPoint);
    }

    private void OnPointCanceled(InputAction.CallbackContext context)
    {
        latestPoint = Vector2.zero;
        UiPointChanged?.Invoke(latestPoint);
    }

    private void OnClickPerformed(InputAction.CallbackContext context)
    {
        if (!isPlayerMapActive)
        {
            UiClickPressed?.Invoke();
        }
    }

    private void OnNavigatePerformed(InputAction.CallbackContext context)
    {
        latestNavigate = context.ReadValue<Vector2>();
        UiNavigateChanged?.Invoke(latestNavigate);
    }

    private void OnNavigateCanceled(InputAction.CallbackContext context)
    {
        latestNavigate = Vector2.zero;
        UiNavigateChanged?.Invoke(latestNavigate);
    }

    private void OnSubmitPerformed(InputAction.CallbackContext context)
    {
        if (!isPlayerMapActive)
        {
            UiSubmitPressed?.Invoke();
        }
    }

    private static void SetActionMapState(InputActionMap actionMap, bool enabled)
    {
        if (actionMap == null)
        {
            return;
        }

        if (enabled)
        {
            if (!actionMap.enabled)
            {
                actionMap.Enable();
            }
        }
        else
        {
            DisableActionMap(actionMap);
        }
    }

    private static void DisableActionMap(InputActionMap actionMap)
    {
        if (actionMap != null && actionMap.enabled)
        {
            actionMap.Disable();
        }
    }
}
