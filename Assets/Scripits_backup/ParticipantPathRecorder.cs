using UnityEngine;

/// <summary>
/// Attach to the participant XR Rig (or another manager). Records position each frame when trialRunning is true.
/// ExperimentManager can set a public flag to indicate recording is active.
/// </summary>
public class ParticipantPathRecorder : MonoBehaviour
{
    public Transform participantTransform;
    public ExperimentManagerBackup experimentManager; // reference to manager to check trialRunning

    void Update()
    {
        if (participantTransform == null || experimentManager == null) return;
        // Suppose ExperimentManager exposes a public property IsTrialRunning
        // if (experimentManager.IsTrialRunning)
        // {
        //     RouteSelectionTracker.Instance.AddPoint(participantTransform.position);
        // }
        // Alternatively, ExperimentManager already records inside its coroutine.
    }
}
