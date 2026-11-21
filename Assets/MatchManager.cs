using System.Collections;
using UnityEngine;

public class MatchManager : MonoBehaviour
{
    public static MatchManager Instance { get; private set; }

    [Header("Score & Simulation")]
    public int blueScore = 0;
    public int redScore = 0;
    [Range(0.5f, 3f)]
    public float simulationSpeed = 1f;

    [Header("Pitch Boundaries (Auto-detected if Goals exist)")]
    public float halfFieldLength = 40f;
    public float halfFieldWidth = 25f;

    // Set-piece state read by SoccerAI
    [HideInInspector] public SoccerAI.Team lastTouchedTeam = SoccerAI.Team.Blue;
    [HideInInspector] public bool isThrowInActive = false;
    [HideInInspector] public SoccerAI designatedThrower = null;

    private Rigidbody ballRb;
    private Vector3 ballStartPos;
    private SoccerAI[] allPlayers;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        ballRb = GetComponent<Rigidbody>();
        ballStartPos = transform.position;
        allPlayers = FindObjectsByType<SoccerAI>(FindObjectsSortMode.None);

        // Auto-detect pitch dimensions from goals if possible
        GameObject blueGoal = GameObject.FindGameObjectWithTag("BlueGoal");
        GameObject redGoal = GameObject.FindGameObjectWithTag("RedGoal");
        if (blueGoal != null && redGoal != null)
        {
            halfFieldLength = Mathf.Abs(redGoal.transform.position.x - blueGoal.transform.position.x) * 0.5f;
            halfFieldWidth = halfFieldLength * 0.62f;
        }

        NudgeBallForKickoff();
    }

    void Update()
    {
        Time.timeScale = simulationSpeed;

        if (!isThrowInActive)
        {
            CheckOutOfBounds();
        }
    }

    public void RegisterTouch(SoccerAI.Team team)
    {
        lastTouchedTeam = team;
    }

    void CheckOutOfBounds()
    {
        // 1. Sideline Out-of-Bounds -> Trigger Throw-In!
        if (Mathf.Abs(transform.position.z) > halfFieldWidth)
        {
            StartCoroutine(ExecuteThrowInRoutine());
        }
        // 2. Safety catch if ball goes past the end-lines without hitting a goal trigger
        else if (Mathf.Abs(transform.position.x) > halfFieldLength + 1.5f)
        {
            ResetPositions();
        }
    }

    IEnumerator ExecuteThrowInRoutine()
    {
        isThrowInActive = true;

        // Award throw-in to the team that DID NOT touch the ball last
        SoccerAI.Team throwingTeam = (lastTouchedTeam == SoccerAI.Team.Blue)
            ? SoccerAI.Team.Red
            : SoccerAI.Team.Blue;

        // Clamp the throw-in spot right on the sideline where it exited
        float clampedX = Mathf.Clamp(transform.position.x, -halfFieldLength * 0.9f, halfFieldLength * 0.9f);
        float sidelineZ = Mathf.Sign(transform.position.z) * (halfFieldWidth - 0.3f);
        Vector3 throwSpot = new Vector3(clampedX, 0.5f, sidelineZ);

        // Freeze the ball and lift it to overhead throw height
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;
        ballRb.isKinematic = true;
        transform.position = throwSpot + Vector3.up * 1.3f;

        // Pick the closest outfield player on the throwing team to take the throw
        designatedThrower = GetClosestOutfieldPlayer(throwingTeam, throwSpot);

        if (designatedThrower != null)
        {
            // Place thrower just outside the touchline facing onto the pitch
            Vector3 outsideOffset = new Vector3(0f, 0f, Mathf.Sign(sidelineZ) * 0.8f);
            Vector3 lookInsideDir = new Vector3(0f, 0f, -Mathf.Sign(sidelineZ));
            designatedThrower.PrepareForThrowIn(throwSpot + outsideOffset, Quaternion.LookRotation(lookInsideDir));
        }

        Debug.Log($"THROW-IN awarded to {throwingTeam} at X = {clampedX:F1}");

        // Wait 1 second so nearby players shift into support/marking spaces
        yield return new WaitForSeconds(1.0f);

        // Re-enable ball physics and execute the throw
        ballRb.isKinematic = false;
        transform.position = throwSpot + new Vector3(0f, 0.8f, -Mathf.Sign(sidelineZ) * 0.4f);

        if (designatedThrower != null)
        {
            designatedThrower.ExecuteThrowIn();
        }

        designatedThrower = null;
        isThrowInActive = false;
    }

    SoccerAI GetClosestOutfieldPlayer(SoccerAI.Team team, Vector3 spot)
    {
        SoccerAI closest = null;
        float minDist = Mathf.Infinity;

        foreach (var player in allPlayers)
        {
            if (player.team != team || player.tacticalPosition == SoccerAI.Position.GK)
                continue;

            float d = Vector3.Distance(player.transform.position, spot);
            if (d < minDist)
            {
                minDist = d;
                closest = player;
            }
        }
        return closest;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("RedGoal"))
        {
            blueScore++;
            Debug.Log($"GOAL! Blue {blueScore} - {redScore} Red");
            ResetPositions();
        }
        else if (other.CompareTag("BlueGoal"))
        {
            redScore++;
            Debug.Log($"GOAL! Blue {blueScore} - {redScore} Red");
            ResetPositions();
        }
    }

    void ResetPositions()
    {
        StopAllCoroutines();
        isThrowInActive = false;
        designatedThrower = null;

        ballRb.isKinematic = false;
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;
        transform.position = ballStartPos;

        foreach (var player in allPlayers)
        {
            player.ResetPlayer();
        }

        NudgeBallForKickoff();
    }

    void NudgeBallForKickoff()
    {
        Vector3 randomKickoff = new Vector3(Random.Range(-3f, 3f), 0f, Random.Range(-4f, 4f));
        ballRb.AddForce(randomKickoff, ForceMode.Impulse);
    }
}