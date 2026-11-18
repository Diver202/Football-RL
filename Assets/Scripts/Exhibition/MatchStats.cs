using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Records what happens in an exhibition match, for the full-time breakdown.
/// ExhibitionMatch starts and stops it; it only reads the game (it never changes anything).
///
/// Per team: possession time, passes, interceptions, tackles, shots, time with the ball in the
/// opponents' half, distance covered by the players, and (for coached teams) how long each formation
/// was used. Goals come from GameManager.
///   pass         = a teammate gets the ball after it was kicked or left loose
///   interception = an opponent gets a loose / kicked ball
///   tackle       = an opponent takes the ball straight off the carrier
///   shot         = a power kick aimed at the opponents' goal mouth, from within shotRange metres
/// </summary>
public class MatchStats : MonoBehaviour
{
    public class TeamStats
    {
        public int goals, shots, passes, interceptions, tackles;
        public float possessionTime, attackingHalfTime, distance;
        public bool coached;
        public readonly float[] formationTime = new float[TeamCoach.FormationCount];

        public int MainFormation()
        {
            int best = -1;
            float bestTime = 0f;
            for (int f = 0; f < formationTime.Length; f++)
                if (formationTime[f] > bestTime) { bestTime = formationTime[f]; best = f; }
            return best;
        }
    }

    [Tooltip("A power kick counts as a shot if it is aimed at the goal mouth from within this distance (m).")]
    public float shotRange = 45f;
    [Tooltip("Extra width (m) either side of the goal mouth that still counts as 'aimed at goal'.")]
    public float shotMargin = 1.5f;

    public TeamStats Red { get; private set; } = new TeamStats();
    public TeamStats Blue { get; private set; } = new TeamStats();
    public float Duration { get; private set; }

    private MatchController match;
    private bool running;
    private readonly Dictionary<FootballAgent, int> teamOfBody = new Dictionary<FootballAgent, int>();
    private readonly Dictionary<Transform, Vector3> lastPosition = new Dictionary<Transform, Vector3>();
    private FootballAgent lastCarrier;
    private bool looseSinceLastCarrier;

    private void OnEnable()
    {
        FootballAgent.OnKick += HandleKick;
        GameManager.OnGoalScoredEvent += HandleGoal;
    }

    private void OnDisable()
    {
        FootballAgent.OnKick -= HandleKick;
        GameManager.OnGoalScoredEvent -= HandleGoal;
    }

    public void BeginMatch(MatchController controller, bool redCoached, bool blueCoached)
    {
        match = controller;
        Red = new TeamStats { coached = redCoached };
        Blue = new TeamStats { coached = blueCoached };
        Duration = 0f;
        teamOfBody.Clear();
        foreach (var a in match.redTeam) if (a != null && a.Body != null) teamOfBody[a.Body] = 0;
        foreach (var a in match.blueTeam) if (a != null && a.Body != null) teamOfBody[a.Body] = 1;
        ResetTracking();
        running = true;
    }

    public void EndMatch()
    {
        running = false;
        if (GameManager.Instance != null)
        {
            Red.goals = GameManager.Instance.teamRedScore;
            Blue.goals = GameManager.Instance.teamBlueScore;
        }
    }

    private void ResetTracking()
    {
        lastCarrier = null;
        looseSinceLastCarrier = false;
        lastPosition.Clear();
        if (match == null) return;
        foreach (var a in match.redTeam) if (a != null) lastPosition[a.transform] = a.transform.position;
        foreach (var a in match.blueTeam) if (a != null) lastPosition[a.transform] = a.transform.position;
    }

    private void FixedUpdate()
    {
        if (!running || match == null) return;
        float dt = Time.fixedDeltaTime;
        Duration += dt;

        // Possession and passes / interceptions / tackles
        FootballAgent carrier = FootballAgent.activeBallCarrier;
        if (carrier == null)
        {
            if (lastCarrier != null) looseSinceLastCarrier = true;
        }
        else if (teamOfBody.TryGetValue(carrier, out int team))
        {
            Of(team).possessionTime += dt;
            if (carrier != lastCarrier && lastCarrier != null && teamOfBody.TryGetValue(lastCarrier, out int oldTeam))
            {
                if (team == oldTeam) { if (looseSinceLastCarrier) Of(team).passes++; }
                else if (looseSinceLastCarrier) Of(team).interceptions++;
                else Of(team).tackles++;
            }
            lastCarrier = carrier;
            looseSinceLastCarrier = false;
        }

        // Territory: which half the ball is in (Red attacks the Blue goal)
        if (match.ball != null && match.redGoal != null && match.blueGoal != null)
        {
            float redAttack = Mathf.Sign(match.blueGoal.position.x - match.redGoal.position.x);
            float x = match.ball.position.x;
            if (x * redAttack > 0f) Red.attackingHalfTime += dt;
            else if (x * redAttack < 0f) Blue.attackingHalfTime += dt;
        }

        // Distance covered (kick-off resets are not counted)
        AddDistance(match.redTeam, Red);
        AddDistance(match.blueTeam, Blue);

        // Formations used by the coaches
        AddFormation(match.redTeam, Red);
        AddFormation(match.blueTeam, Blue);
    }

    private void AddDistance(List<FootballAgentML> team, TeamStats stats)
    {
        foreach (var a in team)
        {
            if (a == null) continue;
            Vector3 now = a.transform.position;
            if (lastPosition.TryGetValue(a.transform, out Vector3 before))
            {
                Vector3 d = now - before;
                d.y = 0f;
                float m = d.magnitude;
                if (m < 2f) stats.distance += m;   // larger jumps are kick-off resets
            }
            lastPosition[a.transform] = now;
        }
    }

    private void AddFormation(List<FootballAgentML> team, TeamStats stats)
    {
        if (!stats.coached || team.Count == 0) return;
        TeamCoach coach = match.CoachOf(team[0]);
        if (coach != null) stats.formationTime[coach.Formation] += Time.fixedDeltaTime;
    }

    private void HandleKick(FootballAgent kicker, Vector3 direction)
    {
        if (!running || match == null || !teamOfBody.TryGetValue(kicker, out int team)) return;
        Transform goal = team == 0 ? match.blueGoal : match.redGoal;
        if (goal == null || match.ball == null) return;

        Vector3 from = match.ball.position;
        Vector3 toGoal = goal.position - from;
        toGoal.y = 0f;
        if (toGoal.magnitude > shotRange || Mathf.Abs(direction.x) < 1e-3f) return;
        if (Vector3.Dot(direction, toGoal) <= 0f) return;

        // Where the kick crosses the goal line, compared with the goal mouth (the goal trigger's width)
        float t = (goal.position.x - from.x) / direction.x;
        float zAtGoal = from.z + direction.z * t;
        float halfWidth = 3f;
        Collider c = goal.GetComponent<Collider>();
        if (c != null) halfWidth = c.bounds.extents.z;
        if (Mathf.Abs(zAtGoal - goal.position.z) <= halfWidth + shotMargin) Of(team).shots++;
    }

    private void HandleGoal(string scoringTeam)
    {
        // Kick-off follows: the next touch is not a pass or an interception.
        ResetTracking();
    }

    private TeamStats Of(int team) => team == 0 ? Red : Blue;
}
