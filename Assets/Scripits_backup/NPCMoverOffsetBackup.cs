using UnityEngine;
using UnityEngine.AI;
using System.Collections;

public class NPCMoverOffsetBackup : MonoBehaviour
{
    [Header("Path Points")]
    public Transform pointStart;
    public Transform pointCenter;
    public Transform pointLeft;
    public Transform pointStraight;
    public Transform pointRight;

    [Header("Path Offset Settings")]
    [Tooltip("Maximum radius for randomly offsetting the entire path in the XZ plane.")]
    public float pathOffsetRadius = 2.0f;

    [Header("Movement Settings")]
    [Tooltip("Movement speed of the NPC in Direct mode or base speed in Uncertain mode.")]
    public float moveSpeed = 1.5f;

    [Tooltip("Optional: Stopping distance from the target point. Leave zero to use NavMeshAgent default.")]
    public float stoppingDistance = 0.5f;

    public enum MovementStyle { Direct, Uncertain }
    [Tooltip("Choose Direct for straight-line movement, or Uncertain for pedestrian-like wandering (slow down, pause, frequent direction changes).")]
    public MovementStyle movementStyle = MovementStyle.Direct;

    [Header("Uncertain Movement Parameters")]
    [Tooltip("When in Uncertain mode: maximum lateral offset radius from the direct path.")]
    public float wanderRadius = 0.5f;

    [Tooltip("When in Uncertain mode: minimum distance ahead along the path before picking the next intermediate point.")]
    public float minSegmentDistance = 3.0f;

    [Tooltip("When in Uncertain mode: maximum distance ahead along the path before picking the next intermediate point.")]
    public float maxSegmentDistance = 5.0f;

    [Tooltip("When in Uncertain mode: probability (0 to 1) to pause before each segment.")]
    [Range(0f, 1f)]
    public float pauseProbability = 0.5f;

    [Tooltip("When in Uncertain mode: minimum pause duration in seconds.")]
    public float minPauseDuration = 0.5f;

    [Tooltip("When in Uncertain mode: maximum pause duration in seconds.")]
    public float maxPauseDuration = 2.0f;

    [Tooltip("When in Uncertain mode: slow-down factor range (multiplied by moveSpeed). E.g., 0.3 to 0.8 of base speed.")]
    [Range(0f, 1f)]
    public float slowSpeedFactorMin = 1.0f;
    [Range(0f, 1f)]
    public float slowSpeedFactorMax = 1.0f;

    public enum OutfitType { Uniform, Casual }

    [Header("Outfit Settings")]
    [Tooltip("Choose either Uniform or Casual outfit.")]
    public OutfitType outfitType = OutfitType.Uniform;

    [Tooltip("If true, pick a random outfit (Uniform or Casual) at Start.")]
    public bool randomizeOutfitOnStart = false;

    [Tooltip("Material to apply when outfit is Uniform.")]
    public Material uniformMaterial;

    [Tooltip("Material to apply when outfit is Casual.")]
    public Material casualMaterial;

    [Tooltip("Renderers on the NPC whose material will be replaced when changing outfit.")]
    public Renderer[] renderersToChange;

    private NavMeshAgent agent;
    private enum State { ToCenter, ToRandomTarget, Stopped }
    private State currentState = State.ToCenter;

    // Track previous outfitType in Editor/play mode to detect changes
    private OutfitType prevOutfitType;

    // Track previous movementStyle to detect changes at runtime
    private MovementStyle prevMovementStyle;

    // Reference to the currently running coroutine for Uncertain movement (if any)
    private Coroutine uncertainMoveCoroutine = null;

    // Store the current target position (world-space) for the current leg
    private Vector3 currentTargetPosition;

    // Random overall path offset, only in the XZ plane
    private Vector3 pathOffset = Vector3.zero;
    // Radius used in NavMesh.SamplePosition; at least as large as pathOffsetRadius
    private float sampleRadius = 2.5f;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        if (agent == null)
        {
            Debug.LogError("NPCMover requires a NavMeshAgent component on the same GameObject.");
        }

        // Auto-fill renderersToChange if empty: find all SkinnedMeshRenderers in children
        if ((renderersToChange == null || renderersToChange.Length == 0))
        {
            var skinned = GetComponentsInChildren<SkinnedMeshRenderer>();
            if (skinned.Length > 0)
            {
                renderersToChange = skinned;
                Debug.Log($"[NPCMover] Auto-assigned {skinned.Length} SkinnedMeshRenderer(s) to renderersToChange.");
            }
        }
    }

    void Start()
    {
        if (agent == null)
            return;

        // Generate a random overall path offset once at start (XZ plane)
        Vector2 rand2D = Random.insideUnitCircle * pathOffsetRadius;
        pathOffset = new Vector3(rand2D.x, 0f, rand2D.y);

        // Set sampleRadius to be at least as large as pathOffsetRadius plus a margin
        sampleRadius = Mathf.Max(pathOffsetRadius, 1.0f) + 0.5f;

        // Apply initial base speed
        agent.speed = moveSpeed;

        // Optionally override stoppingDistance if set
        if (stoppingDistance > 0f)
            agent.stoppingDistance = stoppingDistance;

        // Apply outfit selection at start
        ApplyOutfitAtStart();

        // Record initial outfitType and movementStyle
        prevOutfitType = outfitType;
        prevMovementStyle = movementStyle;

        // Position NPC at start (with offset and NavMesh sampling)
        if (pointStart != null)
        {
            Vector3 startPos = GetOffsetNavMeshPosition(pointStart.position);
            transform.position = startPos;
        }

        // Begin first leg: to center (with offset and NavMesh sampling)
        if (pointCenter != null)
        {
            currentState = State.ToCenter;
            currentTargetPosition = GetOffsetNavMeshPosition(pointCenter.position);
            StartMovementTo(currentTargetPosition);
        }
        else
        {
            Debug.LogWarning("pointCenter is not assigned in Inspector.");
            currentState = State.Stopped;
        }
    }

    void Update()
    {
        if (agent == null)
            return;

        // 1. If in Direct mode and moveSpeed changed in Inspector at runtime, update agent.speed
        if (movementStyle == MovementStyle.Direct)
        {
            if (!Mathf.Approximately(agent.speed, moveSpeed))
            {
                agent.speed = moveSpeed;
                Debug.Log($"[NPCMoverOffset] {name} updated agent.speed to {agent.speed:F2} (Direct mode).");
            }
        }

        // 2. Detect outfitType changes at runtime: apply new outfit when changed
        if (renderersToChange != null && outfitType != prevOutfitType)
        {
            ChangeOutfit(outfitType);
            prevOutfitType = outfitType;
            Debug.Log($"[NPCMoverOffset] {name} changed outfit to {outfitType} at runtime.");
        }

        // 3. Detect movementStyle changes at runtime: stop any existing Uncertain coroutine and restart movement toward current target
        if (movementStyle != prevMovementStyle)
        {
            if (uncertainMoveCoroutine != null)
            {
                StopCoroutine(uncertainMoveCoroutine);
                uncertainMoveCoroutine = null;
            }
            // Only restart if NPC is currently en route to center or to random target
            if (currentState == State.ToCenter || currentState == State.ToRandomTarget)
            {
                Debug.Log($"[NPCMoverOffset] {name} movementStyle changed to {movementStyle}, restarting movement toward currentTargetPosition.");
                StartMovementTo(currentTargetPosition);
            }
            prevMovementStyle = movementStyle;
        }

        // 4. Only when NPC is in a moving state, check arrival and log status.
        if (currentState == State.ToCenter || currentState == State.ToRandomTarget)
        {
            // 4a. Log remainingDistance and pathStatus for debugging
            if (!agent.pathPending)
            {
                Debug.Log($"[NPCMoverOffset] {name} State={currentState}, remainingDistance={agent.remainingDistance:F2}, pathStatus={agent.pathStatus}");
            }

            // 4b. Arrival detection with a small tolerance added to stoppingDistance
            float arrivalTolerance = 0.5f;
            float arrivalThreshold = agent.stoppingDistance + arrivalTolerance;
            if (!agent.pathPending && agent.remainingDistance <= arrivalThreshold)
            {
                Debug.Log($"[NPCMoverOffset] {name} detected arrival in state {currentState}, remainingDistance={agent.remainingDistance:F2}, threshold={arrivalThreshold:F2}");

                if (currentState == State.ToCenter)
                {
                    // Arrived at center: pick next random target
                    Transform nextPoint = PickRandomTargetPoint();
                    Debug.Log($"[NPCMoverOffset] {name} arrived at center. PickRandomTargetPoint -> {(nextPoint != null ? nextPoint.name : "null")}");

                    if (nextPoint != null)
                    {
                        currentState = State.ToRandomTarget;
                        Vector3 nextPos = GetOffsetNavMeshPosition(nextPoint.position);
                        currentTargetPosition = nextPos;
                        Debug.Log($"[NPCMoverOffset] {name} next targetPos after offset/sample = {nextPos:F2}");
                        StartMovementTo(currentTargetPosition);
                    }
                    else
                    {
                        Debug.LogWarning($"[NPCMoverOffset] {name} no valid target points assigned; switching to Stopped.");
                        currentState = State.Stopped;
                        agent.isStopped = true;
                    }
                }
                else if (currentState == State.ToRandomTarget)
                {
                    // Arrived at final random target: stop agent and switch to Stopped state
                    Debug.Log($"[NPCMoverOffset] {name} arrived at random target; stopping agent.");
                    agent.isStopped = true;
                    currentState = State.Stopped;
                }
            }
        }
        // 5. When in Stopped state, do not perform arrival detection or repeated logging
        //    If needed, we could log a one-time status change upon entering Stopped, but avoid per-frame logs here.
    }


    void OnValidate()
    {
        // Called in Editor when values are changed in Inspector (Edit mode)
        if (agent == null)
            agent = GetComponent<NavMeshAgent>();
        if (agent != null)
        {
            // In Edit mode, just set base speed
            agent.speed = moveSpeed;
            if (stoppingDistance > 0f)
                agent.stoppingDistance = stoppingDistance;
        }

        // In Edit mode, apply outfit change immediately if possible
        if (!Application.isPlaying)
        {
            ChangeOutfit(outfitType);
            prevOutfitType = outfitType;
        }
    }

    /// <summary>
    /// Apply outfit at Start: either random (Uniform or Casual) or based on outfitType.
    /// </summary>
    private void ApplyOutfitAtStart()
    {
        if (renderersToChange == null)
            return;

        // Decide outfit type
        if (randomizeOutfitOnStart)
        {
            outfitType = (Random.value < 0.5f) ? OutfitType.Uniform : OutfitType.Casual;
        }

        ChangeOutfit(outfitType);
        prevOutfitType = outfitType;
    }

    /// <summary>
    /// Change outfit by setting material of each renderer in renderersToChange according to outfitType.
    /// </summary>
    public void ChangeOutfit(OutfitType type)
    {
        if (renderersToChange == null)
            return;

        Material chosenMat = null;
        switch (type)
        {
            case OutfitType.Uniform:
                chosenMat = uniformMaterial;
                if (chosenMat == null)
                    Debug.LogWarning("UniformMaterial is not assigned.");
                break;
            case OutfitType.Casual:
                chosenMat = casualMaterial;
                if (chosenMat == null)
                    Debug.LogWarning("CasualMaterial is not assigned.");
                break;
        }
        if (chosenMat == null)
            return;

        foreach (var rend in renderersToChange)
        {
            if (rend == null) continue;
            // Replace all material slots with the chosen material
            if (rend.sharedMaterials.Length == 1)
            {
                rend.sharedMaterial = chosenMat;
            }
            else
            {
                Material[] mats = new Material[rend.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    mats[i] = chosenMat;
                }
                rend.sharedMaterials = mats;
            }
        }
    }

    /// <summary>
    /// Pick one of pointLeft, pointStraight, pointRight at random (ignores nulls).
    /// </summary>
    private Transform PickRandomTargetPoint()
    {
        Transform[] options = new Transform[] { pointLeft, pointStraight, pointRight };
        var validOptions = new System.Collections.Generic.List<Transform>();
        foreach (var t in options)
        {
            if (t != null) validOptions.Add(t);
        }
        if (validOptions.Count == 0) return null;
        int index = Random.Range(0, validOptions.Count);
        return validOptions[index];
    }

    /// <summary>
    /// Starts movement towards the given world-space target position,
    /// using either Direct or Uncertain style depending on movementStyle.
    /// Cancels any existing Uncertain coroutine.
    /// </summary>
    private void StartMovementTo(Vector3 targetPosition)
    {
        // Stop existing uncertain coroutine if any
        if (uncertainMoveCoroutine != null)
        {
            StopCoroutine(uncertainMoveCoroutine);
            uncertainMoveCoroutine = null;
        }

        agent.isStopped = false;

        if (movementStyle == MovementStyle.Direct)
        {
            // Direct straight-line: just set destination & speed
            agent.speed = moveSpeed;
            agent.SetDestination(targetPosition);
        }
        else // Uncertain
        {
            // Start the Uncertain movement coroutine toward the same targetPosition
            uncertainMoveCoroutine = StartCoroutine(UncertainMoveCoroutine(targetPosition));
        }
    }

    /// <summary>
    /// Coroutine for Uncertain movement toward finalTarget:
    /// On the way to finalTarget, repeatedly:
    ///  - maybe pause,
    ///  - adjust agent.speed to a random slow factor of moveSpeed,
    ///  - pick an intermediate point offset from the direct path,
    ///  - move there, then repeat until close enough to finalTarget,
    /// then set destination to finalTarget and wait until arrival.
    /// This keeps the NPC roughly along the straight segment but with zig-zags, slow-downs, and pauses.
    /// </summary>
    private IEnumerator UncertainMoveCoroutine(Vector3 finalTarget)
    {
        // Loop until within stoppingDistance of finalTarget
        while (true)
        {
            if (agent == null) yield break;

            float distToFinal = Vector3.Distance(transform.position, finalTarget);
            if (distToFinal <= agent.stoppingDistance + 0.1f)
                break;

            // Possibly pause before next segment
            if (Random.value < pauseProbability)
            {
                agent.isStopped = true;
                float pauseDur = Random.Range(minPauseDuration, maxPauseDuration);
                yield return new WaitForSeconds(pauseDur);
                if (agent == null) yield break;
                agent.isStopped = false;
            }

            // Slow down: pick random factor
            float factor = Random.Range(slowSpeedFactorMin, slowSpeedFactorMax);
            agent.speed = moveSpeed * factor;

            // Compute a random intermediate point along/near the path
            Vector3 currentPos = transform.position;
            Vector3 dirToFinal = (finalTarget - currentPos).normalized;

            // Pick a segment distance along forward direction
            float segDist = Random.Range(minSegmentDistance, maxSegmentDistance);
            // Clamp so we don't overshoot final
            segDist = Mathf.Min(segDist, distToFinal);

            Vector3 basePoint = currentPos + dirToFinal * segDist;

            // Pick a lateral offset perpendicular to dirToFinal
            Vector3 perp = Vector3.Cross(dirToFinal, Vector3.up).normalized;
            float lateralOffset = Random.Range(-wanderRadius, wanderRadius);
            Vector3 intermediate = basePoint + perp * lateralOffset;

            // Try to set path to intermediate
            NavMeshPath path = new NavMeshPath();
            if (agent.CalculatePath(intermediate, path) && path.status == NavMeshPathStatus.PathComplete)
            {
                agent.SetPath(path);
            }
            else
            {
                // If invalid intermediate, fallback to heading partway directly
                Vector3 fallback = basePoint;
                agent.SetDestination(fallback);
            }

            // Wait until reaching that intermediate (or path invalidated)
            while (!agent.pathPending && agent.remainingDistance > agent.stoppingDistance)
            {
                // If movementStyle changed externally, exit coroutine
                yield return null;
            }

            // Loop continues until close to finalTarget
        }

        // Restore base speed and go straight to finalTarget
        if (agent == null) yield break;
        agent.speed = moveSpeed;
        agent.SetDestination(finalTarget);

        // Wait until arrival at finalTarget
        while (!agent.pathPending && agent.remainingDistance > agent.stoppingDistance)
        {
            yield return null;
        }

        // Done: coroutine ends. Arrival handling (stopping, state update) occurs in Update()
        uncertainMoveCoroutine = null;
    }

    /// <summary>
    /// Public method to reset the NPC back to start and begin again.
    /// Cancels any Uncertain coroutine and restarts movement to center.
    /// </summary>
    public void ResetAndStart()
    {
        if (agent == null)
            return;

        // Stop any uncertain coroutine
        if (uncertainMoveCoroutine != null)
        {
            StopCoroutine(uncertainMoveCoroutine);
            uncertainMoveCoroutine = null;
        }

        agent.isStopped = false;
        currentState = State.ToCenter;

        if (pointStart != null)
        {
            Vector3 startPos = GetOffsetNavMeshPosition(pointStart.position);
            transform.position = startPos;
        }
        if (pointCenter != null)
        {
            currentTargetPosition = GetOffsetNavMeshPosition(pointCenter.position);
            StartMovementTo(currentTargetPosition);
        }
    }

    /// <summary>
    /// Adds the overall path offset to the original position, then uses NavMesh.SamplePosition
    /// to find the nearest valid NavMesh point within sampleRadius. If sampling fails, returns the original position.
    /// </summary>
    private Vector3 GetOffsetNavMeshPosition(Vector3 original)
    {
        Vector3 desired = original + pathOffset;
        NavMeshHit hit;
        // If sampling succeeds, return hit.position; otherwise return original
        if (NavMesh.SamplePosition(desired, out hit, sampleRadius, NavMesh.AllAreas))
        {
            return hit.position;
        }
        else
        {
            // Optional warning:
            // Debug.LogWarning($"NPCMover: SamplePosition failed for desired={desired}, using original={original}");
            return original;
        }
    }
}
