using System;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody2D))]
public sealed class OSPlayerController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Rigidbody2D body;
    [SerializeField] private Collider2D bodyCollider;
    [SerializeField] private OSPlayerBalanceData playerBalance;
    [SerializeField] private OSInputRouter inputRouter;
    [SerializeField] private OSGameSessionController sessionController;

    private Vector2 moveInput;
    private Vector2 lastValidDirection = Vector2.right;
    private bool simulationEnabled;
    private bool subscribedToInput;
    private bool subscribedToSession;

    public event Action<Vector2, Vector2> PositionAdvanced;

    public Vector2 MoveInput => moveInput;
    public Vector2 LastValidDirection => lastValidDirection;
    public bool SimulationEnabled => simulationEnabled;

    public void ConfigureForTests(
        Rigidbody2D testBody,
        Collider2D testCollider,
        OSPlayerBalanceData balance,
        OSInputRouter router = null,
        OSGameSessionController session = null)
    {
        body = testBody;
        bodyCollider = testCollider;
        playerBalance = balance;
        inputRouter = router;
        sessionController = session;
    }

    public OSRuleResult<Vector2> SetMoveInput(Vector2 input)
    {
        if (!IsFinite(input))
        {
            moveInput = Vector2.zero;
            return OSRuleResult<Vector2>.Rejected(OSResultCode.ConfigurationError, "move_input_invalid");
        }

        moveInput = Vector2.ClampMagnitude(input, 1f);
        return OSRuleResult<Vector2>.Accept(moveInput);
    }

    public void SetSimulationEnabled(bool enabled)
    {
        simulationEnabled = enabled;
        if (!enabled)
        {
            moveInput = Vector2.zero;
            if (body != null)
            {
                body.linearVelocity = Vector2.zero;
            }
        }
    }

    public OSRuleResult<Vector2> AdvanceMovement(float deltaTime)
    {
        if (!CanMove(deltaTime))
        {
            return OSRuleResult<Vector2>.Rejected(OSResultCode.RejectedState, "movement_disabled");
        }

        if (playerBalance == null || body == null || bodyCollider == null ||
            playerBalance.ValidateConfiguration() != OSConfigurationValidationResult.Accepted)
        {
            return OSRuleResult<Vector2>.Rejected(OSResultCode.ConfigurationError, "player_movement_configuration_invalid");
        }

        if (moveInput == Vector2.zero)
        {
            body.linearVelocity = Vector2.zero;
            return OSRuleResult<Vector2>.Accept(Vector2.zero);
        }

        Vector2 displacement = moveInput * playerBalance.MoveSpeed * deltaTime;
        Vector2 targetPosition = body.position + displacement;
        body.MovePosition(targetPosition);
        lastValidDirection = moveInput;
        PositionAdvanced?.Invoke(targetPosition, lastValidDirection);

        return OSRuleResult<Vector2>.Accept(displacement);
    }

    private void Reset()
    {
        ResolveReferences();
        ConfigureBody();
    }

    private void Awake()
    {
        ResolveReferences();
        ConfigureBody();
    }

    private void OnEnable()
    {
        ResolveReferences();
        Subscribe();
        SetSimulationEnabled(IsSessionMovementState());
    }

    private void OnDisable()
    {
        Unsubscribe();
        SetSimulationEnabled(false);
    }

    private void FixedUpdate()
    {
        AdvanceMovement(Time.fixedDeltaTime);
    }

    private void ResolveReferences()
    {
        body = body != null ? body : GetComponent<Rigidbody2D>();
        bodyCollider = bodyCollider != null ? bodyCollider : GetComponent<Collider2D>();
    }

    private void ConfigureBody()
    {
        if (body == null)
        {
            return;
        }

        body.gravityScale = 0f;
        body.freezeRotation = true;
        body.interpolation = RigidbodyInterpolation2D.Interpolate;
    }

    private void Subscribe()
    {
        if (inputRouter != null && !subscribedToInput)
        {
            inputRouter.MoveChanged += OnMoveChanged;
            subscribedToInput = true;
        }

        if (sessionController != null && !subscribedToSession)
        {
            sessionController.StateChanged += OnSessionStateChanged;
            subscribedToSession = true;
        }
    }

    private void Unsubscribe()
    {
        if (inputRouter != null && subscribedToInput)
        {
            inputRouter.MoveChanged -= OnMoveChanged;
            subscribedToInput = false;
        }

        if (sessionController != null && subscribedToSession)
        {
            sessionController.StateChanged -= OnSessionStateChanged;
            subscribedToSession = false;
        }
    }

    private void OnMoveChanged(Vector2 input)
    {
        SetMoveInput(input);
    }

    private void OnSessionStateChanged(OSSessionState state)
    {
        SetSimulationEnabled(state == OSSessionState.Combat || state == OSSessionState.ExplosionTelegraph);
    }

    private bool CanMove(float deltaTime)
    {
        return simulationEnabled &&
            IsSessionMovementState() &&
            deltaTime > 0f &&
            !float.IsNaN(deltaTime) &&
            !float.IsInfinity(deltaTime);
    }

    private bool IsSessionMovementState()
    {
        if (sessionController == null)
        {
            return simulationEnabled;
        }

        OSSessionState state = sessionController.CurrentState;
        return state == OSSessionState.Combat || state == OSSessionState.ExplosionTelegraph;
    }

    private static bool IsFinite(Vector2 value)
    {
        return !float.IsNaN(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsInfinity(value.x) &&
            !float.IsInfinity(value.y);
    }
}
