using UnityEngine;
using UnityEngine.AI;
using System.Collections;

/// <summary>
/// NPCMoverOffset
/// - Controls an NPC moving Start -> Center -> (Left|Right)
/// - Two movement styles: Direct and Uncertain
///
/// Changes in this version:
/// - Introduces a smooth-turning step when switching from center -> left|right.
///   Instead of immediately setting the final left/right destination (which caused
///   abrupt, 'robotic' turns), we insert a turn waypoint ahead of the center that
///   lies on the bisector between approach and exit directions. The agent first
///   travels to that turn waypoint, then continues to the final target. This creates
///   a smoother, more natural curve in both Direct and Uncertain movement styles.
/// - Keeps the earlier fixes for Uncertain arrival detection (world-space distance)
///   and pronounced uncertain behaviour (nudges, backtracks, pauses).
/// - Adds inspector-exposed tuning for turn smoothing radius and optional override
///   to control how large the smoothing arc should be.
///
/// All comments are in English.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class NPCMoverOffset : MonoBehaviour
{
    [Header("Path Points")]
    public Transform pointStart;
    public Transform pointCenter;
    public Transform pointLeft;
    public Transform pointRight;

    [Header("Path Offset Settings")]
    public float pathOffsetRadius = 2.0f;

    [Header("Movement Settings")]
    public float moveSpeed = 1.4f;
    public float stoppingDistance = 0.0f;

    public enum MovementStyle { Direct, Uncertain }
    public MovementStyle movementStyle = MovementStyle.Direct;

    [Header("Uncertain Movement Parameters")]
    // Increased wander radius so lateral movement is more visible.
    public float wanderRadius = 0.5f;

    // Shorter segment distances so NPC frequently changes sub-targets (more jitter).
    public float minSegmentDistance = 3.0f;
    public float maxSegmentDistance = 5.0f;

    // Higher pause probability and longer pauses for obvious hesitation.
    [Range(0f, 1f)]
    public float pauseProbability = 0.5f;
    public float minPauseDuration = 0.5f;
    public float maxPauseDuration = 2.0f;

    // Bigger slow-speed range (0.2..0.9) to allow slow crawling and more visible speed changes.
    [Range(0f, 1f)]
    public float slowSpeedFactorMin = 0.80f;
    [Range(0f, 1f)]
    public float slowSpeedFactorMax = 1.00f;

    // Chance to occasionally backtrack (reverse-ish intermediate) to show indecision.
    [Range(0f, 1f)]
    public float backtrackChance = 0.18f;

    // Frequency for small in-motion "nudges" (seconds). Nudges add micro-lateral offsets while moving.
    public float nudgeMinInterval = 0.45f;
    public float nudgeMaxInterval = 1.2f;
    public float nudgeMagnitude = 0.6f; // how big each nudge is relative to wanderRadius

    // Linger on final arrival before destroy (seconds) to avoid abrupt disappearance.
    public float lingerOnArrival = 0.45f;

    [Header("Turn Smoothing")]
    // How far from the center the intermediate 'turn waypoint' will be placed.
    // Larger radius -> wider, gentler arcs. Small radius -> tighter turns.
    public float turnSmoothingRadius = 3.0f;

    // If true, the smoothing radius will be scaled automatically based on pathOffsetRadius
    // (keeps smoothing proportional to your random path offset).
    public bool autoScaleTurnRadius = true;

    public enum OutfitType { Uniform, Casual }

    [Header("Outfit Settings")]
    public OutfitType outfitType = OutfitType.Uniform;
    public bool randomizeOutfitOnStart = false;
    public Material uniformMaterial;
    public Material casualMaterial;
    public Renderer[] renderersToChange;

    // Trial / Recording fields still kept for metadata usage, but no DataRecorder reference
    [Header("Trial Metadata (optional)")]
    public int participantID = -1;
    public int trialID = -1;
    public bool landmarkIsLeft = false;

    private NavMeshAgent agent;
    private enum State { ToCenter, ToRandomTarget, Stopped }
    private State currentState = State.ToCenter;

    private OutfitType prevOutfitType;
    private MovementStyle prevMovementStyle;
    private Coroutine uncertainMoveCoroutine = null;
    private Coroutine pendingDestinationCoroutine = null;
    private Coroutine finalizePlacementCoroutine = null;
    private Coroutine destroyCoroutine = null;

    private Vector3 currentTargetPosition;
    private Vector3 pathOffset = Vector3.zero;
    private float sampleRadius = 2.5f;
    private Transform lastChosenTarget = null;

    // For turn smoothing: when we insert an intermediate waypoint (turn), we store the
    // intended final target here so that when the intermediate is reached we continue
    // to the final target.
    private Vector3 pendingFinalTarget = Vector3.zero;
    private bool isPerformingTurn = false;

    public bool isPlacedOnNavMesh { get; private set; } = false;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        if (agent == null)
        {
            Debug.LogError("[NPCMoverOffset] NavMeshAgent component missing.");
            return;
        }

        if (!agent.enabled) agent.enabled = true;

        if ((renderersToChange == null || renderersToChange.Length == 0))
        {
            var skinned = GetComponentsInChildren<SkinnedMeshRenderer>();
            if (skinned.Length > 0)
            {
                renderersToChange = skinned;
                Debug.Log($"[NPCMoverOffset] Auto-assigned {skinned.Length} SkinnedMeshRenderer(s).");
            }
        }

        TryPlaceAgentOnNavMesh(transform.position);
    }

    void OnEnable()
    {
        if (agent != null && !isPlacedOnNavMesh)
            TryPlaceAgentOnNavMesh(transform.position);
    }

    void Start()
    {
        if (agent == null) return;

        Vector2 rand2D = Random.insideUnitCircle * pathOffsetRadius;
        pathOffset = new Vector3(rand2D.x, 0f, rand2D.y);
        sampleRadius = Mathf.Max(pathOffsetRadius, 1.0f) + 0.5f;

        agent.speed = moveSpeed;
        if (stoppingDistance > 0f) agent.stoppingDistance = stoppingDistance;

        ApplyOutfitAtStart();
        prevOutfitType = outfitType;
        prevMovementStyle = movementStyle;

        if (pointStart != null)
        {
            Vector3 startPos = GetOffsetNavMeshPosition(pointStart.position);
            transform.position = startPos;
            TryPlaceAgentOnNavMesh(transform.position);
        }

        if (pointCenter != null)
        {
            currentState = State.ToCenter;
            currentTargetPosition = GetOffsetNavMeshPosition(pointCenter.position);
            agent.isStopped = false;
            StartMovementTo(currentTargetPosition);
        }
        else
        {
            Debug.LogWarning("[NPCMoverOffset] pointCenter is not assigned.");
            currentState = State.Stopped;
        }

        Debug.Log($"[NPCMoverOffset] START {name}: moveSpeed={moveSpeed:F2}, style={movementStyle}, isOnNavMesh={agent.isOnNavMesh}");
    }

    void Update()
    {
        if (agent == null) return;

        if ((currentState == State.ToCenter || currentState == State.ToRandomTarget)
            && agent.isOnNavMesh && agent.isStopped)
        {
            agent.isStopped = false;
            if (currentTargetPosition != Vector3.zero && agent.isOnNavMesh)
                agent.SetDestination(currentTargetPosition);
        }

        if (movementStyle == MovementStyle.Direct)
        {
            if (!Mathf.Approximately(agent.speed, moveSpeed))
            {
                agent.speed = moveSpeed;
            }
        }

        if (renderersToChange != null && outfitType != prevOutfitType)
        {
            ChangeOutfit(outfitType);
            prevOutfitType = outfitType;
        }

        if (movementStyle != prevMovementStyle)
        {
            if (uncertainMoveCoroutine != null)
            {
                StopCoroutine(uncertainMoveCoroutine);
                uncertainMoveCoroutine = null;
            }

            if (currentState == State.ToCenter || currentState == State.ToRandomTarget)
            {
                agent.isStopped = false;
                if (!agent.isOnNavMesh)
                    TryPlaceAgentOnNavMesh(transform.position);

                StartMovementTo(currentTargetPosition);
            }
            prevMovementStyle = movementStyle;
        }

        if ((currentState == State.ToCenter || currentState == State.ToRandomTarget) && agent.isOnNavMesh)
        {
            if (!agent.pathPending && agent.hasPath)
            {
                float arrivalTolerance = 0.5f;
                float arrivalThreshold = agent.stoppingDistance + arrivalTolerance;

                // Use distance from NPC to the intended final waypoint (currentTargetPosition).
                // This avoids false arrivals when the agent briefly reaches intermediate sub-paths.
                float distToIntendedTarget = (currentTargetPosition == Vector3.zero)
                    ? Mathf.Infinity
                    : Vector3.Distance(transform.position, currentTargetPosition);

                if (distToIntendedTarget <= arrivalThreshold)
                {
                    if (currentState == State.ToCenter)
                    {
                        // Arrived at center: pick left/right and insert a turn waypoint to smooth the corner.
                        Transform nextPoint = PickRandomTargetPoint();
                        if (nextPoint != null)
                        {
                            currentState = State.ToRandomTarget;

                            // Final target (left/right) with offset
                            Vector3 finalPos = GetOffsetNavMeshPosition(nextPoint.position);

                            // Compute a turn waypoint that lies ahead of center on the bisector of approach/exit.
                            Vector3 turnWaypoint = ComputeTurnWaypoint(pointCenter.position, finalPos, turnSmoothingRadius);

                            // If turnWaypoint couldn't be placed on navmesh, fall back to a point slightly ahead of center.
                            if (turnWaypoint == Vector3.zero)
                            {
                                Vector3 fallback = pointCenter.position + (finalPos - pointCenter.position).normalized * Mathf.Min(1.0f, turnSmoothingRadius);
                                Vector3 fallbackNav = GetOffsetNavMeshPosition(fallback);
                                if (fallbackNav != Vector3.zero) turnWaypoint = fallbackNav; else turnWaypoint = finalPos;
                            }

                            // Store the actual final target so that when the turn waypoint is reached we continue to it.
                            pendingFinalTarget = finalPos;
                            isPerformingTurn = true;

                            // Start movement to the turn waypoint (both Direct & Uncertain will honour this intermediate).
                            currentTargetPosition = turnWaypoint;
                            lastChosenTarget = nextPoint;

                            if (!agent.isOnNavMesh) TryPlaceAgentOnNavMesh(turnWaypoint);
                            agent.isStopped = false;
                            StartMovementTo(currentTargetPosition);
                        }
                        else
                        {
                            currentState = State.Stopped;
                            agent.isStopped = true;
                        }
                    }
                    else if (currentState == State.ToRandomTarget)
                    {
                        // When reaching the currentTargetPosition while ToRandomTarget, we need to check
                        // whether we are currently performing a turn (i.e., this arrival is the turn waypoint)
                        if (isPerformingTurn && pendingFinalTarget != Vector3.zero)
                        {
                            // We have reached the intermediate turn waypoint; now proceed to the real final target.
                            if (uncertainMoveCoroutine != null)
                            {
                                StopCoroutine(uncertainMoveCoroutine);
                                uncertainMoveCoroutine = null;
                            }

                            isPerformingTurn = false;
                            Vector3 nextFinal = pendingFinalTarget;
                            pendingFinalTarget = Vector3.zero;

                            currentTargetPosition = nextFinal;

                            if (!agent.isOnNavMesh) TryPlaceAgentOnNavMesh(nextFinal);
                            agent.isStopped = false;
                            StartMovementTo(currentTargetPosition);
                        }
                        else
                        {
                            // We have arrived at the final left/right target. Stop and destroy after linger.
                            if (uncertainMoveCoroutine != null)
                            {
                                StopCoroutine(uncertainMoveCoroutine);
                                uncertainMoveCoroutine = null;
                            }

                            agent.isStopped = true;
                            currentState = State.Stopped;

                            // Start a short linger coroutine before destroying to make ending less natural.
                            if (destroyCoroutine != null) StopCoroutine(destroyCoroutine);
                            destroyCoroutine = StartCoroutine(DestroyAfterDelay(lingerOnArrival));
                        }
                    }
                }
            }
            else
            {
                if (!agent.pathPending && !agent.hasPath)
                {
                    if (currentTargetPosition != Vector3.zero)
                        agent.SetDestination(currentTargetPosition);
                }
            }
        }
    }

    void OnValidate()
    {
        if (agent == null) agent = GetComponent<NavMeshAgent>();
        if (agent != null)
        {
            agent.speed = moveSpeed;
            if (stoppingDistance > 0f) agent.stoppingDistance = stoppingDistance;
        }

        if (!Application.isPlaying)
        {
            ChangeOutfit(outfitType);
            prevOutfitType = outfitType;
        }
    }

    private IEnumerator DestroyAfterDelay(float delay)
    {
        // linger for a short random amount around the configured linger (natural variation)
        float jitter = Random.Range(-0.15f, 0.15f);
        float t = Mathf.Max(0f, delay + jitter);
        yield return new WaitForSeconds(t);
        Destroy(gameObject);
    }

    private void ApplyOutfitAtStart()
    {
        if (renderersToChange == null) return;

        if (randomizeOutfitOnStart)
        {
            outfitType = (Random.value < 0.5f) ? OutfitType.Uniform : OutfitType.Casual;
        }

        ChangeOutfit(outfitType);
        prevOutfitType = outfitType;
    }

    public void ChangeOutfit(OutfitType type)
    {
        if (renderersToChange == null) return;

        Material chosenMat = null;
        switch (type)
        {
            case OutfitType.Uniform:
                chosenMat = uniformMaterial;
                if (chosenMat == null) Debug.LogWarning("[NPCMoverOffset] Uniform material not assigned.");
                break;
            case OutfitType.Casual:
                chosenMat = casualMaterial;
                if (chosenMat == null) Debug.LogWarning("[NPCMoverOffset] Casual material not assigned.");
                break;
        }
        if (chosenMat == null) return;

        foreach (var rend in renderersToChange)
        {
            if (rend == null) continue;
            if (rend.sharedMaterials.Length == 1)
            {
                rend.sharedMaterial = chosenMat;
            }
            else
            {
                Material[] mats = new Material[rend.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = chosenMat;
                rend.sharedMaterials = mats;
            }
        }
    }

    private Transform PickRandomTargetPoint()
    {
        var validOptions = new System.Collections.Generic.List<Transform>();
        if (pointLeft != null) validOptions.Add(pointLeft);
        if (pointRight != null) validOptions.Add(pointRight);

        if (validOptions.Count == 0) return null;
        if (validOptions.Count == 1)
        {
            lastChosenTarget = validOptions[0];
            return lastChosenTarget;
        }

        int index = Random.Range(0, validOptions.Count);
        lastChosenTarget = validOptions[index];
        return lastChosenTarget;
    }

    private void StartMovementTo(Vector3 targetPosition)
    {
        if (uncertainMoveCoroutine != null)
        {
            StopCoroutine(uncertainMoveCoroutine);
            uncertainMoveCoroutine = null;
        }

        agent.isStopped = false;
        currentTargetPosition = targetPosition;

        if (agent.isOnNavMesh)
        {
            if (movementStyle == MovementStyle.Direct)
            {
                agent.speed = moveSpeed;
                bool ok = agent.SetDestination(targetPosition);
                if (!ok)
                {
                    agent.ResetPath();
                    agent.SetDestination(targetPosition);
                }
            }
            else
            {
                uncertainMoveCoroutine = StartCoroutine(UncertainMoveCoroutine(targetPosition));
            }
        }
        else
        {
            TryPlaceAgentOnNavMesh(targetPosition);

            if (pendingDestinationCoroutine != null)
            {
                StopCoroutine(pendingDestinationCoroutine);
                pendingDestinationCoroutine = null;
            }
            pendingDestinationCoroutine = StartCoroutine(WaitAndSetDestination(targetPosition, 1.0f));
        }
    }

    private IEnumerator WaitAndSetDestination(Vector3 target, float timeoutSeconds)
    {
        float t = 0f;
        while (t < timeoutSeconds)
        {
            if (agent == null) yield break;
            if (isPlacedOnNavMesh && agent.isOnNavMesh)
                break;
            t += Time.deltaTime;
            yield return null;
        }

        if (agent != null && agent.isOnNavMesh)
        {
            if (movementStyle == MovementStyle.Direct)
            {
                agent.speed = moveSpeed;
                agent.SetDestination(target);
            }
            else
            {
                uncertainMoveCoroutine = StartCoroutine(UncertainMoveCoroutine(target));
            }
        }
        else
        {
            Debug.LogWarning($"[NPCMoverOffset] {name} WaitAndSetDestination timed out before agent became ready.");
        }

        pendingDestinationCoroutine = null;
    }

    private IEnumerator FinalizeAgentPlacementCoroutine(Vector3 warpPosition)
    {
        if (agent == null) yield break;

        if (!agent.enabled) agent.enabled = true;

        agent.Warp(warpPosition);
        agent.ResetPath();
        agent.isStopped = true;

        yield return null;

        agent.isStopped = false;

        isPlacedOnNavMesh = agent.isOnNavMesh;

        finalizePlacementCoroutine = null;
        Debug.Log($"[NPCMoverOffset] {name} FinalizeAgentPlacement completed. isOnNavMesh={agent.isOnNavMesh}");
    }

    private bool TryPlaceAgentOnNavMesh(Vector3 position)
    {
        if (agent == null) return false;

        if (agent.isOnNavMesh)
        {
            isPlacedOnNavMesh = true;
            return true;
        }

        NavMeshHit hit;
        float tryRadius = Mathf.Max(sampleRadius, 1.0f);

        if (NavMesh.SamplePosition(position, out hit, tryRadius, NavMesh.AllAreas))
        {
            if (finalizePlacementCoroutine != null) { StopCoroutine(finalizePlacementCoroutine); finalizePlacementCoroutine = null; }
            finalizePlacementCoroutine = StartCoroutine(FinalizeAgentPlacementCoroutine(hit.position));
            return true;
        }

        if (pointCenter != null && NavMesh.SamplePosition(pointCenter.position + pathOffset, out hit, tryRadius, NavMesh.AllAreas))
        {
            if (finalizePlacementCoroutine != null) { StopCoroutine(finalizePlacementCoroutine); finalizePlacementCoroutine = null; }
            finalizePlacementCoroutine = StartCoroutine(FinalizeAgentPlacementCoroutine(hit.position));
            return true;
        }
        if (pointLeft != null && NavMesh.SamplePosition(pointLeft.position + pathOffset, out hit, tryRadius, NavMesh.AllAreas))
        {
            if (finalizePlacementCoroutine != null) { StopCoroutine(finalizePlacementCoroutine); finalizePlacementCoroutine = null; }
            finalizePlacementCoroutine = StartCoroutine(FinalizeAgentPlacementCoroutine(hit.position));
            return true;
        }
        if (pointRight != null && NavMesh.SamplePosition(pointRight.position + pathOffset, out hit, tryRadius, NavMesh.AllAreas))
        {
            if (finalizePlacementCoroutine != null) { StopCoroutine(finalizePlacementCoroutine); finalizePlacementCoroutine = null; }
            finalizePlacementCoroutine = StartCoroutine(FinalizeAgentPlacementCoroutine(hit.position));
            return true;
        }

        return false;
    }

    private IEnumerator UncertainMoveCoroutine(Vector3 finalTarget)
    {
        // The uncertain routine produces a sequence of intermediate sub-targets that:
        // - are shorter than before (more frequent decision points)
        // - have larger lateral offsets
        // - occasionally backtrack
        // - include micro-nudges to make movement oscillatory / indecisive
        float nextNudgeTime = Time.time + Random.Range(nudgeMinInterval, nudgeMaxInterval);

        while (true)
        {
            if (agent == null) yield break;

            // distance to real final
            float distToFinal = Vector3.Distance(transform.position, finalTarget);

            // Check quick arrival conditions using world-space distance (robust against sub-paths)
            if (agent.isOnNavMesh && distToFinal <= agent.stoppingDistance + 0.1f)
                break;
            if (!agent.isOnNavMesh && distToFinal <= stoppingDistance + 0.1f)
                break;

            // Random pause to emulate thinking/hesitation
            if (Random.value < pauseProbability)
            {
                agent.isStopped = true;
                float pauseDur = Random.Range(minPauseDuration, maxPauseDuration);
                yield return new WaitForSeconds(pauseDur);

                if (agent == null) yield break;
                agent.isStopped = false;

                if (agent.isOnNavMesh)
                    agent.SetDestination(currentTargetPosition);
                else
                    TryPlaceAgentOnNavMesh(transform.position);
            }

            // Randomize forward segment length; sometimes backtrack a little to show indecision.
            float segDist = Random.Range(minSegmentDistance, maxSegmentDistance);
            bool doBacktrack = (Random.value < backtrackChance && distToFinal > segDist + 0.2f);
            if (doBacktrack)
            {
                // backtrack: move slightly backwards relative to forward direction
                segDist = -Random.Range(minSegmentDistance * 0.5f, Mathf.Min(maxSegmentDistance, distToFinal * 0.6f));
            }
            else
            {
                // clip forward segment so we don't overshoot final
                segDist = Mathf.Min(segDist, distToFinal);
            }

            // choose a lateral offset (bigger than before for visible weaving)
            Vector3 currentPos = transform.position;
            Vector3 dirToFinal = (finalTarget - currentPos).normalized;
            if (dirToFinal == Vector3.zero) dirToFinal = transform.forward;
            Vector3 basePoint = currentPos + dirToFinal * segDist;
            Vector3 perp = Vector3.Cross(dirToFinal, Vector3.up).normalized;
            float lateralScale = Random.Range(0.75f, 1.6f); // sometimes larger lateral displacement
            float lateralOffset = Random.Range(-wanderRadius, wanderRadius) * lateralScale;
            Vector3 intermediate = basePoint + perp * lateralOffset;

            // Make sure intermediate is on NavMesh (or use basePoint) and set path
            NavMeshPath path = new NavMeshPath();
            if (agent.isOnNavMesh && agent.CalculatePath(intermediate, path) && path.status == NavMeshPathStatus.PathComplete)
            {
                agent.isStopped = false;
                agent.SetPath(path);
            }
            else
            {
                // fallback: try basePoint or sample nearby
                if (agent.isOnNavMesh)
                {
                    agent.isStopped = false;
                    agent.SetDestination(basePoint);
                }
                else
                {
                    TryPlaceAgentOnNavMesh(transform.position);
                }
            }

            // Follow this sub-path but with periodic micro-nudges to the side to emphasize wobble
            while (true)
            {
                if (agent == null) yield break;

                // If movement style changed mid-way, exit
                if (movementStyle != MovementStyle.Uncertain) yield break;

                // If there is no path, try to reassign the intended target
                if (!agent.pathPending && !agent.hasPath)
                {
                    if (agent.isOnNavMesh) agent.SetDestination(currentTargetPosition);
                    else TryPlaceAgentOnNavMesh(transform.position);
                }

                // If we have a path, check whether we've reached this sub-path's end using agent.remainingDistance.
                if (!agent.pathPending && agent.hasPath)
                {
                    // A sub-path arrival shouldn't be treated as final arrival (Update handles final).
                    if (agent.remainingDistance <= agent.stoppingDistance)
                    {
                        // break out to choose a new intermediate sub-target
                        break;
                    }
                }

                // If agent is stopped unexpectedly, resume and push towards the main currentTargetPosition
                if (agent.isStopped)
                {
                    agent.isStopped = false;
                    if (agent.isOnNavMesh) agent.SetDestination(currentTargetPosition);
                }

                // Micro-nudge logic: occasionally redirect slightly to produce lateral wobble
                if (Time.time >= nextNudgeTime)
                {
                    nextNudgeTime = Time.time + Random.Range(nudgeMinInterval, nudgeMaxInterval);

                    if (agent != null && agent.isOnNavMesh)
                    {
                        // pick a small lateral nudge near current destination to create oscillation
                        Vector3 toFinal = (finalTarget - transform.position).normalized;
                        if (toFinal == Vector3.zero) toFinal = transform.forward;
                        Vector3 nudgePerp = Vector3.Cross(toFinal, Vector3.up).normalized;
                        float nudgeDir = (Random.value < 0.5f) ? -1f : 1f;
                        Vector3 nudgeTarget = transform.position + toFinal * Mathf.Clamp(segDist * 0.4f, 0.6f, 1.5f) + nudgePerp * (nudgeMagnitude * nudgeDir);

                        // Try to sample navmesh near nudgeTarget for validity
                        NavMeshHit nudgeHit;
                        if (NavMesh.SamplePosition(nudgeTarget, out nudgeHit, Mathf.Max(1.0f, nudgeMagnitude), NavMesh.AllAreas))
                        {
                            // Set a short path to the nudge point (this yields a visible wobble)
                            NavMeshPath nudgePath = new NavMeshPath();
                            if (agent.CalculatePath(nudgeHit.position, nudgePath) && nudgePath.status == NavMeshPathStatus.PathComplete)
                            {
                                agent.SetPath(nudgePath);
                            }
                        }
                        else
                        {
                            // If nudge sampling failed, just reassign the main target (safe fallback)
                            agent.SetDestination(currentTargetPosition);
                        }
                    }
                }

                // Small dynamic speed modulation for the sub-segment (stronger variation than before)
                float factor = Random.Range(slowSpeedFactorMin, slowSpeedFactorMax);
                agent.speed = moveSpeed * factor;

                yield return null;
            } // end follow sub-path loop
        } // end while not reached final

        // Ensure agent restores normal speed and is directed to final.
        if (agent == null) yield break;
        agent.speed = moveSpeed;
        agent.isStopped = false;

        if (agent.isOnNavMesh)
            agent.SetDestination(finalTarget);
        else
            TryPlaceAgentOnNavMesh(transform.position);

        // Wait for final arrival (Update will check world-space distance and handle destruction)
        while (true)
        {
            if (agent == null) yield break;

            if (agent.isOnNavMesh && !agent.pathPending && agent.hasPath)
            {
                if (agent.remainingDistance <= agent.stoppingDistance)
                    break;
            }
            else if (!agent.hasPath && !agent.pathPending)
            {
                if (agent.isOnNavMesh) agent.SetDestination(finalTarget);
                else TryPlaceAgentOnNavMesh(transform.position);
            }

            if (agent.isStopped)
            {
                agent.isStopped = false;
                if (agent.isOnNavMesh) agent.SetDestination(finalTarget);
            }

            yield return null;
        }

        uncertainMoveCoroutine = null;
    }

    /// <summary>
    /// Compute a turn waypoint that helps smooth a corner between the approach direction
    /// (towards center) and the exit direction (center -> final). The waypoint is placed
    /// on the bisector of the two directions at a distance controlled by turnSmoothingRadius.
    /// The result is sampled onto the NavMesh; if sampling fails Vector3.zero is returned.
    /// </summary>
    private Vector3 ComputeTurnWaypoint(Vector3 centerWorldPos, Vector3 finalWorldPos, float smoothingRadius)
    {
        if (agent == null) return Vector3.zero;

        // Determine approach direction: take current agent velocity if available, else use forward.
        Vector3 approachDir = agent.velocity.sqrMagnitude > 0.001f ? agent.velocity.normalized : transform.forward;
        approachDir.y = 0f;
        if (approachDir == Vector3.zero) approachDir = (centerWorldPos - transform.position).normalized;
        approachDir.y = 0f;

        // Determine exit direction from center to final
        Vector3 exitDir = (finalWorldPos - centerWorldPos);
        exitDir.y = 0f;
        if (exitDir.sqrMagnitude < 0.001f) exitDir = transform.forward;
        exitDir.Normalize();

        // Compute bisector direction
        Vector3 bisector = (approachDir + exitDir).normalized;
        if (bisector == Vector3.zero)
        {
            // If approach and exit are nearly opposite, pick a perpendicular to exit for a smooth corner
            bisector = Vector3.Cross(exitDir, Vector3.up).normalized;
        }

        // Optionally scale smoothing radius based on pathOffsetRadius so small prefabs still get gentle turns
        float effectiveRadius = smoothingRadius;
        if (autoScaleTurnRadius)
            effectiveRadius *= Mathf.Max(0.6f, pathOffsetRadius);

        Vector3 candidate = centerWorldPos + bisector * effectiveRadius;

        // Sample candidate onto NavMesh
        NavMeshHit hit;
        if (NavMesh.SamplePosition(candidate, out hit, Mathf.Max(1.0f, effectiveRadius * 0.6f), NavMesh.AllAreas))
        {
            return hit.position;
        }

        // If sampling failed, try a point slightly in front of center towards exit
        Vector3 fallback = centerWorldPos + exitDir * Mathf.Clamp(effectiveRadius * 0.5f, 0.5f, effectiveRadius);
        if (NavMesh.SamplePosition(fallback, out hit, 1.0f, NavMesh.AllAreas))
            return hit.position;

        return Vector3.zero;
    }

    private Vector3 GetOffsetNavMeshPosition(Vector3 original)
    {
        Vector3 desired = original + pathOffset;
        NavMeshHit hit;
        if (NavMesh.SamplePosition(desired, out hit, sampleRadius, NavMesh.AllAreas))
        {
            return hit.position;
        }
        else
        {
            return original;
        }
    }
}