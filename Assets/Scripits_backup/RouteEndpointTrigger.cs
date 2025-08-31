using UnityEngine;

/// <summary>
/// Call RouteSelectionTracker when player enters this endpoint trigger.
/// </summary>
public class RouteEndpointTrigger : MonoBehaviour
{
    [Tooltip("Label for this route, e.g., \"Left\", \"Right\"...")]
    public string routeLabel = "Left";

    void OnTriggerEnter(Collider other)
    {
        // Adjust tag or detection according to your player setup
        if (other.CompareTag("Player"))
        {
            RouteSelectionTracker.Instance.SetChosenRoute(routeLabel);
        }
    }
}
