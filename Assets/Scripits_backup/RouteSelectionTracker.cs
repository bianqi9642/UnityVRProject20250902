using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Singleton to track the participant’s path and route choice in a trial.
/// 
/// Usage:
/// - Other scripts (e.g., attached to XR Rig) should call AddPoint(position) each frame (or at some interval).
/// - Scene route-end triggers should detect collision with the player and call SetChosenRoute(label).
/// - ExperimentManager will query HasChosenRoute() / GetChosenRoute() to know when to end trial.
/// </summary>
public class RouteSelectionTracker : MonoBehaviour
{
    public static RouteSelectionTracker Instance;

    // Recorded path points
    private List<Vector3> pathPoints = new List<Vector3>();

    // The label of the chosen route (set once when player enters a route endpoint trigger)
    private string chosenRoute = null;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(this.gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Update()
    {
        // Optionally record the participant's current position each frame.
        // You need to have a reference to the participant (XR Rig or camera). 
        // For example, if this script has a public Transform participant, you can call AddPoint(participant.position).
        // Alternatively, your ExperimentManager or another script can push positions into this tracker.
    }

    /// <summary>
    /// Add a position sample to the path.
    /// Call this from participant movement script each frame or at fixed intervals.
    /// </summary>
    public void AddPoint(Vector3 pos)
    {
        pathPoints.Add(pos);
    }

    /// <summary>
    /// Reset path and choice at the start of each trial.
    /// </summary>
    public void Reset()
    {
        pathPoints.Clear();
        chosenRoute = null;
    }

    /// <summary>
    /// Record the chosen route label, only the first time it's set.
    /// Route endpoints in scene should call this when player enters trigger.
    /// </summary>
    public void SetChosenRoute(string label)
    {
        if (chosenRoute == null)
        {
            chosenRoute = label;
            Debug.Log($"[RouteSelectionTracker] Route chosen: {label}");
        }
    }

    /// <summary>
    /// Whether a route has been chosen (player reached some endpoint).
    /// </summary>
    public bool HasChosenRoute()
    {
        return chosenRoute != null;
    }

    /// <summary>
    /// Get chosen route label.
    /// </summary>
    public string GetChosenRoute()
    {
        return chosenRoute;
    }

    /// <summary>
    /// Get chosen route or default if none.
    /// </summary>
    public string GetChosenRouteOrDefault(string def)
    {
        return chosenRoute ?? def;
    }

    /// <summary>
    /// Serialize pathPoints to a string, e.g. "x1,y1,z1; x2,y2,z2; ..."
    /// </summary>
    public string GetPathDataString()
    {
        StringBuilder sb = new StringBuilder();
        foreach (var p in pathPoints)
        {
            sb.AppendFormat("{0:F2},{1:F2},{2:F2};", p.x, p.y, p.z);
        }
        return sb.ToString();
    }
}
