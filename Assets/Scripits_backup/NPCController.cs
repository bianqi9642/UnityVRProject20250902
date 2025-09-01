using UnityEngine;
using UnityEngine.AI;

public enum UniformType
{
    Uniform,   // uniform
    Casual     // non-uniform
}

public enum RouteChoice
{
    GoStraight,
    TurnLeft,
    TurnRight
}

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Animator))]
public class NPCController : MonoBehaviour
{
    [Header("NPC Parameters (can be set externally)")]
    public UniformType uniformType = UniformType.Casual;
    public RouteChoice routeChoice = RouteChoice.GoStraight;
    [Range(0.5f, 3f)] public float moveSpeed = 1.2f;
    [Range(0f, 1f)] public float pathStability = 1f;

    [Header("Animation Settings")]
    public string paramWalk = "isWalking";    // Must match your Animator bool

    private NavMeshAgent agent;
    private Animator animator;
    private Vector3 targetPosition;
    private bool isMoving = false;

    [Header("Intersection Waypoints (must be set via Spawner)")]
    public Transform point_Straight;
    public Transform point_Left;
    public Transform point_Right;

    void Awake()
    {
        // grab both components (RequireComponent ensures they exist)
        agent   = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();

        // We want the NavMeshAgent to drive movement, not root motion:
        animator.applyRootMotion = false;

        // configure agent
        agent.speed = moveSpeed;
        agent.autoBraking = (pathStability >= 0.8f);
    }

    void Start()
    {
        ApplyUniformMaterial();
        ComputeTarget();
    }

    void Update()
    {
        // 1) Check arrival
        if (!agent.pathPending && agent.remainingDistance <= agent.stoppingDistance)
        {
            SetWalking(false);

            // immediately pick a new route and go again
            // ComputeTarget();
            return;
        }

        // 2) Check actual movement
        bool nowMoving = agent.velocity.sqrMagnitude > 0.1f;
        SetWalking(nowMoving);

        // 3) Optional jitter
        if (pathStability < 1f && !agent.pathPending)
        {
            float jitterChance = 1f - pathStability;
            if (Random.value < jitterChance * Time.deltaTime)
            {
                JitterPath();
            }
        }
    }

    private void SetWalking(bool walk)
    {
        if (walk == isMoving) return;
        isMoving = walk;
        animator.SetBool(paramWalk, isMoving);
    }

    private void JitterPath()
    {
        Vector3 jittered = targetPosition + new Vector3(
            Random.Range(-0.5f, 0.5f),
            0,
            Random.Range(-0.5f, 0.5f)
        );
        if (NavMesh.SamplePosition(jittered, out var hit, 1f, NavMesh.AllAreas))
            agent.SetDestination(hit.position);
    }

    void ApplyUniformMaterial()
    {
        var smr = GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr == null) return;

        var mats = smr.materials;
        mats[1] = Resources.Load<Material>(
            uniformType == UniformType.Uniform
                ? "Materials/Uniform"
                : "Materials/Casual"
        );
        smr.materials = mats;
    }

    void ComputeTarget()
    {
        if (point_Straight == null || point_Left == null || point_Right == null)
        {
            Debug.LogError(
                "Missing intersection waypoints on NPCController"
                + " – set Straight/Left/Right in your Spawner."
            );
            return;
        }

        switch (routeChoice)
        {
            case RouteChoice.GoStraight:
                targetPosition = point_Straight.position; break;
            case RouteChoice.TurnLeft:
                targetPosition = point_Left.position;    break;
            case RouteChoice.TurnRight:
                targetPosition = point_Right.position;   break;
        }

        agent.SetDestination(targetPosition);
    }
}
