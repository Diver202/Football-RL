using System.Collections.Generic;
using UnityEngine;

/// <summary>How a team is coached. Set on MatchController, or with the command line "-coach off|scripted|learned".</summary>
public enum CoachMode
{
    /// <summary>No coach: players get zeros for the coach inputs and play as before.</summary>
    Off,
    /// <summary>A scripted coach picks a random formation every 2-5 s (phase 1: players learn to follow roles).</summary>
    Scripted,
    /// <summary>A learning coach agent (behavior "Coordinator") picks the formation every 0.5 s (phase 2).</summary>
    Learned
}

/// <summary>
/// One team's coach state (plain C#, not a component). It holds the current FORMATION and turns it into
/// an instruction for every player:
///   - the CHASER (the team's ball carrier, otherwise the player closest to the ball) goes for the ball,
///   - the other players each get a SLOT: a spot on the pitch, defined relative to the ball, that moves
///     with the ball.
/// Players see their instruction as 3 extra observations and are rewarded for being in their slot
/// (see FootballAgentML). Who picks the formation depends on CoachMode.
///
/// Formations (2 slots each; "ahead" = towards the goal the team attacks, "across" = sideways):
///   0 Wide      both 4 m ahead of the ball, 14 m to either side
///   1 Attack    both 16 m ahead, 9 m to either side (in position for a through pass)
///   2 Balanced  one 6 m ahead and 12 m towards the middle of the pitch, one covering 40% of the way back to own goal
///   3 Defend    both 45% of the way back to own goal, 8 m to either side
///   4 Press     both right next to the ball (2 m behind it, 4 m to either side)
/// </summary>
public class TeamCoach
{
    public const int FormationCount = 5;
    public const int DefaultFormation = 2;   // Balanced
    public static readonly string[] FormationNames = { "Wide", "Attack", "Balanced", "Defend", "Press" };

    private readonly MatchController match;
    private readonly List<FootballAgentML> players;
    private readonly Transform attackGoal;
    private readonly Transform defendGoal;

    public int Formation { get; private set; } = DefaultFormation;
    public FootballAgentML Chaser { get; private set; }

    // Per player: slot index (-1 = chaser) and a version that changes whenever the player's target jumps
    // (new role, new slot or new formation), so the "move towards your slot" reward is not paid for a jump.
    private readonly Dictionary<FootballAgentML, int> slotIndex = new Dictionary<FootballAgentML, int>();
    private readonly Dictionary<FootballAgentML, int> version = new Dictionary<FootballAgentML, int>();
    private int assignedFormation = -1;
    private int nextScriptedChange;

    // Statistics for the current match
    public int Ticks { get; private set; }
    public readonly int[] FormationTicks = new int[FormationCount];
    public int SupportSteps { get; private set; }
    public int SupportInSlotSteps { get; private set; }

    public TeamCoach(MatchController match, List<FootballAgentML> players, Transform attackGoal, Transform defendGoal)
    {
        this.match = match;
        this.players = players;
        this.attackGoal = attackGoal;
        this.defendGoal = defendGoal;
        foreach (var p in players)
        {
            if (p == null) continue;
            slotIndex[p] = 0;
            version[p] = 0;
        }
    }

    public IReadOnlyList<FootballAgentML> Players => players;

    /// <summary>At kick-off.</summary>
    public void ResetForKickoff(CoachMode mode, int step)
    {
        Formation = mode == CoachMode.Scripted ? Random.Range(0, FormationCount) : DefaultFormation;
        nextScriptedChange = step + ScriptedInterval();
        Ticks = 0;
        System.Array.Clear(FormationTicks, 0, FormationTicks.Length);
        SupportSteps = SupportInSlotSteps = 0;
        Assign(null);
    }

    /// <summary>The learned coach's choice.</summary>
    public void SetFormation(int formation) => Formation = Mathf.Clamp(formation, 0, FormationCount - 1);

    /// <summary>Every coach period (0.5 s): scripted formation change if due, then re-assign roles.</summary>
    public void Tick(CoachMode mode, int step)
    {
        if (mode == CoachMode.Scripted && step >= nextScriptedChange)
        {
            Formation = Random.Range(0, FormationCount);
            nextScriptedChange = step + ScriptedInterval();
        }
        Assign(null);
        Ticks++;
        FormationTicks[Formation]++;
    }

    /// <summary>Every physics step: the team's ball carrier is always the chaser; also counts role adherence.</summary>
    public void Step()
    {
        FootballAgent carrier = FootballAgent.activeBallCarrier;
        if (carrier != null && (Chaser == null || Chaser.Body != carrier))
        {
            foreach (var p in players)
                if (p != null && p.Body == carrier) { Assign(p); break; }
        }
        foreach (var p in players)
        {
            if (p == null || p == Chaser) continue;
            SupportSteps++;
            if (FlatDistance(p.transform.position, SlotOf(p)) < match.roleRadius) SupportInSlotSteps++;
        }
    }

    public bool IsChaser(FootballAgentML p) => p != null && p == Chaser;

    /// <summary>Where `p` should be: the ball for the chaser, its slot otherwise (y = 0).</summary>
    public Vector3 SlotOf(FootballAgentML p)
    {
        if (p == Chaser || !slotIndex.TryGetValue(p, out int i) || i < 0) return Flat(match.ball.position);
        return SlotPosition(Formation, i);
    }

    public int VersionOf(FootballAgentML p) => version.TryGetValue(p, out int v) ? v : 0;

    // ------------------------------------------------------------------------------------------

    private static int ScriptedInterval() => Random.Range(100, 251);   // 2-5 s

    private void Assign(FootballAgentML forcedChaser)
    {
        FootballAgentML chaser = forcedChaser != null ? forcedChaser : (OwnCarrier() ?? ClosestToBall());

        // The other players go to the formation's slots; of the possible pairings keep the one with
        // the shortest total distance, so players do not cross over each other.
        var others = new List<FootballAgentML>(2);
        foreach (var p in players) if (p != null && p != chaser) others.Add(p);
        var newIndex = new Dictionary<FootballAgentML, int>();
        if (chaser != null) newIndex[chaser] = -1;
        if (others.Count == 1)
        {
            newIndex[others[0]] = Distance(others[0], 0) <= Distance(others[0], 1) ? 0 : 1;
        }
        else if (others.Count >= 2)
        {
            float straight = Distance(others[0], 0) + Distance(others[1], 1);
            float swapped = Distance(others[0], 1) + Distance(others[1], 0);
            bool swap = swapped < straight;
            newIndex[others[0]] = swap ? 1 : 0;
            newIndex[others[1]] = swap ? 0 : 1;
            for (int k = 2; k < others.Count; k++) newIndex[others[k]] = k % 2;   // more than 3 players: share slots
        }

        bool formationChanged = Formation != assignedFormation;
        foreach (var kv in newIndex)
        {
            int old = slotIndex.TryGetValue(kv.Key, out int o) ? o : int.MinValue;
            if (old != kv.Value || (formationChanged && kv.Value >= 0))
                version[kv.Key] = VersionOf(kv.Key) + 1;
            slotIndex[kv.Key] = kv.Value;
        }
        assignedFormation = Formation;
        Chaser = chaser;
    }

    private float Distance(FootballAgentML p, int slot) => FlatDistance(p.transform.position, SlotPosition(Formation, slot));

    private FootballAgentML OwnCarrier()
    {
        FootballAgent carrier = FootballAgent.activeBallCarrier;
        if (carrier == null) return null;
        foreach (var p in players) if (p != null && p.Body == carrier) return p;
        return null;
    }

    private FootballAgentML ClosestToBall()
    {
        FootballAgentML best = null;
        float bestD = float.PositiveInfinity;
        foreach (var p in players)
        {
            if (p == null || !p.gameObject.activeInHierarchy) continue;
            float d = FlatDistance(p.transform.position, match.ball.position);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    /// <summary>World position (y = 0) of slot 0 or 1 of a formation, relative to the ball, kept on the pitch.</summary>
    public Vector3 SlotPosition(int formation, int slot)
    {
        Vector3 b = Flat(match.ball.position);
        Vector3 own = Flat(defendGoal.position);
        Vector3 ahead = new Vector3(Mathf.Sign(attackGoal.position.x - defendGoal.position.x), 0f, 0f);
        Vector3 across = Vector3.forward;
        float side = slot == 0 ? 1f : -1f;
        float towardsMiddle = b.z > 0f ? -1f : 1f;

        Vector3 p;
        switch (formation)
        {
            case 0: p = b + ahead * 4f + across * (14f * side); break;                       // Wide
            case 1: p = b + ahead * 16f + across * (9f * side); break;                       // Attack
            case 2:                                                                          // Balanced
                p = slot == 0 ? b + ahead * 6f + across * (12f * towardsMiddle)
                              : Vector3.Lerp(b, own, 0.4f);
                break;
            case 3: p = Vector3.Lerp(b, own, 0.45f) + across * (8f * side); break;           // Defend
            default: p = b - ahead * 2f + across * (4f * side); break;                       // Press
        }
        p.x = Mathf.Clamp(p.x, -match.pitchHalfLength, match.pitchHalfLength);
        p.z = Mathf.Clamp(p.z, -match.pitchHalfWidth, match.pitchHalfWidth);
        p.y = 0f;
        return p;
    }

    private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    public static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
