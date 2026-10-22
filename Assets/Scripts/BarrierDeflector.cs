using UnityEngine;

/// <summary>
/// Attached to barrier walls (or parent Barriers) to deflect the ball at considerable speed
/// back into the field and break any scrums.
/// </summary>
public class BarrierDeflector : MonoBehaviour
{
    public float deflectionSpeed = 20f;
    private static float lastDeflectTime = -1f;

    private void OnCollisionEnter(Collision collision)
    {
        HandleHit(collision.gameObject);
    }

    private void OnCollisionStay(Collision collision)
    {
        HandleHit(collision.gameObject);
    }

    private void HandleHit(GameObject hitObj)
    {
        if (hitObj.CompareTag("Ball") || hitObj.name.Contains("Ball"))
        {
            DeflectBall(hitObj);
        }
        else
        {
            FootballAgent agent = hitObj.GetComponent<FootballAgent>();
            if (agent != null && agent.HasPossession)
            {
                agent.LosePossession();
                agent.SetPossessionCooldown(1.0f);
            }
        }
    }

    private void DeflectBall(GameObject ball)
    {
        if (Time.time - lastDeflectTime < 0.1f) return;
        lastDeflectTime = Time.time;

        // Strip possession if held
        FootballAgent[] agents = FindObjectsByType<FootballAgent>(FindObjectsSortMode.None);
        foreach (var agent in agents)
        {
            if (agent.HasPossession)
            {
                agent.LosePossession();
            }
            agent.SetPossessionCooldown(1.0f);
        }

        Rigidbody rb = ball.GetComponent<Rigidbody>();
        if (rb == null) return;

        rb.isKinematic = false;

        // Inward direction from the wall to field center
        Vector3 inward = GetInwardDirection(transform, ball.transform.position);

        // Reflect velocity horizontally
        Vector3 incomingHoriz = new Vector3(rb.linearVelocity.x, 0, rb.linearVelocity.z);
        Vector3 reflect = Vector3.Reflect(incomingHoriz, inward);
        reflect.y = 0;

        Vector3 outDir = (reflect.normalized + inward * 0.6f).normalized;
        outDir.y = 0;

        // Launch ball back into the field at considerable speed
        rb.linearVelocity = outDir * deflectionSpeed;

        // Nudge position inward away from the wall
        Vector3 p = ball.transform.position;
        ball.transform.position = new Vector3(p.x + inward.x * 0.5f, 0.15f, p.z + inward.z * 0.5f);
    }

    private Vector3 GetInwardDirection(Transform wall, Vector3 ballPos)
    {
        Vector3 p = wall.position;
        if (Mathf.Abs(p.x) > 10f || Mathf.Abs(p.z) > 10f)
        {
            if (Mathf.Abs(p.x) > Mathf.Abs(p.z))
                return p.x > 0 ? new Vector3(-1, 0, 0) : new Vector3(1, 0, 0);
            else
                return p.z > 0 ? new Vector3(0, 0, -1) : new Vector3(0, 0, 1);
        }

        if (Mathf.Abs(ballPos.x) > Mathf.Abs(ballPos.z))
            return ballPos.x > 0 ? new Vector3(-1, 0, 0) : new Vector3(1, 0, 0);
        else
            return ballPos.z > 0 ? new Vector3(0, 0, -1) : new Vector3(0, 0, 1);
    }
}

public class BarrierWallContact : MonoBehaviour
{
    [HideInInspector]
    public BarrierDeflector parentDeflector;
}
