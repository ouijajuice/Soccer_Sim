using UnityEngine;
using UnityEngine.AI;

public class TeamSpawner : MonoBehaviour
{
    [Header("Prefab & Scene References")]
    public GameObject playerPrefab;
    public Transform blueGoal; // Left goal (e.g., X = -40)
    public Transform redGoal;  // Right goal (e.g., X = +40)

    [Header("Team Visuals")]
    public Material blueMaterial;
    public Material redMaterial;
    public Material goalieMaterial; // Optional distinct jersey for GKs

    // Normalized 4-3-3 coordinates for the Left side of the pitch:
    // xFraction: -0.95 is the goal line, -0.08 is near center circle
    // zFraction: -1.0 is bottom touchline, +1.0 is top touchline
    private struct FormationSlot
    {
        public SoccerAI.Position pos;
        public float xFraction;
        public float zFraction;

        public FormationSlot(SoccerAI.Position pos, float xFraction, float zFraction)
        {
            this.pos = pos;
            this.xFraction = xFraction;
            this.zFraction = zFraction;
        }
    }

    private static readonly FormationSlot[] Formation433 = new FormationSlot[]
    {
        // Goalkeeper
        new FormationSlot(SoccerAI.Position.GK,       -0.94f,  0.00f),

        // 4 Defenders (LB, CB_Left, CB_Right, RB)
        new FormationSlot(SoccerAI.Position.LB,       -0.65f,  0.65f),
        new FormationSlot(SoccerAI.Position.CB_Left,  -0.70f,  0.24f),
        new FormationSlot(SoccerAI.Position.CB_Right, -0.70f, -0.24f),
        new FormationSlot(SoccerAI.Position.RB,       -0.65f, -0.65f),

        // 3 Midfielders (CM_Left, CDM, CM_Right)
        new FormationSlot(SoccerAI.Position.CM_Left,  -0.36f,  0.42f),
        new FormationSlot(SoccerAI.Position.CDM,      -0.46f,  0.00f),
        new FormationSlot(SoccerAI.Position.CM_Right, -0.36f, -0.42f),

        // 3 Forwards (LW, ST, RW)
        new FormationSlot(SoccerAI.Position.LW,       -0.14f,  0.68f),
        new FormationSlot(SoccerAI.Position.ST,       -0.08f,  0.00f),
        new FormationSlot(SoccerAI.Position.RW,       -0.14f, -0.68f)
    };

    void Awake()
    {
        if (playerPrefab == null || blueGoal == null || redGoal == null)
        {
            Debug.LogError("TeamSpawner: Please assign Player Prefab, Blue Goal, and Red Goal in the Inspector!");
            return;
        }

        // Automatically measure the pitch from the goal transforms
        float halfLength = Mathf.Abs(redGoal.position.x - blueGoal.position.x) * 0.5f;
        float halfWidth = halfLength * 0.62f; // Standard soccer pitch aspect ratio

        // Create parent folders in the Hierarchy to keep it clean
        Transform blueParent = new GameObject("Blue Team").transform;
        Transform redParent = new GameObject("Red Team").transform;

        SpawnTeam(SoccerAI.Team.Blue, halfLength, halfWidth, blueParent);
        SpawnTeam(SoccerAI.Team.Red, halfLength, halfWidth, redParent);
    }

    void SpawnTeam(SoccerAI.Team team, float halfLength, float halfWidth, Transform teamParent)
    {
        // Blue starts on negative X facing +X; Red mirrors to positive X facing -X
        float sideSign = (team == SoccerAI.Team.Blue) ? 1f : -1f;
        Quaternion facingRotation = (team == SoccerAI.Team.Blue)
            ? Quaternion.Euler(0f, 90f, 0f)
            : Quaternion.Euler(0f, -90f, 0f);

        foreach (var slot in Formation433)
        {
            float worldX = slot.xFraction * halfLength * sideSign;
            float worldZ = slot.zFraction * halfWidth * sideSign;
            Vector3 spawnPos = new Vector3(worldX, 1f, worldZ);

            GameObject playerObj = Instantiate(playerPrefab, spawnPos, facingRotation, teamParent);
            playerObj.name = $"{team}_{slot.pos}";

            // Ensure NavMeshAgent snaps cleanly onto the baked NavMesh
            if (playerObj.TryGetComponent<NavMeshAgent>(out var agent))
            {
                agent.Warp(spawnPos);
            }

            // Configure SoccerAI team, position, and target goals
            if (playerObj.TryGetComponent<SoccerAI>(out var ai))
            {
                ai.team = team;
                ai.tacticalPosition = slot.pos;
                ai.targetGoal = (team == SoccerAI.Team.Blue) ? redGoal : blueGoal;
                ai.defendGoal = (team == SoccerAI.Team.Blue) ? blueGoal : redGoal;
            }

            // Apply team material (and optional goalie material)
            if (playerObj.TryGetComponent<Renderer>(out var rend))
            {
                if (slot.pos == SoccerAI.Position.GK && goalieMaterial != null)
                {
                    rend.material = goalieMaterial;
                }
                else
                {
                    rend.material = (team == SoccerAI.Team.Blue) ? blueMaterial : redMaterial;
                }
            }
        }
    }
}