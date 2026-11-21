using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class SoccerAI : MonoBehaviour
{
    public enum Team { Blue, Red }

    public enum Position
    {
        GK,
        LB, CB_Left, CB_Right, RB,
        CM_Left, CDM, CM_Right,
        LW, ST, RW
    }

    [Header("Team & Tactical Position")]
    public Team team = Team.Blue;
    public Position tacticalPosition = Position.CM_Right;
    public Transform targetGoal;
    public Transform defendGoal;

    [Header("Kick, Pass, Cross & Dribble Attributes")]
    public float kickForce = 16f;
    public float passForceMultiplier = 0.75f;
    public float throughBallForceMultiplier = 1.05f;
    public float crossForceMultiplier = 1.12f;
    public float dribbleForce = 4.2f;
    public float shootDistance = 18f;
    public float pressureRadius = 4.5f;
    public float kickCooldown = 0.6f;
    public float dribbleCooldown = 0.28f;
    public float kickRange = 1.45f;

    [Header("Anti-Stalemate Settings")]
    public float stalemateTimeThreshold = 0.6f;

    private Vector3 homePosition;
    private Quaternion homeRotation;
    private NavMeshAgent agent;
    private Transform ball;
    private Rigidbody ballRb;
    private float nextActionTime;
    private float approachAngleOffset;
    private float stuckTimer = 0f;
    private float halfFieldLength;
    private float halfFieldWidth;
    private float roamSeed;

    private Vector3 incomingPassTarget;
    private float chasePassUntilTime = 0f;

    private SoccerAI[] allPlayers;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        allPlayers = FindObjectsByType<SoccerAI>(FindObjectsSortMode.None);

        agent.speed *= Random.Range(0.92f, 1.14f);
        agent.acceleration *= Random.Range(0.9f, 1.2f);
        agent.avoidancePriority = Random.Range(20, 80);
        agent.stoppingDistance = 0.25f;

        if (IsWinger())
        {
            agent.speed *= 1.06f;
        }

        approachAngleOffset = Random.Range(-0.35f, 0.35f);
        roamSeed = Random.Range(0f, 100f);

        GameObject ballObj = GameObject.FindGameObjectWithTag("Ball");
        if (ballObj != null)
        {
            ball = ballObj.transform;
            ballRb = ballObj.GetComponent<Rigidbody>();
        }

        homePosition = transform.position;
        homeRotation = transform.rotation;

        halfFieldLength = Mathf.Abs(targetGoal.position.x);
        halfFieldWidth = halfFieldLength * 0.62f;
    }

    void Update()
    {
        if (ball == null) return;

        if (MatchManager.Instance != null && MatchManager.Instance.isThrowInActive)
        {
            UpdateDuringThrowIn();
            return;
        }

        UpdateStalemateTimer();

        if (tacticalPosition == Position.GK)
        {
            UpdateGoalie();
        }
        else
        {
            UpdateOutfieldPlayer();
        }

        TryInteractWithBall();
    }

    void UpdateDuringThrowIn()
    {
        if (MatchManager.Instance.designatedThrower == this)
        {
            agent.ResetPath();
            return;
        }

        Vector3 targetSpot = (tacticalPosition == Position.GK) ? homePosition : GetDynamicFormationSpot();
        float distToBall = Vector3.Distance(targetSpot, ball.position);

        if (distToBall < 5.5f)
        {
            Vector3 awayFromSideline = (targetSpot - ball.position).normalized;
            awayFromSideline.y = 0f;
            if (awayFromSideline.sqrMagnitude < 0.01f)
                awayFromSideline = new Vector3(0f, 0f, -Mathf.Sign(ball.position.z));

            targetSpot = ball.position + awayFromSideline * 6f;
        }

        agent.SetDestination(targetSpot);
    }

    public void PrepareForThrowIn(Vector3 sidelineSpot, Quaternion facingRotation)
    {
        stuckTimer = 0f;
        chasePassUntilTime = 0f;
        agent.Warp(sidelineSpot);
        agent.ResetPath();
        transform.rotation = facingRotation;
    }

    public void ExecuteThrowIn()
    {
        if (MatchManager.Instance != null)
            MatchManager.Instance.RegisterTouch(this.team);

        nextActionTime = Time.time + kickCooldown;

        SoccerAI receiver = FindBestTeammateToPass(out bool isThroughBall, out Vector3 customTarget);
        Vector3 throwTarget;

        if (receiver != null)
        {
            throwTarget = isThroughBall ? customTarget : receiver.transform.position + (receiver.agent.velocity * 0.3f);
        }
        else
        {
            float forwardDir = Mathf.Sign(targetGoal.position.x);
            float infieldZ = -Mathf.Sign(ball.position.z) * 6f;
            throwTarget = ball.position + new Vector3(forwardDir * 10f, 0f, infieldZ);
        }

        Vector3 throwDir = (throwTarget - ball.position).normalized;
        throwDir.y = 0.25f;

        float dist = Vector3.Distance(ball.position, throwTarget);
        float throwPower = Mathf.Clamp(dist * 0.9f, 7f, kickForce * 0.8f);

        ballRb.AddForce(throwDir.normalized * throwPower, ForceMode.Impulse);
        Debug.DrawLine(ball.position, throwTarget, Color.magenta, 1.5f);
    }

    void UpdateStalemateTimer()
    {
        float distToBall = Vector3.Distance(transform.position, ball.position);
        float ballSpeed = ballRb.linearVelocity.magnitude;

        if (distToBall < kickRange * 1.5f && ballSpeed < 1.5f)
            stuckTimer += Time.deltaTime;
        else
            stuckTimer = 0f;
    }

    void UpdateGoalie()
    {
        float attackDir = Mathf.Sign(targetGoal.position.x);
        bool teamInPossession = (MatchManager.Instance != null && MatchManager.Instance.lastTouchedTeam == this.team);

        float sweeperPush = (teamInPossession && Mathf.Sign(ball.position.x) == attackDir) ? 4.5f : 1.8f;

        float goalWidthClamp = Mathf.Clamp(ball.position.z * 0.35f, -4.5f, 4.5f);
        float offsetX = defendGoal.position.x + (attackDir * sweeperPush);
        Vector3 goalieTarget = new Vector3(offsetX, transform.position.y, goalWidthClamp);

        if (stuckTimer > stalemateTimeThreshold || Vector3.Distance(defendGoal.position, ball.position) < 7.5f)
        {
            agent.SetDestination(ball.position);
        }
        else
        {
            agent.SetDestination(goalieTarget);
        }
    }

    void UpdateOutfieldPlayer()
    {
        // 1. If a teammate just launched a through-ball or cross to us, sprint directly to the landing spot!
        if (Time.time < chasePassUntilTime)
        {
            if (Vector3.Distance(transform.position, ball.position) < 4.5f)
            {
                chasePassUntilTime = 0f;
            }
            else
            {
                agent.SetDestination(incomingPassTarget);
                return;
            }
        }

        // 2. Decide whether to press the ball or make an off-ball tactical run
        if (ShouldPressBall())
        {
            if (stuckTimer > stalemateTimeThreshold)
            {
                float swirlZ = Mathf.Sin(Time.time * 6f + GetInstanceID()) * 1.2f;
                agent.SetDestination(ball.position + new Vector3(0f, 0f, swirlZ));
                return;
            }

            Vector3 desiredTarget = IsWinger() && !IsInCrossingZone()
                ? GetWingerChannelTarget()
                : targetGoal.position;

            Vector3 dirToTarget = (desiredTarget - ball.position).normalized;
            Vector3 perp = new Vector3(-dirToTarget.z, 0f, dirToTarget.x) * approachAngleOffset;
            Vector3 approachSpot = ball.position - (dirToTarget * 0.3f) + perp;

            agent.SetDestination(approachSpot);
        }
        else
        {
            Vector3 tacticalSpot = GetDynamicFormationSpot();
            agent.SetDestination(tacticalSpot);
        }
    }

    public void NotifyIncomingThroughBall(Vector3 landingSpot, float duration)
    {
        incomingPassTarget = landingSpot;
        chasePassUntilTime = Time.time + duration;
    }

    bool ShouldPressBall()
    {
        float myDistToBall = Vector3.Distance(transform.position, ball.position);
        bool teamInPossession = (MatchManager.Instance != null && MatchManager.Instance.lastTouchedTeam == this.team);

        // STRIKER DISCIPLINE:
        // 1. Never chase the ball back into our own defensive third, or when the ball is wide on the wings and a teammate is near it
        if (tacticalPosition == Position.ST)
        {
            float attackDir = Mathf.Sign(targetGoal.position.x);
            bool ballDeepInOurHalf = (ball.position.x * attackDir) < -(halfFieldLength * 0.20f);
            if (ballDeepInOurHalf && myDistToBall > 7f)
                return false;

            // If our team has possession and a Winger or Midfielder is already within 5m of the ball (dribbling),
            // the Striker stays off the ball and makes a forward run into the box instead of crowding them!
            if (teamInPossession && IsTeammateControllingBall() && myDistToBall > 2.2f)
                return false;
        }

        Vector3 currentTacticalAnchor = GetBaseTacticalAnchor();
        float maxLeash = GetRoleLeashRadius();

        if (Vector3.Distance(currentTacticalAnchor, ball.position) > maxLeash && myDistToBall > 9f)
            return false;

        int teammatesCloser = 0;
        foreach (var other in allPlayers)
        {
            if (other == this || other.team != this.team || other.tacticalPosition == Position.GK)
                continue;

            float otherDist = Vector3.Distance(other.transform.position, ball.position);
            if (otherDist < myDistToBall)
            {
                teammatesCloser++;
            }
        }

        // CRITICAL FIX:
        // When OUR team has possession, ONLY the single closest player (teammatesCloser == 0) carries the ball!
        // Teammates never "double-team" their own ball carrier.
        if (teamInPossession)
        {
            return teammatesCloser == 0;
        }

        // When DEFENDING (opponent has the ball), allow a 2nd player (Midfielder/Defender, NOT the Striker) to help press
        if (tacticalPosition == Position.ST)
        {
            // Striker only presses on defense if they are the #1 closest player AND the ball is within 13m
            return teammatesCloser == 0 && myDistToBall < 13f;
        }

        return (teammatesCloser == 0) || (teammatesCloser == 1 && myDistToBall < 8.5f);
    }

    // Checks if a teammate (like a Winger or Midfielder) is already right on the ball dribbling it
    bool IsTeammateControllingBall()
    {
        foreach (var other in allPlayers)
        {
            if (other == this || other.team != this.team || other.tacticalPosition == Position.GK)
                continue;

            if (Vector3.Distance(other.transform.position, ball.position) < 4.2f)
                return true;
        }
        return false;
    }

    float GetRoleLeashRadius()
    {
        // Tighter leash for the Striker (0.48f) so they stay central and upfield instead of chasing all over the pitch
        if (tacticalPosition == Position.ST) return halfFieldLength * 0.48f;
        if (IsWinger()) return halfFieldLength * 0.75f;
        if (IsDefender()) return halfFieldLength * 0.85f;
        return halfFieldLength * 1.1f; // Midfielders are the workhorses
    }

    // Finds the X coordinate of the deepest opposing outfield defender so the Striker can play on their shoulder
    float GetOpponentDefensiveLineX()
    {
        float attackDir = Mathf.Sign(targetGoal.position.x);
        float deepestDefenderX = 0f;
        bool found = false;

        foreach (var opponent in allPlayers)
        {
            if (opponent.team == this.team || opponent.tacticalPosition == Position.GK)
                continue;

            if (!found || (opponent.transform.position.x * attackDir) > (deepestDefenderX * attackDir))
            {
                deepestDefenderX = opponent.transform.position.x;
                found = true;
            }
        }

        return found ? deepestDefenderX : attackDir * (halfFieldLength * 0.5f);
    }

    Vector3 GetBaseTacticalAnchor()
    {
        float attackDir = Mathf.Sign(targetGoal.position.x);
        bool teamInPossession = (MatchManager.Instance == null || MatchManager.Instance.lastTouchedTeam == this.team);
        bool ballInAttackingHalf = (Mathf.Sign(ball.position.x) == attackDir);
        bool ballWideInAttackingThird = ballInAttackingHalf &&
                                        Mathf.Abs(ball.position.x) > halfFieldLength * 0.55f &&
                                        Mathf.Abs(ball.position.z) > halfFieldWidth * 0.45f;

        // SPECIALIZED TARGET-MAN / POACHER POSITIONING FOR STRIKER (ST)
        if (tacticalPosition == Position.ST)
        {
            if (teamInPossession)
            {
                // 1. If the ball is wide in the crossing zone, dart to the penalty spot!
                if (ballWideInAttackingThird)
                {
                    float penaltySpotX = targetGoal.position.x - (attackDir * 10.5f);
                    float nearPostZ = Mathf.Sign(ball.position.z) * 2.5f;
                    return new Vector3(penaltySpotX, transform.position.y, nearPostZ);
                }

                // 2. Otherwise, lurk right on the shoulder of the opponent's last defender in the central channel
                // so we are always ready for a through-ball instead of dropping back to the ball carrier!
                float offsideLineX = GetOpponentDefensiveLineX() - (attackDir * 1.5f);
                float minForwardX = ball.position.x + (attackDir * 10f);
                float poacherX = (attackDir > 0) ? Mathf.Max(offsideLineX, minForwardX) : Mathf.Min(offsideLineX, minForwardX);
                poacherX = Mathf.Clamp(poacherX, -halfFieldLength * 0.85f, halfFieldLength * 0.85f);

                // Stay inside the central goal-width corridor (-6m to +6m Z)
                float centralZ = Mathf.Clamp(ball.position.z * 0.25f, -6f, 6f);
                return new Vector3(poacherX, transform.position.y, centralZ);
            }
            else
            {
                // When our team is defending, the Striker stays near the halfway line / upper midfield
                // ready to spring a counter-attack rather than dropping into our own penalty box
                float counterAttackX = Mathf.Clamp(
                    ball.position.x * 0.45f + (attackDir * 8f),
                    -halfFieldLength * 0.25f,
                    halfFieldLength * 0.75f
                );
                // Ensure Blue ST doesn't drop behind X = -8 and Red ST doesn't drop behind X = +8
                if (attackDir > 0) counterAttackX = Mathf.Max(-8f, counterAttackX);
                else counterAttackX = Mathf.Min(8f, counterAttackX);

                float counterZ = Mathf.Clamp(ball.position.z * 0.3f, -7f, 7f);
                return new Vector3(counterAttackX, transform.position.y, counterZ);
            }
        }

        // OTHER POSITIONS (Defenders, Midfielders, Wingers)
        float shiftX = ball.position.x * 0.72f;
        float ballNormalizedZ = ball.position.z / Mathf.Max(1f, halfFieldWidth);
        float shiftZ = ballNormalizedZ * 7.5f;

        if (IsDefender())
        {
            if (teamInPossession)
            {
                shiftX += attackDir * (ballInAttackingHalf ? 12f : 5f);

                bool ballOnMyWing = Mathf.Sign(ball.position.z) == Mathf.Sign(homePosition.z) && Mathf.Abs(ball.position.z) > 5f;
                if ((tacticalPosition == Position.LB || tacticalPosition == Position.RB) && ballOnMyWing)
                {
                    shiftX += attackDir * 11f;
                }
            }
            else
            {
                shiftX -= attackDir * 4f;
            }
        }
        else if (IsMidfielder())
        {
            shiftX += attackDir * (teamInPossession ? 7f : -4f);
        }
        else if (IsWinger())
        {
            shiftX += attackDir * (teamInPossession ? 9f : 2f);

            if (teamInPossession)
            {
                bool ballOnOppositeWing = Mathf.Sign(ball.position.z) != Mathf.Sign(homePosition.z) && ballWideInAttackingThird;
                if (ballOnOppositeWing)
                {
                    float farPostX = targetGoal.position.x - (attackDir * 9f);
                    float farPostZ = Mathf.Sign(homePosition.z) * 4.5f;
                    return new Vector3(farPostX, transform.position.y, farPostZ);
                }
                else
                {
                    shiftZ = Mathf.Sign(homePosition.z) * (halfFieldWidth * 0.80f) - homePosition.z;
                }
            }
        }

        float targetX = Mathf.Clamp(homePosition.x + shiftX, -halfFieldLength * 0.90f, halfFieldLength * 0.90f);
        float targetZ = Mathf.Clamp(homePosition.z + shiftZ, -halfFieldWidth * 0.88f, halfFieldWidth * 0.88f);

        return new Vector3(targetX, transform.position.y, targetZ);
    }

    Vector3 GetDynamicFormationSpot()
    {
        Vector3 anchor = GetBaseTacticalAnchor();
        bool teamInPossession = (MatchManager.Instance == null || MatchManager.Instance.lastTouchedTeam == this.team);

        if (!teamInPossession && (IsDefender() || tacticalPosition == Position.CDM))
        {
            SoccerAI markTarget = FindOpponentToMark(anchor);
            if (markTarget != null)
            {
                Vector3 toOurGoal = (defendGoal.position - markTarget.transform.position).normalized;
                Vector3 goalSideSpot = markTarget.transform.position + toOurGoal * 2.5f;
                anchor = Vector3.Lerp(anchor, goalSideSpot, 0.65f);
            }
        }

        Vector3 organicRoam = GetOrganicRoamOffset();
        Vector3 finalSpot = anchor + organicRoam;

        finalSpot.x = Mathf.Clamp(finalSpot.x, -halfFieldLength * 0.92f, halfFieldLength * 0.92f);
        finalSpot.z = Mathf.Clamp(finalSpot.z, -halfFieldWidth * 0.90f, halfFieldWidth * 0.90f);
        finalSpot.y = transform.position.y;

        return finalSpot;
    }

    SoccerAI FindOpponentToMark(Vector3 myZoneCenter)
    {
        SoccerAI bestThreat = null;
        float closestDist = 14f;

        foreach (var opponent in allPlayers)
        {
            if (opponent.team == this.team || opponent.tacticalPosition == Position.GK)
                continue;

            if (opponent.IsDefender()) continue;

            float distToZone = Vector3.Distance(myZoneCenter, opponent.transform.position);
            if (distToZone < closestDist)
            {
                closestDist = distToZone;
                bestThreat = opponent;
            }
        }
        return bestThreat;
    }

    Vector3 GetOrganicRoamOffset()
    {
        float t = Time.time * 1.6f + roamSeed;
        float roamX = Mathf.Sin(t) * 2.4f;
        float roamZ = Mathf.Cos(t * 0.85f) * 2.4f;
        return new Vector3(roamX, 0f, roamZ);
    }

    void TryInteractWithBall()
    {
        if (Time.time < nextActionTime) return;

        float distToBall = Vector3.Distance(transform.position, ball.position);
        if (distToBall > kickRange) return;

        if (MatchManager.Instance != null)
        {
            MatchManager.Instance.RegisterTouch(this.team);
        }

        approachAngleOffset = Random.Range(-0.35f, 0.35f);

        // 1. STALEMATE BREAKER
        if (stuckTimer >= stalemateTimeThreshold || IsOpponentSandwichingBall())
        {
            BreakStalemateKick();
            stuckTimer = 0f;
            nextActionTime = Time.time + kickCooldown + Random.Range(-0.1f, 0.15f);
            return;
        }

        // 2. WINGER / FULLBACK CROSS INTO THE PENALTY BOX
        if ((IsWinger() || tacticalPosition == Position.LB || tacticalPosition == Position.RB) && IsInCrossingZone())
        {
            if (TryCrossIntoBox())
            {
                nextActionTime = Time.time + kickCooldown;
                return;
            }
        }

        float distToGoal = Vector3.Distance(transform.position, targetGoal.position);

        // 3. SHOOT
        bool centralEnoughToShoot = !IsWinger() || Mathf.Abs(ball.position.z) < halfFieldWidth * 0.42f;
        if (distToGoal <= shootDistance && centralEnoughToShoot && tacticalPosition != Position.GK)
        {
            ShootAtGoal();
            nextActionTime = Time.time + kickCooldown;
            return;
        }

        // 4. MIDFIELDER EARLY THROUGH-BALL VISION
        if (IsMidfielder())
        {
            SoccerAI runner = FindBestTeammateToPass(out bool isThrough, out Vector3 throughSpot);
            if (runner != null && isThrough)
            {
                PassToTeammate(runner, true, throughSpot);
                nextActionTime = Time.time + kickCooldown;
                return;
            }
        }

        // 5. DRIBBLE
        if (tacticalPosition != Position.GK && IsUncontested())
        {
            DribbleForward();
            nextActionTime = Time.time + dribbleCooldown;
            return;
        }

        // 6. PASS
        SoccerAI openTeammate = FindBestTeammateToPass(out bool useThroughBall, out Vector3 targetSpot);
        if (openTeammate != null)
        {
            PassToTeammate(openTeammate, useThroughBall, targetSpot);
            nextActionTime = Time.time + kickCooldown;
            return;
        }

        // 7. FALLBACK
        if (IsDefender() || tacticalPosition == Position.GK)
        {
            ShootAtGoal();
            nextActionTime = Time.time + kickCooldown;
        }
        else
        {
            DribbleForward();
            nextActionTime = Time.time + dribbleCooldown;
        }
    }

    bool IsInCrossingZone()
    {
        float attackDir = Mathf.Sign(targetGoal.position.x);
        bool inAttackingHalf = Mathf.Sign(ball.position.x) == attackDir;
        bool deepEnough = Mathf.Abs(ball.position.x) > halfFieldLength * 0.62f;
        bool wideEnough = Mathf.Abs(ball.position.z) > halfFieldWidth * 0.42f;

        return inAttackingHalf && deepEnough && wideEnough;
    }

    Vector3 GetWingerChannelTarget()
    {
        float attackDir = Mathf.Sign(targetGoal.position.x);
        float wingSign = Mathf.Sign(homePosition.z);
        if (Mathf.Abs(wingSign) < 0.1f) wingSign = Mathf.Sign(ball.position.z);

        float cornerChannelX = attackDir * (halfFieldLength * 0.84f);
        float cornerChannelZ = wingSign * (halfFieldWidth * 0.74f);
        return new Vector3(cornerChannelX, transform.position.y, cornerChannelZ);
    }

    bool TryCrossIntoBox()
    {
        float attackDir = Mathf.Sign(targetGoal.position.x);
        SoccerAI striker = null;
        SoccerAI farPostRunner = null;

        foreach (var player in allPlayers)
        {
            if (player.team != this.team || player == this) continue;

            if (player.tacticalPosition == Position.ST)
                striker = player;
            else if (player.IsWinger() || player.IsMidfielder())
            {
                if (farPostRunner == null || Vector3.Distance(player.transform.position, targetGoal.position) <
                    Vector3.Distance(farPostRunner.transform.position, targetGoal.position))
                {
                    farPostRunner = player;
                }
            }
        }

        if (striker == null && farPostRunner == null)
            return false;

        float boxTargetX = targetGoal.position.x - (attackDir * Random.Range(8.5f, 12.0f));
        float boxTargetZ = (striker != null)
            ? Mathf.Clamp(striker.transform.position.z + Random.Range(-2.5f, 2.5f), -6f, 6f)
            : Random.Range(-4f, 4f);

        Vector3 crossLandingSpot = new Vector3(boxTargetX, transform.position.y, boxTargetZ);

        Vector3 crossDir = (crossLandingSpot - ball.position).normalized;
        crossDir.y = Random.Range(0.32f, 0.42f);

        float crossDist = Vector3.Distance(ball.position, crossLandingSpot);
        float crossPower = Mathf.Clamp(crossDist * 0.92f, 10.5f, kickForce * crossForceMultiplier);

        ballRb.linearVelocity *= 0.15f;
        ballRb.AddForce(crossDir.normalized * crossPower, ForceMode.Impulse);

        if (striker != null)
            striker.NotifyIncomingThroughBall(crossLandingSpot, 1.8f);
        if (farPostRunner != null)
            farPostRunner.NotifyIncomingThroughBall(crossLandingSpot + new Vector3(0f, 0f, -Mathf.Sign(ball.position.z) * 2.5f), 1.8f);

        Debug.DrawLine(ball.position, crossLandingSpot, Color.white, 1.8f);
        return true;
    }

    bool IsUncontested()
    {
        Vector3 toGoal = (targetGoal.position - transform.position).normalized;

        foreach (var opponent in allPlayers)
        {
            if (opponent.team == this.team) continue;

            float dist = Vector3.Distance(transform.position, opponent.transform.position);
            if (dist < pressureRadius)
            {
                Vector3 toOpponent = (opponent.transform.position - transform.position).normalized;
                if (Vector3.Dot(toGoal, toOpponent) > -0.25f || dist < 2.2f)
                {
                    return false;
                }
            }
        }
        return true;
    }

    void DribbleForward()
    {
        Vector3 targetPoint = (IsWinger() && !IsInCrossingZone())
            ? GetWingerChannelTarget()
            : targetGoal.position;

        Vector3 dirToTarget = (targetPoint - ball.position).normalized;

        SoccerAI nearestOpp = GetNearestOpponent();
        if (nearestOpp != null)
        {
            float oppDist = Vector3.Distance(transform.position, nearestOpp.transform.position);
            if (oppDist < pressureRadius * 1.6f)
            {
                Vector3 awayFromOpp = (transform.position - nearestOpp.transform.position).normalized;
                awayFromOpp.y = 0f;
                dirToTarget = (dirToTarget * 0.78f + awayFromOpp * 0.40f).normalized;
            }
        }

        if (Mathf.Abs(ball.position.z) > halfFieldWidth * 0.84f)
        {
            float inwardZ = -Mathf.Sign(ball.position.z) * 0.45f;
            dirToTarget = new Vector3(dirToTarget.x, 0f, inwardZ).normalized;
        }

        dirToTarget.y = 0f;
        ballRb.linearVelocity *= 0.35f;
        ballRb.AddForce(dirToTarget * dribbleForce, ForceMode.Impulse);

        Debug.DrawLine(ball.position, ball.position + dirToTarget * 3f, Color.green, 0.35f);
    }

    void PassToTeammate(SoccerAI openTeammate, bool isThroughBall = false, Vector3 throughBallSpot = default)
    {
        if (isThroughBall)
        {
            Vector3 passDir = (throughBallSpot - ball.position).normalized;
            passDir.y = Random.Range(0.24f, 0.34f);

            float passDistance = Vector3.Distance(ball.position, throughBallSpot);
            float loftForce = Mathf.Clamp(passDistance * 0.88f, 9.5f, kickForce * throughBallForceMultiplier);

            ballRb.linearVelocity *= 0.15f;
            ballRb.AddForce(passDir.normalized * loftForce, ForceMode.Impulse);

            openTeammate.NotifyIncomingThroughBall(throughBallSpot, 1.6f);

            Debug.DrawLine(ball.position, throughBallSpot, Color.blue, 1.5f);
        }
        else
        {
            Vector3 leadPosition = openTeammate.transform.position + (openTeammate.agent.velocity * 0.4f);
            leadPosition.z = Mathf.Clamp(leadPosition.z, -halfFieldWidth * 0.88f, halfFieldWidth * 0.88f);

            Vector3 passDir = (leadPosition - ball.position).normalized;
            passDir.y = 0.03f;

            float passDistance = Vector3.Distance(ball.position, leadPosition);
            float scaledPassForce = Mathf.Clamp(passDistance * 0.95f, 6.5f, kickForce * passForceMultiplier);

            ballRb.linearVelocity *= 0.25f;
            ballRb.AddForce(passDir * scaledPassForce, ForceMode.Impulse);

            Debug.DrawLine(ball.position, leadPosition, Color.yellow, 1.0f);
        }
    }

    void ShootAtGoal()
    {
        Vector3 aimPoint = targetGoal.position + new Vector3(0f, 0f, Random.Range(-3.2f, 3.2f));
        Vector3 kickDir = (aimPoint - ball.position).normalized;
        kickDir.y = Random.Range(0.06f, 0.22f);

        ballRb.AddForce(kickDir * (kickForce * Random.Range(0.92f, 1.15f)), ForceMode.Impulse);

        Debug.DrawLine(ball.position, aimPoint, Color.red, 1.0f);
    }

    SoccerAI GetNearestOpponent()
    {
        SoccerAI nearest = null;
        float minDist = Mathf.Infinity;
        foreach (var opponent in allPlayers)
        {
            if (opponent.team == this.team) continue;
            float d = Vector3.Distance(transform.position, opponent.transform.position);
            if (d < minDist)
            {
                minDist = d;
                nearest = opponent;
            }
        }
        return nearest;
    }

    bool IsOpponentSandwichingBall()
    {
        foreach (var opponent in allPlayers)
        {
            if (opponent.team == this.team) continue;
            if (Vector3.Distance(opponent.transform.position, ball.position) <= kickRange * 1.1f)
                return true;
        }
        return false;
    }

    void BreakStalemateKick()
    {
        Vector3 forwardToGoal = (targetGoal.position - ball.position).normalized;
        float sideSign = (Random.value > 0.5f) ? 1f : -1f;

        if (Mathf.Abs(ball.position.z) > halfFieldWidth * 0.7f)
            sideSign = -Mathf.Sign(ball.position.z);

        Vector3 sideDir = new Vector3(-forwardToGoal.z * sideSign, 0f, forwardToGoal.x * sideSign);
        Vector3 escapeDir = (forwardToGoal * 0.6f + sideDir * 0.8f).normalized;
        escapeDir.y = Random.Range(0.28f, 0.45f);

        ballRb.linearVelocity = Vector3.zero;
        ballRb.AddForce(escapeDir * (kickForce * 0.85f), ForceMode.Impulse);

        Debug.DrawLine(ball.position, ball.position + escapeDir * 5f, Color.cyan, 1.0f);
    }

    SoccerAI FindBestTeammateToPass(out bool isThroughBall, out Vector3 bestTargetSpot)
    {
        SoccerAI bestTeammate = null;
        isThroughBall = false;
        bestTargetSpot = Vector3.zero;

        float bestScore = -Mathf.Infinity;
        float myDistToGoal = Vector3.Distance(transform.position, targetGoal.position);

        foreach (var candidate in allPlayers)
        {
            if (candidate == this || candidate.team != this.team || candidate.tacticalPosition == Position.GK)
                continue;

            float passDist = Vector3.Distance(transform.position, candidate.transform.position);
            if (passDist < 4f || passDist > 34f)
                continue;

            // 1. CHECK FOR LOFTED THROUGH-BALL (Midfielder -> Forward sprinting into space)
            if (IsMidfielder() && candidate.IsForward())
            {
                if (TryGetThroughBallSpot(candidate, out Vector3 spaceSpot))
                {
                    float spaceDistToGoal = Vector3.Distance(spaceSpot, targetGoal.position);
                    float throughScore = (myDistToGoal - spaceDistToGoal) * 2.6f - (passDist * 0.15f) + 8f;

                    if (throughScore > bestScore)
                    {
                        bestScore = throughScore;
                        bestTeammate = candidate;
                        isThroughBall = true;
                        bestTargetSpot = spaceSpot;
                    }
                }
            }

            // 2. CHECK FOR STANDARD PASS TO FEET
            float candidateDistToGoal = Vector3.Distance(candidate.transform.position, targetGoal.position);
            if (candidateDistToGoal < myDistToGoal + 8f)
            {
                if (IsTeammateOpen(candidate))
                {
                    float forwardGain = (myDistToGoal - candidateDistToGoal) * 2.0f;
                    float distancePenalty = passDist * 0.22f;
                    float wingBonus = candidate.IsWinger() ? 3.5f : 0f;
                    float score = forwardGain - distancePenalty + wingBonus;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTeammate = candidate;
                        isThroughBall = false;
                        bestTargetSpot = candidate.transform.position;
                    }
                }
            }
        }
        return bestTeammate;
    }

    bool TryGetThroughBallSpot(SoccerAI forward, out Vector3 landingSpot)
    {
        float attackDir = Mathf.Sign(targetGoal.position.x);
        Vector3 toGoalDir = (targetGoal.position - forward.transform.position).normalized;

        bool movingForward = (forward.agent.velocity.x * attackDir) > -0.5f;
        if (!movingForward)
        {
            landingSpot = Vector3.zero;
            return false;
        }

        Vector3 runDir;
        if (forward.IsWinger())
        {
            runDir = (forward.GetWingerChannelTarget() - forward.transform.position).normalized;
        }
        else
        {
            runDir = (forward.agent.velocity.sqrMagnitude > 1f)
                ? (forward.agent.velocity.normalized * 0.55f + toGoalDir * 0.45f).normalized
                : new Vector3(attackDir, 0f, 0f);
        }

        runDir.y = 0f;
        landingSpot = forward.transform.position + runDir * 9.5f;

        landingSpot.x = Mathf.Clamp(landingSpot.x, -halfFieldLength * 0.86f, halfFieldLength * 0.86f);
        landingSpot.z = Mathf.Clamp(landingSpot.z, -halfFieldWidth * 0.84f, halfFieldWidth * 0.84f);

        float forwardTimeToSpot = Vector3.Distance(forward.transform.position, landingSpot) / Mathf.Max(3.5f, forward.agent.speed);

        foreach (var opponent in allPlayers)
        {
            if (opponent.team == this.team) continue;

            float oppDistToSpot = Vector3.Distance(opponent.transform.position, landingSpot);
            float oppTimeToSpot = oppDistToSpot / Mathf.Max(3.5f, opponent.agent.speed);

            if (oppTimeToSpot < forwardTimeToSpot + 0.35f)
            {
                return false;
            }
        }

        return true;
    }

    bool IsTeammateOpen(SoccerAI teammate)
    {
        foreach (var opponent in allPlayers)
        {
            if (opponent.team == this.team) continue;

            if (Vector3.Distance(opponent.transform.position, teammate.transform.position) < 2.8f)
                return false;

            Vector3 passVector = teammate.transform.position - ball.position;
            Vector3 toOpponent = opponent.transform.position - ball.position;
            float passDist = passVector.magnitude;

            float projection = Vector3.Dot(toOpponent, passVector.normalized);
            if (projection > 0f && projection < passDist)
            {
                Vector3 closestPointOnLine = ball.position + passVector.normalized * projection;
                if (Vector3.Distance(opponent.transform.position, closestPointOnLine) < 1.3f)
                    return false;
            }
        }
        return true;
    }

    public bool IsDefender() =>
        tacticalPosition == Position.LB || tacticalPosition == Position.CB_Left ||
        tacticalPosition == Position.CB_Right || tacticalPosition == Position.RB;

    public bool IsMidfielder() =>
        tacticalPosition == Position.CM_Left || tacticalPosition == Position.CDM ||
        tacticalPosition == Position.CM_Right;

    public bool IsForward() =>
        tacticalPosition == Position.LW || tacticalPosition == Position.ST ||
        tacticalPosition == Position.RW;

    public bool IsWinger() =>
        tacticalPosition == Position.LW || tacticalPosition == Position.RW;

    public void ResetPlayer()
    {
        stuckTimer = 0f;
        chasePassUntilTime = 0f;
        Vector3 jitter = new Vector3(Random.Range(-0.5f, 0.5f), 0f, Random.Range(-0.5f, 0.5f));
        agent.Warp(homePosition + jitter);
        agent.ResetPath();
        transform.rotation = homeRotation;
    }
}