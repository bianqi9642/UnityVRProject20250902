using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPCAlignedOffsetter:
/// - Computes a random offset for Point_Center: (offsetX, offsetZ) in XZ plane.
/// - Clones each original waypoint (Point_Start, Point_Center, Point_Left, Point_Straight, Point_Right)
///   as child GameObjects with adjusted positions:
///     * Center: original + (offsetX, 0, offsetZ)
///     * Start & Straight: original + (offsetX, 0, 0)  (z unchanged)
///     * Left & Right: original + (0, 0, offsetZ)      (x unchanged)
/// - Assigns these cloned Transforms back into the NPCMover fields so movement uses them.
/// - Optionally samples NavMesh to ensure new point is on walkable area.
/// 
/// Usage:
/// - Ensure your NPCMover (or equivalent) has its waypoint fields assigned to shared Transforms in the scene.
/// - Add this script component on the same GameObject as NPCMover.
/// - Optionally adjust offsetRadius and sampling settings in Inspector.
/// - In Awake(), this script clones and reassigns the waypoints before NPCMover.Start() uses them.
/// </summary>
public class NPCRandomPathOffset : MonoBehaviour
{
    [Header("Reference to Movement Script")]
    [Tooltip("Reference to the NPCMover (or whichever script) that has pointStart, pointCenter, pointLeft/Straight/Right Transforms.")]
    public NPCMover npcMover;

    [Header("Offset Settings")]
    [Tooltip("Maximum radius for random offset in XZ plane for Point_Center.")]
    public float offsetRadius = 1.0f;

    [Tooltip("If true, sample the NavMesh to project cloned points onto walkable areas. If sampling fails, fallback to original position.")]
    public bool samplingNavMesh = true;

    [Tooltip("Max distance for NavMesh.SamplePosition when projecting offset point.")]
    public float sampleDistance = 1.0f;

    // Store the cloned/transformed waypoint Transforms so they persist under this NPC
    private Transform offsetPointStart;
    private Transform offsetPointCenter;
    private Transform offsetPointLeft;
    private Transform offsetPointStraight;
    private Transform offsetPointRight;

    void Awake()
    {
        // Find npcMover if not assigned
        if (npcMover == null)
        {
            npcMover = GetComponent<NPCMover>();
            if (npcMover == null)
            {
                Debug.LogWarning("[NPCAlignedOffsetter] No NPCMover assigned or found. Disabling.");
                enabled = false;
                return;
            }
        }

        // Generate a random offset in XZ plane within circle of radius offsetRadius
        Vector2 rand2D = Random.insideUnitCircle * offsetRadius;
        float offsetX = rand2D.x;
        float offsetZ = rand2D.y;

        // Clone and offset each waypoint if assigned
        // Center: original + (offsetX, 0, offsetZ)
        if (npcMover.pointCenter != null)
        {
            offsetPointCenter = CreateAlignedOffsetWaypoint(
                "PointCenter_Offset", 
                npcMover.pointCenter, 
                new Vector3(offsetX, 0f, offsetZ)
            );
        }

        // Start: original + (offsetX, 0, 0)
        if (npcMover.pointStart != null)
        {
            offsetPointStart = CreateAlignedOffsetWaypoint(
                "PointStart_Offset",
                npcMover.pointStart,
                new Vector3(offsetX, 0f, 0f)
            );
        }

        // Straight: original + (offsetX, 0, 0)
        if (npcMover.pointStraight != null)
        {
            offsetPointStraight = CreateAlignedOffsetWaypoint(
                "PointStraight_Offset",
                npcMover.pointStraight,
                new Vector3(offsetX, 0f, 0f)
            );
        }

        // Left: original + (0, 0, offsetZ)
        if (npcMover.pointLeft != null)
        {
            offsetPointLeft = CreateAlignedOffsetWaypoint(
                "PointLeft_Offset",
                npcMover.pointLeft,
                new Vector3(0f, 0f, offsetZ)
            );
        }

        // Right: original + (0, 0, offsetZ)
        if (npcMover.pointRight != null)
        {
            offsetPointRight = CreateAlignedOffsetWaypoint(
                "PointRight_Offset",
                npcMover.pointRight,
                new Vector3(0f, 0f, offsetZ)
            );
        }

        // Assign clones back into npcMover so movement uses them
        if (offsetPointCenter != null)
            npcMover.pointCenter = offsetPointCenter;
        if (offsetPointStart != null)
            npcMover.pointStart = offsetPointStart;
        if (offsetPointStraight != null)
            npcMover.pointStraight = offsetPointStraight;
        if (offsetPointLeft != null)
            npcMover.pointLeft = offsetPointLeft;
        if (offsetPointRight != null)
            npcMover.pointRight = offsetPointRight;
    }

    /// <summary>
    /// Creates a new child GameObject under this NPC, named as given, at position = original.position + delta.
    /// If samplingNavMesh is true, attempts to project onto NavMesh within sampleDistance; if fails, uses original.position.
    /// Returns the Transform of the new child.
    /// </summary>
    private Transform CreateAlignedOffsetWaypoint(string name, Transform original, Vector3 delta)
    {
        Vector3 desiredPos = original.position + delta;
        Vector3 finalPos = desiredPos;

        if (samplingNavMesh)
        {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(desiredPos, out hit, sampleDistance, NavMesh.AllAreas))
            {
                finalPos = hit.position;
            }
            else
            {
                Debug.LogWarningFormat("[NPCAlignedOffsetter] Failed NavMesh sampling for {0} offset. Using original.", original.name);
                finalPos = original.position;
            }
        }

        GameObject go = new GameObject(name);
        go.transform.parent = this.transform;
        go.transform.position = finalPos;
        go.transform.rotation = original.rotation; // preserve orientation if needed
        return go.transform;
    }
}
