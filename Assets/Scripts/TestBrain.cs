using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Heuristic sparring brain for FootballAgent (decision-making only; all physics lives in FootballAgent).
///
/// The bot never "holds" the ball. It moves the ball purely with FootballAgent's dribble taps,
/// so any opponent can step in and steal it.
///
/// Core idea (a standard robot-soccer approach):
///   1. GET BEHIND THE BALL: work out the "dribble line" running from the ball to the target.
///      If the bot is not on the back end of that line, it circles around the ball at a safe
///      radius (never touching it) until it is.
///   2. DRIBBLE: once lined up behind the ball, run straight through it along the line.
///      FootballAgent taps the ball in the facing direction, which is now the goal direction.
///      Tap, chase, tap, chase.
///   3. SHOOT: inside shooting range, with the ball in front and facing the goal, power-kick
///      at the side of the goal away from the nearest defender.
///
/// Extras: ball-trajectory prediction (uses the ball's linear damping), side-latching and
/// angle hysteresis (no twitching), dodging defenders standing in the dribble path,
/// wall-aware dribble lines, and teammate coordination (only one TestBrain per team chases).
/// </summary>
[RequireComponent(typeof(FootballAgent))]
public class TestBrain : MonoBehaviour
{
    public enum Mode { GetBehindBall, Dribble, Support }

    [Header("References")]
    [Tooltip("The ball (GlowBall). Auto-found by the 'Ball' tag if left empty.")]
    public Transform targetBall;

    [Tooltip("The goal this bot ATTACKS. Must be assigned.")]
    public Transform opponentGoal;

    [Header("Getting Behind The Ball")]
    [Tooltip("Gap (m) kept between body and ball while circling around it.")]
    public float orbitClearance = 1.0f;

    [Tooltip("Switch to dribbling once within this angle (deg) of the ideal line behind the ball.")]
    [Range(5f, 45f)] public float dribbleEnterAngle = 15f;

    [Tooltip("Stop dribbling and re-position once beyond this angle (deg). Must be larger than the enter angle.")]
    [Range(20f, 90f)] public float dribbleExitAngle = 40f;

    [Tooltip("How far around the ball (deg) each circling waypoint is placed.")]
    [Range(20f, 90f)] public float orbitStepAngle = 60f;

    [Header("Dribbling")]
    [Tooltip("How far ahead along the dribble line (m) the bot steers. Larger = smoother convergence onto the line.")]
    public float lineLookahead = 1.5f;

    [Tooltip("Angle (deg) the bot veers to take the ball around a defender standing in its path.")]
    public float dodgeAngle = 35f;

    [Tooltip("Defenders closer than this (m) ahead of the ball trigger a dodge.")]
    public float dodgeLookahead = 5f;

    [Tooltip("Half-width (m) of the corridor ahead of the ball that is checked for defenders.")]
    public float dodgeCorridor = 1.6f;

    [Header("Shooting")]
    [Tooltip("Shoot when the ball is within this distance (m) of the opponent goal.")]
    public float shootingRange = 20f;

    [Tooltip("Lateral offset (m) from goal centre to aim at. The side away from the nearest defender is chosen.")]
    public float goalAimSpread = 2f;

    [Range(0f, 1f)] public float shotElevation = 0.1f;
    [Range(0f, 1f)] public float shotPower = 1f;

    [Header("Team Play")]
    [Tooltip("Non-chasing teammates hold position this far (m) behind the ball.")]
    public float supportDistance = 8f;

    [Tooltip("Distance bonus (m) that keeps the current chaser chasing, so teammates don't swap roles every frame.")]
    public float chaserStickiness = 1.5f;

    [Header("Pitch (wall positions)")]
    public float pitchHalfLength = 53.5f;
    public float pitchHalfWidth = 35f;

    [Header("Debug (read only)")]
    [SerializeField] private Mode mode = Mode.GetBehindBall;
    [SerializeField] private float angleOffLine;

    /// <summary>True if this bot is the one on its team going for the ball.</summary>
    public bool IsChaser { get; private set; } = true;

    private FootballAgent agent;
    private Rigidbody ballRb;
    private float botRadius = 0.5f;
    private float contactDistance = 0.65f; // bot radius + ball radius (centre-to-centre when touching)

    private float orbitSide = 1f;  // +1 / -1: which way round the ball we are circling (latched)
    private float dodgeSide = 0f;  // +1 / -1: which way we veer around a defender (latched), 0 = none
    private bool warnedMissingGoal;

    private readonly List<FootballAgent> teammates = new List<FootballAgent>();
    private readonly List<TestBrain> teammateBrains = new List<TestBrain>();
    private readonly List<FootballAgent> opponents = new List<FootballAgent>();
    private float nextRosterRefresh;

    // Gizmo debug data
    private Vector3 debugTarget, debugBall, debugDribbleDir;

    private void Awake()
    {
        agent = GetComponent<FootballAgent>();

        CapsuleCollider cap = GetComponent<CapsuleCollider>();
        if (cap != null)
        {
            Vector3 s = transform.lossyScale;
            botRadius = cap.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
        }
    }

    private void FixedUpdate()
    {
        if (!EnsureReferences()) return;

        if (Time.time >= nextRosterRefresh)
        {
            RefreshRoster();
            nextRosterRefresh = Time.time + 1f;
        }

        Vector3 bot = Flat(transform.position);
        Vector3 ballNow = Flat(targetBall.position);
        Vector3 ball = PredictBall(bot, ballNow);
        Vector3 goal = Flat(opponentGoal.position);
        debugBall = ball;

        Vector3 attackDir = DirOr(goal - ball, transform.forward);

        // ---- Team coordination: only one bot per team chases the ball ----
        IsChaser = ComputeIsChaser(bot, ballNow);
        if (!IsChaser)
        {
            mode = Mode.Support;
            dodgeSide = 0f;
            MoveTo(bot, SupportSpot(ball, attackDir), true);
            return;
        }

        // ---- Work out the dribble line (goal direction, adjusted for defenders and walls) ----
        Vector3 dribbleDir = attackDir;
        if (Vector3.Distance(bot, ballNow) < 3f) dribbleDir = ApplyDodge(ball, attackDir);
        else dodgeSide = 0f;
        dribbleDir = KeepLineInsidePitch(ball, dribbleDir);
        debugDribbleDir = dribbleDir;

        // Angle between "where the bot is" and "the ideal spot directly behind the ball".
        // 0 deg = perfectly behind the ball, 180 deg = standing between ball and target.
        Vector3 rel = bot - ball;
        angleOffLine = rel.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(-dribbleDir, rel, Vector3.up) : 0f;
        float absAngle = Mathf.Abs(angleOffLine);

        // ---- Mode selection with hysteresis (enter at 15 deg, leave at 40 deg) ----
        Mode previous = mode;
        if (mode == Mode.Dribble)
        {
            if (absAngle > dribbleExitAngle) mode = Mode.GetBehindBall;
        }
        else
        {
            mode = absAngle < dribbleEnterAngle ? Mode.Dribble : Mode.GetBehindBall;
        }

        if (mode == Mode.GetBehindBall && previous != Mode.GetBehindBall)
        {
            orbitSide = angleOffLine >= 0f ? 1f : -1f; // circle round the shorter way
        }

        // ---- Act ----
        if (mode == Mode.Dribble)
        {
            TryShoot(bot, ballNow, goal, attackDir);
            MoveTo(bot, DribbleTarget(bot, ball, dribbleDir), false);
        }
        else
        {
            MoveTo(bot, OrbitTarget(ball, dribbleDir), false);
        }
    }

    // =====================================================================
    // Behaviours
    // =====================================================================

    /// <summary>
    /// Waypoint on a circle around the ball, stepped toward the spot directly behind it.
    /// The circle radius keeps the body clear of the ball, and moving between circle points is
    /// mostly sideways relative to the ball, so FootballAgent never taps it during the approach.
    /// </summary>
    private Vector3 OrbitTarget(Vector3 ball, Vector3 dribbleDir)
    {
        float angle = angleOffLine;

        if (Mathf.Abs(angle) < 150f)
        {
            if (Mathf.Abs(angle) > 1f) orbitSide = Mathf.Sign(angle);
        }
        else
        {
            // Near the front of the ball: keep circling the way we already were (no flip-flopping)
            if (orbitSide < 0f && angle > 0f) angle -= 360f;
            else if (orbitSide > 0f && angle < 0f) angle += 360f;
        }

        float step = Mathf.Min(Mathf.Abs(angle), orbitStepAngle);
        float nextAngle = angle - Mathf.Sign(angle) * step;
        float radius = contactDistance + orbitClearance;

        Vector3 waypoint = ball + Quaternion.AngleAxis(nextAngle, Vector3.up) * (-dribbleDir) * radius;
        return ClampInsidePitch(waypoint);
    }

    /// <summary>
    /// Steer onto the dribble line and run through the ball along it.
    /// The target is a point on the line a little ahead of the bot, capped at 1 m past the ball,
    /// so the bot is moving along the line (not across it) when it reaches the ball.
    /// </summary>
    private Vector3 DribbleTarget(Vector3 bot, Vector3 ball, Vector3 dribbleDir)
    {
        float along = Vector3.Dot(bot - ball, dribbleDir); // negative = behind the ball
        float onLine = Mathf.Min(along + lineLookahead, 1f);
        return ball + dribbleDir * onLine;
    }

    private bool TryShoot(Vector3 bot, Vector3 ballNow, Vector3 goal, Vector3 attackDir)
    {
        if (Vector3.Distance(ballNow, goal) > shootingRange || !agent.CanKickBall()) return false;

        Vector3 forward = DirOr(transform.forward, attackDir);
        Vector3 toBall = ballNow - bot;
        if (toBall.sqrMagnitude < 1e-4f || Vector3.Dot(forward, toBall.normalized) < 0.75f) return false;

        Vector3 aim = ChooseShotTarget(bot, goal, attackDir);
        Vector3 aimDir = DirOr(aim - ballNow, attackDir);
        if (Vector3.Dot(forward, aimDir) < 0.85f) return false;

        agent.KickTowards(aim, shotElevation, shotPower);
        return true;
    }

    private Vector3 ChooseShotTarget(Vector3 bot, Vector3 goal, Vector3 attackDir)
    {
        Vector3 lateral = Vector3.Cross(Vector3.up, attackDir).normalized;

        // Treat the opponent nearest the goal (within 15 m) as the keeper and shoot away from them
        FootballAgent keeper = null;
        float best = 15f;
        foreach (FootballAgent opp in opponents)
        {
            if (opp == null || !opp.isActiveAndEnabled) continue;
            float d = Vector3.Distance(Flat(opp.transform.position), goal);
            if (d < best) { best = d; keeper = opp; }
        }

        float side = keeper != null
            ? -SignNonZero(Vector3.Dot(Flat(keeper.transform.position) - goal, lateral))
            : -SignNonZero(Vector3.Dot(bot - goal, lateral)); // otherwise aim at the far post

        return goal + lateral * side * goalAimSpread;
    }

    /// <summary>
    /// If a defender stands in the corridor ahead of the ball, veer the dribble line to the
    /// side away from them (latched until the corridor is clear).
    /// </summary>
    private Vector3 ApplyDodge(Vector3 ball, Vector3 attackDir)
    {
        Vector3 lateral = Vector3.Cross(Vector3.up, attackDir);
        bool threat = false;
        float nearestAhead = float.MaxValue;
        float threatLateral = 0f;

        foreach (FootballAgent opp in opponents)
        {
            if (opp == null || !opp.isActiveAndEnabled) continue;
            Vector3 r = Flat(opp.transform.position) - ball;
            float ahead = Vector3.Dot(r, attackDir);
            float lat = Vector3.Dot(r, lateral);
            if (ahead > -0.3f && ahead < dodgeLookahead && Mathf.Abs(lat) < dodgeCorridor && ahead < nearestAhead)
            {
                threat = true;
                nearestAhead = ahead;
                threatLateral = lat;
            }
        }

        if (!threat)
        {
            dodgeSide = 0f;
            return attackDir;
        }

        if (dodgeSide == 0f)
        {
            dodgeSide = threatLateral >= 0f ? -1f : 1f;

            // Don't dodge into a wall
            Vector3 probe = ball + Quaternion.AngleAxis(dodgeSide * dodgeAngle, Vector3.up) * attackDir * 4f;
            if (Mathf.Abs(probe.x) > pitchHalfLength - 1f || Mathf.Abs(probe.z) > pitchHalfWidth - 1f)
            {
                dodgeSide = -dodgeSide;
            }
        }

        return Quaternion.AngleAxis(dodgeSide * dodgeAngle, Vector3.up) * attackDir;
    }

    /// <summary>
    /// When the ball is near a wall, the spot "behind" it can be outside the pitch, which the bot
    /// can never reach. Bend the dribble line so the behind-spot stays reachable (the ball gets
    /// dragged along the wall until it is clear, then the line points back at the goal).
    /// </summary>
    private Vector3 KeepLineInsidePitch(Vector3 ball, Vector3 d)
    {
        float r = contactDistance + orbitClearance;
        float limX = pitchHalfLength - botRadius;
        float limZ = pitchHalfWidth - botRadius;

        // behind = ball - d * r must satisfy |behind.x| <= limX and |behind.z| <= limZ
        float x = Mathf.Clamp(d.x, (ball.x - limX) / r, (ball.x + limX) / r);
        float z = Mathf.Clamp(d.z, (ball.z - limZ) / r, (ball.z + limZ) / r);

        bool clampedX = !Mathf.Approximately(x, d.x);
        bool clampedZ = !Mathf.Approximately(z, d.z);

        if (clampedX && !clampedZ)
            z = SignOrTowardCentre(d.z, ball.z) * Mathf.Sqrt(Mathf.Max(0f, 1f - x * x));
        else if (clampedZ && !clampedX)
            x = SignOrTowardCentre(d.x, ball.x) * Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));

        Vector3 result = new Vector3(x, 0f, z);
        return result.sqrMagnitude > 1e-4f ? result.normalized : DirOr(-ball, d);
    }

    private Vector3 SupportSpot(Vector3 ball, Vector3 attackDir)
    {
        // Hang back behind the ball, shifted toward the middle of the pitch
        Vector3 spot = ball - attackDir * supportDistance;
        spot.z += -SignNonZero(ball.z) * 6f;
        return ClampInsidePitch(spot);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private void MoveTo(Vector3 bot, Vector3 target, bool arrive)
    {
        debugTarget = target;
        Vector3 to = target - bot;
        to.y = 0f;
        float dist = to.magnitude;

        if (dist < 0.1f)
        {
            agent.ApplyMovement(Vector2.zero);
            return;
        }

        float speed = arrive ? Mathf.Clamp01(dist / 2f) : 1f;
        Vector3 dir = (to / dist) * speed;
        agent.ApplyMovement(new Vector2(dir.x, dir.z));
    }

    /// <summary>
    /// Where the ball will be by the time we reach it, using the exact solution for Unity's
    /// linear damping: displacement = v * (1 - e^(-k t)) / k.
    /// </summary>
    private Vector3 PredictBall(Vector3 bot, Vector3 ballNow)
    {
        if (ballRb == null) return ballNow;

        Vector3 v = Flat(ballRb.linearVelocity);
        float speed = Mathf.Max(agent.CurrentMaxVelocity, 1f);
        float t = Mathf.Clamp(Vector3.Distance(bot, ballNow) / speed, 0f, 1.5f);
        float k = ballRb.linearDamping;
        float travel = k > 0.01f ? (1f - Mathf.Exp(-k * t)) / k : t;

        return ClampInsidePitch(ballNow + v * travel);
    }

    private bool ComputeIsChaser(Vector3 bot, Vector3 ballNow)
    {
        float mine = Vector3.Distance(bot, ballNow) - (IsChaser ? chaserStickiness : 0f);

        for (int i = 0; i < teammates.Count; i++)
        {
            FootballAgent mate = teammates[i];
            if (mate == null || !mate.isActiveAndEnabled) continue;

            TestBrain mateBrain = teammateBrains[i];
            float theirs = Vector3.Distance(Flat(mate.transform.position), ballNow);
            if (mateBrain != null && mateBrain.enabled && mateBrain.IsChaser) theirs -= chaserStickiness;

            if (theirs < mine - 0.01f) return false;
            if (Mathf.Abs(theirs - mine) <= 0.01f && mate.GetInstanceID() < agent.GetInstanceID()) return false;
        }
        return true;
    }

    /// <summary>
    /// Sorts every other FootballAgent into teammates (attacking the same goal) and opponents.
    /// </summary>
    private void RefreshRoster()
    {
        teammates.Clear();
        teammateBrains.Clear();
        opponents.Clear();

        foreach (FootballAgent other in FindObjectsByType<FootballAgent>(FindObjectsSortMode.None))
        {
            if (other == agent) continue;

            Transform theirTarget = null;
            TestBrain brain = other.GetComponent<TestBrain>();
            if (brain != null && brain.enabled) theirTarget = brain.opponentGoal;

            if (theirTarget == null)
            {
                FootballAgentML ml = other.GetComponent<FootballAgentML>();
                if (ml != null) theirTarget = ml.opponentGoal;
            }

            if (theirTarget != null && theirTarget == opponentGoal)
            {
                teammates.Add(other);
                teammateBrains.Add(brain);
            }
            else
            {
                opponents.Add(other);
            }
        }
    }

    private bool EnsureReferences()
    {
        if (agent == null) return false;

        if (targetBall == null)
        {
            GameObject b = GameObject.FindWithTag("Ball");
            if (b == null) b = GameObject.Find("GlowBall");
            if (b != null) { targetBall = b.transform; ballRb = null; }
        }

        if (targetBall != null && ballRb == null)
        {
            ballRb = targetBall.GetComponent<Rigidbody>();

            float ballRadius = 0.15f;
            SphereCollider sc = targetBall.GetComponent<SphereCollider>();
            if (sc != null)
            {
                Vector3 s = targetBall.lossyScale;
                ballRadius = sc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            }
            contactDistance = botRadius + ballRadius;
        }

        if (opponentGoal == null)
        {
            if (!warnedMissingGoal)
            {
                Debug.LogWarning($"[TestBrain] '{name}' has no Opponent Goal assigned - it will stand still.", this);
                warnedMissingGoal = true;
            }
            return false;
        }

        return targetBall != null;
    }

    private Vector3 ClampInsidePitch(Vector3 p)
    {
        float limX = pitchHalfLength - 1f;
        float limZ = pitchHalfWidth - 1f;
        p.x = Mathf.Clamp(p.x, -limX, limX);
        p.z = Mathf.Clamp(p.z, -limZ, limZ);
        return p;
    }

    private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    private static Vector3 DirOr(Vector3 v, Vector3 fallback)
    {
        v.y = 0f;
        if (v.sqrMagnitude > 1e-4f) return v.normalized;
        fallback.y = 0f;
        return fallback.sqrMagnitude > 1e-4f ? fallback.normalized : Vector3.forward;
    }

    private static float SignNonZero(float v) => v >= 0f ? 1f : -1f;

    private static float SignOrTowardCentre(float component, float position) =>
        Mathf.Abs(component) > 0.05f ? Mathf.Sign(component) : -SignNonZero(position);

    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;

        Gizmos.color = mode == Mode.Dribble ? Color.green : mode == Mode.Support ? Color.gray : Color.cyan;
        Gizmos.DrawLine(transform.position, debugTarget + Vector3.up * 0.1f);
        Gizmos.DrawWireSphere(debugTarget + Vector3.up * 0.1f, 0.3f);

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(debugBall + Vector3.up * 0.1f, debugBall + Vector3.up * 0.1f + debugDribbleDir * 3f);
        Gizmos.DrawWireSphere(debugBall + Vector3.up * 0.1f, contactDistance + orbitClearance);
    }
}