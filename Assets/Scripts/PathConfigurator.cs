using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Configures paths for target and distractor groups.
/// Provides runtime values for NPCSpawner or NPCMoverOffset.
/// 
/// NOTE:
/// - PathParameters is defined as a nested, public, [System.Serializable] class so other
///   scripts can reference it as PathConfigurator.PathParameters (which GameManager and NPCSpawner do).
/// - This file preserves the original API: SetTargetPath, SetDistractorPath, ApplyConfiguration,
///   and getters for the parameters.
/// </summary>
public class PathConfigurator : MonoBehaviour
{
    /// <summary>
    /// Serializable container for per-path runtime parameters.
    /// This is nested so other scripts can refer to PathConfigurator.PathParameters.
    /// </summary>
    [System.Serializable]
    public class PathParameters
    {
        [Tooltip("Number of NPCs assigned to this path.")]
        public int npcNumber = 20;

        [Tooltip("Proportion (0..1) of NPCs on this path that are 'credible' (used to choose outfit).")]
        public float credibleProportion = 0f;

        [Tooltip("Direction-consistency interpreted as the proportion (0..1) used for splitting counts when applicable.")]
        public float directionConsistency = 0.75f;

        [Tooltip("Movement speed (m/s) used for NPCs on this path.")]
        public float speed = 1.4f;

        [Tooltip("Movement style for NPCs on this path (Direct or Uncertain).")]
        public NPCMoverOffset.MovementStyle movementStyle = NPCMoverOffset.MovementStyle.Direct;
    }

    // Exposed instances for target and distractor path parameters (visible in Inspector)
    public PathParameters targetPath = new PathParameters();
    public PathParameters distractorPath = new PathParameters();

    /// <summary>
    /// Set target path parameters from TrialScheduler.PathConfig
    /// </summary>
    public void SetTargetPath(TrialScheduler.PathConfig cfg)
    {
        if (cfg == null) return;

        targetPath.npcNumber = cfg.npcNumber;
        targetPath.credibleProportion = cfg.credibleProportion;
        targetPath.directionConsistency = cfg.directionConsistency;
        targetPath.speed = cfg.speed;
        targetPath.movementStyle = cfg.movementStyle; // directly copy MovementStyle
    }

    /// <summary>
    /// Set distractor path parameters from TrialScheduler.PathConfig
    /// </summary>
    public void SetDistractorPath(TrialScheduler.PathConfig cfg)
    {
        if (cfg == null) return;

        distractorPath.npcNumber = cfg.npcNumber;
        distractorPath.credibleProportion = cfg.credibleProportion;
        distractorPath.directionConsistency = cfg.directionConsistency;
        distractorPath.speed = cfg.speed;
        distractorPath.movementStyle = cfg.movementStyle; // directly copy MovementStyle
    }

    /// <summary>
    /// Any precomputation or debug logging at trial start
    /// </summary>
    public void ApplyConfiguration()
    {
        Debug.Log($"[PathConfigurator] Target: N={targetPath.npcNumber}, C={targetPath.credibleProportion}, D={targetPath.directionConsistency}, S={targetPath.speed}, Style={targetPath.movementStyle}");
        Debug.Log($"[PathConfigurator] Distractor: N={distractorPath.npcNumber}, C={distractorPath.credibleProportion}, D={distractorPath.directionConsistency}, S={distractorPath.speed}, Style={distractorPath.movementStyle}");
    }

    /// <summary>
    /// Helper methods for external scripts
    /// </summary>
    public PathParameters GetTargetParams() => targetPath;
    public PathParameters GetDistractorParams() => distractorPath;
}