using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Standalone manual player controller for testing locomotion and kicking.
/// Kept completely disconnected from FootballAgentML and disabled by default
/// so FreeCamera can navigate without accidentally driving the bot.
/// </summary>
[RequireComponent(typeof(FootballAgent))]
public class ManualPlayerController : MonoBehaviour
{
    [Header("Activation Toggle")]
    [Tooltip("Check this box ONLY when you explicitly want to drive this character with Keyboard/Mouse/Gamepad.")]
    public bool enableManualControl = false;

    [Header("Aim & Kick Settings")]
    [Range(0f, 1f)] public float defaultKickElevation = 0.2f;
    [Range(0f, 1f)] public float defaultKickForce = 1.0f;

    private FootballAgent agent;
    private Vector2 smoothedKeyboardInput = Vector2.zero;

    private void Awake()
    {
        agent = GetComponent<FootballAgent>();
    }

    private void Update()
    {
        // If not explicitly enabled, do nothing (preserves FreeCamera controls)
        if (!enableManualControl || agent == null) return;

        float moveX = 0f;
        float moveZ = 0f;
        bool kickTriggered = false;
        float kickAngleX = 0f;

#if ENABLE_INPUT_SYSTEM
        // 1. Full 360-degree Gamepad Analog Stick
        var gamepad = Gamepad.current;
        if (gamepad != null)
        {
            Vector2 stick = gamepad.leftStick.ReadValue();
            if (stick.sqrMagnitude > 0.04f)
            {
                moveX = stick.x;
                moveZ = stick.y;
            }
            if (gamepad.buttonSouth.wasPressedThisFrame || gamepad.rightTrigger.wasPressedThisFrame)
            {
                kickTriggered = true;
            }
        }

        // 2. Smooth Analog Keyboard
        var keyboard = Keyboard.current;
        if (keyboard != null && (gamepad == null || (moveX == 0f && moveZ == 0f)))
        {
            float targetX = 0f;
            float targetZ = 0f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) targetX -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) targetX += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) targetZ -= 1f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) targetZ += 1f;
            if (keyboard.spaceKey.wasPressedThisFrame) kickTriggered = true;

            Vector2 raw = new Vector2(targetX, targetZ);
            if (raw.sqrMagnitude > 1f) raw.Normalize();

            smoothedKeyboardInput = Vector2.MoveTowards(smoothedKeyboardInput, raw, 15f * Time.deltaTime);
            moveX = smoothedKeyboardInput.x;
            moveZ = smoothedKeyboardInput.y;
        }

        // 3. True 360-degree Mouse Aiming for Kicks
        var mouse = Mouse.current;
        if (mouse != null && Camera.main != null)
        {
            if (mouse.leftButton.wasPressedThisFrame) kickTriggered = true;

            Ray ray = Camera.main.ScreenPointToRay(mouse.position.ReadValue());
            Plane groundPlane = new Plane(Vector3.up, new Vector3(0f, 0.15f, 0f));
            if (groundPlane.Raycast(ray, out float enterDist))
            {
                Vector3 worldHit = ray.GetPoint(enterDist);
                Vector3 toCursor = worldHit - transform.position;
                toCursor.y = 0f;
                if (toCursor.sqrMagnitude > 0.1f)
                {
                    float signedAngle = Vector3.SignedAngle(transform.forward, toCursor.normalized, Vector3.up);
                    kickAngleX = Mathf.Clamp(signedAngle / 45f, -1f, 1f);
                }
            }
        }
#else
        moveX = Input.GetAxis("Horizontal");
        moveZ = Input.GetAxis("Vertical");
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0)) kickTriggered = true;
#endif

        // Apply continuous 360-degree locomotion
        Vector2 moveInput = new Vector2(moveX, moveZ);
        if (moveInput.sqrMagnitude > 1f) moveInput.Normalize();
        agent.ApplyMovement(moveInput);

        // Execute Power Kick
        if (kickTriggered && agent.CanKickBall())
        {
            agent.ExecuteKick(new Vector2(kickAngleX, defaultKickElevation), defaultKickForce);
        }
    }
}
