using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// EnvironmentManager
/// - Responsible for configuring the environment for each trial:
///     - Landmark placement (via LandmarkSpawner)
///     - Path setup (via PathConfigurator)
/// 
/// NOTE:
/// - GetLandmarkX() has been removed because per-NPC choice recording (which relied on
///   final NPC X coordinate vs landmark X) has been removed from the experiment.
/// - Keep ConfigureTrial(ts) to set up landmark side and path parameters for each trial.
/// </summary>
public class EnvironmentManager : MonoBehaviour
{
    [Header("Scene References")]
    public LandmarkSpawner landmarkSpawner;   // Responsible for placing landmarks in the scene
    public PathConfigurator pathConfigurator; // Responsible for configuring path geometry/waypoints
    public Transform landmarkTransform; // optional reference to the landmark Transform (kept for convenience)

    /// <summary>
    /// Apply trial-specific environment settings.
    /// Called by GameManager at the start of each trial.
    /// </summary>
    /// <param name="ts">Trial settings containing environment-related parameters</param>
    public void ConfigureTrial(TrialScheduler.TrialSettings ts)
    {
        Debug.Log($"[EnvironmentManager] Configuring Trial {ts.trialID} - landmarkLeft={ts.landmarkLeft}");

        // 1. Set the landmark side (left/right)
        if (landmarkSpawner != null)
        {
            landmarkSpawner.SetLandmarkSide(ts.landmarkLeft);
        }
        else
        {
            Debug.LogWarning("[EnvironmentManager] landmarkSpawner not assigned.");
        }

        // 2. Configure paths (geometry/waypoints) using the PathConfigurator
        if (pathConfigurator != null)
        {
            pathConfigurator.SetTargetPath(ts.targetPath);       // Only path geometry, not NPC behavior
            pathConfigurator.SetDistractorPath(ts.distractorPath);
            pathConfigurator.ApplyConfiguration();               // Prepare runtime path data
        }
        else
        {
            Debug.LogWarning("[EnvironmentManager] pathConfigurator not assigned.");
        }
    }

    // NOTE:
    // - GetLandmarkX() removed intentionally because NPC per-choice recording was removed.
    // - If in the future you need landmark X for visualization or other logic, re-add a focused accessor here.
}