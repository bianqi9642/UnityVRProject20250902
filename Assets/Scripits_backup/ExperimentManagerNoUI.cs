using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// ExperimentManager orchestrates the VR experiment:
/// - Allows the experimenter to select one variable group (or All) via Inspector before starting.
/// - Builds trial configurations only for the selected group (or all groups if “All”), ordering trials by Latin square to counterbalance.
/// - For All groups: also orders group blocks by Latin square shift.
/// - For each trial: shows prompt, configures NPCSpawner, resets participant position, records path & route choice, logs results, cleans up.
/// 
/// Attach this script to a GameObject in your scene (e.g., an empty "ExperimentManager").
/// Assign references in Inspector: NPCSpawner, UI text for prompts, participant Transform, etc.
/// Ensure participantID ends with a number (e.g., Sub01) so that Latin-square shift can be derived automatically.
/// </summary>
public class ExperimentManagerNoUI : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Reference to the NPCSpawner in scene.")]
    public NPCSpawner npcSpawner;
    [Tooltip("Text UI in world-space Canvas for prompts.")]
    public Text uiText;
    [Tooltip("Participant XR Rig or main camera transform.")]
    public Transform participant;

    [Header("Trial Settings")]
    [Tooltip("Maximum duration for each trial in seconds.")]
    public float trialDuration = 60f;
    [Tooltip("Participant ID, should end with a number for Latin square shift, e.g., Sub01.")]
    public string participantID = "Sub01";

    // Enum for selecting which group to run
    public enum ExperimentGroup
    {
        All = -1,
        NPCCount = 0,
        UniformRatio = 1,
        DestinationRatio = 2,
        MoveSpeed = 3,
        DirectRatio = 4
    }

    [Header("Experiment Group Selection")]
    [Tooltip("Select which variable group to run; All = run every group mixed.")]
    public ExperimentGroup selectedGroup = ExperimentGroup.All;

    // Internal config and state
    private ExperimentConfig config;
    private int currentTrialIdx = 0;
    private bool trialRunning = false;
    private float trialStartTime;

    // Logging
    private StreamWriter logWriter;
    private string logFilePath;

    void Start()
    {
        // Basic checks
        if (npcSpawner == null)
        {
            Debug.LogError("[ExperimentManager] NPCSpawner reference is missing.");
            return;
        }
        if (participant == null)
        {
            Debug.LogError("[ExperimentManager] Participant transform reference is missing.");
            return;
        }
        if (uiText == null)
        {
            Debug.LogWarning("[ExperimentManager] UI Text reference is missing. Prompts will not show.");
        }

        // Determine Latin square shift index from participantID trailing number
        int latinShift = ParseParticipantNumber(participantID) - 1;
        if (latinShift < 0) latinShift = 0;

        // Initialize experiment config based on selectedGroup and latinShift
        config = new ExperimentConfig(selectedGroup, latinShift);

        // Initialize log file
        string fileName = $"ExperimentLog_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        logFilePath = Path.Combine(Application.persistentDataPath, fileName);
        logWriter = new StreamWriter(logFilePath, false);
        // Write CSV header
        logWriter.WriteLine("Timestamp,ParticipantID,TrialGlobalIndex,GroupID,TrialIndexInGroup,NPCCount,UniformRatio,DestinationLeftRatio,DestinationRightRatio,MoveSpeed,DirectRatio,ChosenRoute,TimeTaken,PathData");
        logWriter.Flush();

        Debug.Log($"[ExperimentManager] Logging to: {logFilePath}");
        Debug.Log($"[ExperimentManager] SelectedGroup = {selectedGroup}, latinShift = {latinShift}. Total trials: {config.allTrials.Count}");

        // Reset any previous route selection tracking
        if (RouteSelectionTracker.Instance != null)
        {
            RouteSelectionTracker.Instance.Reset();
        }

        // Start the first trial prompt coroutine
        StartCoroutine(RunNextTrialWithPrompt());
    }

    /// <summary>
    /// Parse trailing digits from participantID. Returns parsed integer or 1 if none found.
    /// </summary>
    private int ParseParticipantNumber(string pid)
    {
        if (string.IsNullOrEmpty(pid)) return 1;
        Match m = Regex.Match(pid, "(\\d+)$");
        if (m.Success)
        {
            if (int.TryParse(m.Groups[1].Value, out int num))
            {
                return num;
            }
        }
        return 1;
    }

    /// <summary>
    /// Coroutine to show prompt before each trial, wait for confirmation, then run trial.
    /// </summary>
    private IEnumerator RunNextTrialWithPrompt()
    {
        if (currentTrialIdx >= config.allTrials.Count)
        {
            // All trials done
            if (uiText != null)
                uiText.text = "Experiment complete. Thank you!";
            logWriter?.Close();
            yield break;
        }

        // Show prompt: which trial number out of total
        if (uiText != null)
            uiText.text = $"Get ready for Trial {currentTrialIdx + 1}/{config.allTrials.Count}. Press button to start.";
        yield return StartCoroutine(WaitForConfirm());

        // Run the trial
        TrialParameters tp = config.allTrials[currentTrialIdx];
        yield return StartCoroutine(RunTrial(tp));

        // After trial ends: prompt for break
        if (uiText != null)
            uiText.text = "Trial finished. Take a short break. Press button to continue.";
        yield return StartCoroutine(WaitForConfirm());

        currentTrialIdx++;
        // Reset path tracker for next trial
        if (RouteSelectionTracker.Instance != null)
            RouteSelectionTracker.Instance.Reset();

        // Next trial
        yield return StartCoroutine(RunNextTrialWithPrompt());
    }

    /// <summary>
    /// Wait until the participant presses a designated button/key.
    /// Replace Input condition with VR controller input if needed.
    /// </summary>
    private IEnumerator WaitForConfirm()
    {
        bool confirmed = false;
        while (!confirmed)
        {
            // Example: space key for testing in Editor. Replace with appropriate VR input.
            if (Input.GetKeyDown(KeyCode.Space))
            {
                confirmed = true;
            }
            yield return null;
        }
    }

    /// <summary>
    /// Executes a single trial:
    /// - Configures NPCSpawner parameters and spawns NPCs.
    /// - Resets participant position.
    /// - Records path & waits for route choice or timeout.
    /// - Logs results.
    /// - Cleans up NPCs.
    /// </summary>
    private IEnumerator RunTrial(TrialParameters tp)
    {
        trialRunning = true;
        trialStartTime = Time.time;

        // 1. Configure NPCSpawner according to trial parameters
        npcSpawner.npcCount = tp.npcCount;
        npcSpawner.uniformRatio = tp.uniformRatio;
        npcSpawner.leftRatio = tp.destinationLeftRatio;
        npcSpawner.rightRatio = tp.destinationRightRatio;
        npcSpawner.moveSpeed = tp.moveSpeed;
        npcSpawner.directRatio = tp.directRatio;

        // 2. Spawn NPCs
        npcSpawner.SpawnNPCs();

        // 3. Reset participant position to start location
        ResetParticipantPosition();

        // 4. Show “in-trial” prompt
        if (uiText != null)
            uiText.text = "Trial in progress...";

        // 5. Record path: add participant.position each frame (or at intervals)
        while (trialRunning)
        {
            if (RouteSelectionTracker.Instance != null)
                RouteSelectionTracker.Instance.AddPoint(participant.position);

            // Check for timeout
            if (Time.time - trialStartTime >= trialDuration)
            {
                break;
            }
            // Check for route chosen by participant
            if (RouteSelectionTracker.Instance != null && RouteSelectionTracker.Instance.HasChosenRoute())
            {
                break;
            }
            yield return null;
        }

        // 6. Compute results
        float timeTaken = Time.time - trialStartTime;
        string chosenRoute = RouteSelectionTracker.Instance != null
            ? RouteSelectionTracker.Instance.GetChosenRouteOrDefault("Timeout")
            : "Unknown";

        string pathData = RouteSelectionTracker.Instance != null
            ? RouteSelectionTracker.Instance.GetPathDataString()
            : "";

        // 7. Log to CSV
        string timestamp = DateTime.Now.ToString("o");
        string line = $"{timestamp},{participantID},{currentTrialIdx},{tp.groupID},{tp.trialIndexInGroup},{tp.npcCount},{tp.uniformRatio},{tp.destinationLeftRatio},{tp.destinationRightRatio},{tp.moveSpeed},{tp.directRatio},{chosenRoute},{timeTaken:F2},\"{pathData}\"";
        logWriter.WriteLine(line);
        logWriter.Flush();

        // 8. Cleanup NPCs
        npcSpawner.CleanupNPCs();

        trialRunning = false;
        yield break;
    }

    /// <summary>
    /// Reset participant position & orientation to the designated start location.
    /// Currently resets to world origin; modify if you have a specific start Transform.
    /// </summary>
    private void ResetParticipantPosition()
    {
        Vector3 startPos = Vector3.zero;
        Quaternion startRot = Quaternion.identity;
        participant.SetPositionAndRotation(startPos, startRot);
        // If using CharacterController or Rigidbody, consider resetting velocity here.
    }

    void OnApplicationQuit()
    {
        logWriter?.Close();
    }

    #region ExperimentConfig and TrialParameters

    /// <summary>
    /// Stores parameters for one trial.
    /// </summary>
    [Serializable]
    public class TrialParameters
    {
        public int groupID;               // 0~4, which variable group this trial belongs to
        public int trialIndexInGroup;     // index within its group
        public int npcCount;
        public float uniformRatio;
        public float destinationLeftRatio;
        public float destinationRightRatio;
        public float moveSpeed;
        public float directRatio;
    }

    /// <summary>
    /// Builds trial lists for selected group or all groups. Orders each group's trials by Latin square shift,
    /// and if All groups, orders group blocks by Latin square shift as well.
    /// </summary>
    private class ExperimentConfig
    {
        public List<TrialParameters> allTrials;

        // Baseline constants (adjust as needed)
        private int baselineCount = 20;
        private float baselineUniform = 0f;
        private float baselineLeft = 0.50f;
        private float baselineRight = 0.50f;
        private float baselineSpeed = 1.4f;
        private float baselineDirect = 1.0f;

        // Value arrays for each group
        private int[] countValues = new int[] { 5, 10, 15, 20, 25 };
        private float[] uniformValues = new float[] { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f };
        private float[] destinationLeftValues = new float[] { 0.50f, 0.60f, 0.70f, 0.80f, 0.90f, 1.0f };
        private float[] destinationRightValues = new float[] { 0.50f, 0.40f, 0.30f, 0.20f, 0.10f, 0f };
        private float[] speedValues = new float[] { 0.6f, 1.0f, 1.4f, 1.8f, 2.2f };
        private float[] directValues = new float[] { 0f, 1.0f };

        /// <summary>
        /// Constructor: builds trials only for the specified group (or all if selectedGroup == All), using latinShift.
        /// </summary>
        /// <param name="selGroup">Enum indicating which group to generate (-1 for All).</param>
        /// <param name="latinShift">Shift index for Latin square ordering.</param>
        public ExperimentConfig(ExperimentGroup selGroup, int latinShift)
        {
            allTrials = new List<TrialParameters>();
            int selected = (int)selGroup; // -1 for All, or 0-4 for specific group

            if (selected == -1)
            {
                // All groups: order group blocks by Latin square cyclic shift
                int numGroups = 5;
                for (int g = 0; g < numGroups; g++)
                {
                    int groupID = (latinShift + g) % numGroups; // cyclic group order
                    AddGroupTrials(groupID, latinShift);
                }
            }
            else
            {
                // Single selected group: just add that group's trials in Latin order
                AddGroupTrials(selected, latinShift);
            }
        }

        /// <summary>
        /// Add trials for a given groupID, ordering trials by Latin square shift.
        /// </summary>
        private void AddGroupTrials(int groupID, int latinShift)
        {
            switch (groupID)
            {
                case 0: // NPC Count group
                    AddLatinOrderedTrials(countValues.Length, latinShift, (i, idx) => new TrialParameters
                    {
                        groupID = 0,
                        trialIndexInGroup = idx,
                        npcCount = countValues[i],
                        uniformRatio = baselineUniform,
                        destinationLeftRatio = baselineLeft,
                        destinationRightRatio = baselineRight,
                        moveSpeed = baselineSpeed,
                        directRatio = baselineDirect
                    });
                    break;
                case 1: // Uniform Ratio group
                    AddLatinOrderedTrials(uniformValues.Length, latinShift, (i, idx) => new TrialParameters
                    {
                        groupID = 1,
                        trialIndexInGroup = idx,
                        npcCount = baselineCount,
                        uniformRatio = uniformValues[i],
                        destinationLeftRatio = baselineLeft,
                        destinationRightRatio = baselineRight,
                        moveSpeed = baselineSpeed,
                        directRatio = baselineDirect
                    });
                    break;
                case 2: // Destination Ratio group
                    AddLatinOrderedTrials(destinationLeftValues.Length, latinShift, (i, idx) =>
                    {
                        float left = destinationLeftValues[i];
                        float right = destinationRightValues[i];
                        if (left + right > 1f)
                        {
                            float sum = left + right;
                            left /= sum;
                            right /= sum;
                        }
                        return new TrialParameters
                        {
                            groupID = 2,
                            trialIndexInGroup = idx,
                            npcCount = baselineCount,
                            uniformRatio = baselineUniform,
                            destinationLeftRatio = left,
                            destinationRightRatio = right,
                            moveSpeed = baselineSpeed,
                            directRatio = baselineDirect
                        };
                    });
                    break;
                case 3: // Move Speed group
                    AddLatinOrderedTrials(speedValues.Length, latinShift, (i, idx) => new TrialParameters
                    {
                        groupID = 3,
                        trialIndexInGroup = idx,
                        npcCount = baselineCount,
                        uniformRatio = baselineUniform,
                        destinationLeftRatio = baselineLeft,
                        destinationRightRatio = baselineRight,
                        moveSpeed = speedValues[i],
                        directRatio = baselineDirect
                    });
                    break;
                case 4: // Direct Ratio group
                    AddLatinOrderedTrials(directValues.Length, latinShift, (i, idx) => new TrialParameters
                    {
                        groupID = 4,
                        trialIndexInGroup = idx,
                        npcCount = baselineCount,
                        uniformRatio = baselineUniform,
                        destinationLeftRatio = baselineLeft,
                        destinationRightRatio = baselineRight,
                        moveSpeed = baselineSpeed,
                        directRatio = directValues[i]
                    });
                    break;
                default:
                    Debug.LogWarning($"[ExperimentConfig] Unknown groupID {groupID}");
                    break;
            }
        }

        /// <summary>
        /// Helper to add trials for one group in Latin-order: for N levels, use cyclic Latin row offset,
        /// where latinShift mod N determines the starting index.
        /// The factory function receives the actual index in values array (i) and the position index (trialIndexInGroup).
        /// </summary>
        private void AddLatinOrderedTrials(int numLevels, int latinShift, Func<int, int, TrialParameters> factory)
        {
            // Ensure positive shift
            int shift = ((latinShift % numLevels) + numLevels) % numLevels;
            for (int pos = 0; pos < numLevels; pos++)
            {
                int i = (shift + pos) % numLevels;
                TrialParameters tp = factory(i, pos);
                allTrials.Add(tp);
            }
        }
    }

    #endregion
}