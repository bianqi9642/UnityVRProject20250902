using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// ExperimentManager orchestrates the VR experiment with UI-only flow:
/// - At each group start: prompt via ExperimentUIController.ShowGroupStart(groupID, trialCount).
/// - For each trial:
///     * Prompt “Get ready for Trial {trialIndex}/{totalTrials}. Press button to start.” via ShowTrialStart.
///     * Configure and spawn NPCs, reset participant position.
///     * Wait until intersection event is signaled (NotifyIntersectionReached).
///     * Once intersection is reached, show route choice UI (ShowRouteChoice) and record the chosen route.
///     * Log trial data, cleanup NPCs.
/// - At group end: prompt “Group {groupID} complete. Thank you!” via ShowGroupFinish.
/// - Optionally rest after each group via ShowRest(restDuration).
///
/// Intersection event must be signaled by calling NotifyIntersectionReached() when appropriate 
/// (e.g., from an OnTriggerEnter on the intersection collider).
///
/// Attach this script to a GameObject in your scene (e.g., "ExperimentManager").
/// Assign references in Inspector: NPCSpawner, participant Transform, ExperimentUIController, etc.
/// Ensure participantID ends with a number (e.g., Sub01) so that Latin-square shift can be derived automatically.
/// </summary>
public class ExperimentManagerBackup : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Reference to the NPCSpawner in the scene.")]
    public NPCSpawner npcSpawner;
    [Tooltip("Participant XR Rig or main camera transform.")]
    public Transform participant;

    [Header("UI Controller")]
    [Tooltip("Reference to the ExperimentUIController for showing prompts, waits, rest, route choice, etc.")]
    public ExperimentUIController uiController;

    [Header("Trial Settings")]
    [Tooltip("Maximum duration for each trial in seconds.")]
    public float trialDuration = 60f;
    [Tooltip("Participant ID, should end with a number for Latin square shift, e.g., Sub01.")]
    public string participantID = "Sub01";

    [Header("Experiment Group Selection")]
    [Tooltip("Select which variable group to run; All = run every group mixed.")]
    public ExperimentGroup selectedGroup = ExperimentGroup.All;

    [Header("Rest Durations per Group (seconds)")]
    [Tooltip("Specify rest duration after each group ID (index = groupID). Used after each group completes.")]
    public float[] restDurations = new float[] { 30f, 30f, 30f, 30f, 30f };

    /// <summary>
    /// Enum for selecting which group to run.
    /// All = -1, NPCCount=0, UniformRatio=1, DestinationRatio=2, MoveSpeed=3, DirectRatio=4.
    /// </summary>
    public enum ExperimentGroup
    {
        All = -1,
        NPCCount = 0,
        UniformRatio = 1,
        DestinationRatio = 2,
        MoveSpeed = 3,
        DirectRatio = 4
    }

    // Internal experiment configuration
    private ExperimentConfig config;

    // Logging
    private StreamWriter logWriter;
    private string logFilePath;

    // Intersection event flag & chosen route
    private bool intersectionReached = false;
    private string chosenRoute = "";

    void Start()
    {
        // Basic reference checks
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
        if (uiController == null)
        {
            Debug.LogError("[ExperimentManager] UI Controller reference is missing. Please assign ExperimentUIController in Inspector.");
            return;
        }

        // Determine Latin square shift index from trailing digits in participantID
        int latinShift = ParseParticipantNumber(participantID) - 1;
        if (latinShift < 0) latinShift = 0;

        // Initialize experiment configuration based on selectedGroup and latinShift
        config = new ExperimentConfig(selectedGroup, latinShift);

        // Validate restDurations length if needed
        if (selectedGroup == ExperimentGroup.All && (restDurations == null || restDurations.Length < 5))
        {
            Debug.LogWarning("[ExperimentManager] restDurations array length is less than 5. Missing entries will be treated as 0.");
        }

        // Initialize log file
        string fileName = $"ExperimentLog_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        logFilePath = Path.Combine(Application.persistentDataPath, fileName);
        try
        {
            logWriter = new StreamWriter(logFilePath, false);
            // Write CSV header
            logWriter.WriteLine("Timestamp,ParticipantID,TrialGlobalIndex,GroupID,TrialIndexInGroup,NPCCount,UniformRatio,DestinationLeftRatio,DestinationRightRatio,MoveSpeed,DirectRatio,ChosenRoute,TimeTaken,PathData");
            logWriter.Flush();
            Debug.Log($"[ExperimentManager] Logging to: {logFilePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ExperimentManager] Failed to open log file: {e}");
        }

        Debug.Log($"[ExperimentManager] SelectedGroup = {selectedGroup}, latinShift = {latinShift}. Total trials: {config.allTrials.Count}");

        // Reset any previous route selection tracking
        if (RouteSelectionTracker.Instance != null)
        {
            RouteSelectionTracker.Instance.Reset();
        }

        // Start the main experiment coroutine
        StartCoroutine(RunExperiment());
    }

    /// <summary>
    /// Should be called externally (e.g., from an intersection trigger) when intersection is reached.
    /// Sets a flag so RunTrial can proceed to route-choice UI.
    /// </summary>
    public void NotifyIntersectionReached()
    {
        intersectionReached = true;
    }

    /// <summary>
    /// Parse trailing digits from participantID. Returns parsed integer or 1 if none found.
    /// </summary>
    private int ParseParticipantNumber(string pid)
    {
        if (string.IsNullOrEmpty(pid)) return 1;
        var m = Regex.Match(pid, "(\\d+)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int num))
        {
            return num;
        }
        return 1;
    }

    /// <summary>
    /// Main experiment loop:
    /// - For each group block in config.groupOrder:
    ///     - ShowGroupStart(groupID, trialsInThisGroup)
    ///     - For each trialIndex from 1 to trialsInThisGroup:
    ///         * ShowTrialStart(trialIndex, trialsInThisGroup)
    ///         * RunTrial with actual parameters and global index
    ///     - ShowGroupFinish(groupID)
    ///     - ShowRest(restDurations[groupID]) if > 0
    /// </summary>
    private IEnumerator RunExperiment()
    {
        int globalTrialIdx = 0;

        for (int blockIdx = 0; blockIdx < config.groupOrder.Count; blockIdx++)
        {
            int groupID = config.groupOrder[blockIdx];
            int trialsInThisGroup = config.groupTrialCounts[blockIdx];

            // 1. Group start prompt, using actual groupID and trial count
            yield return StartCoroutine(uiController.ShowGroupStart(groupID, trialsInThisGroup));

            // 2. Trials in this group
            for (int i = 0; i < trialsInThisGroup; i++)
            {
                int trialIndex = i + 1; // 1-based for display

                // Trial start prompt, using actual trialIndex and total trialsInThisGroup
                yield return StartCoroutine(uiController.ShowTrialStart(trialIndex, trialsInThisGroup));

                // Run the trial
                TrialParameters tp = config.allTrials[globalTrialIdx];
                yield return StartCoroutine(RunTrial(tp, globalTrialIdx));
                globalTrialIdx++;

                // Reset path tracker for next trial if used
                if (RouteSelectionTracker.Instance != null)
                    RouteSelectionTracker.Instance.Reset();
            }

            // 3. Group finish prompt, using actual groupID
            yield return StartCoroutine(uiController.ShowGroupFinish(groupID));

            // 4. Rest after group if configured
            float restDuration = 0f;
            if (restDurations != null && groupID >= 0 && groupID < restDurations.Length)
                restDuration = restDurations[groupID];
            if (restDuration > 0f)
            {
                yield return StartCoroutine(uiController.ShowRest(restDuration));
            }
        }

        // Optional final prompt after all groups complete, if desired:
        // yield return StartCoroutine(uiController.ShowGroupFinish(-1)); // or implement a ShowExperimentComplete method

        // Close log
        logWriter?.Close();
    }

    /// <summary>
    /// Runs a single trial with given TrialParameters and global index.
    /// - Configure NPCSpawner, spawn NPCs.
    /// - Reset participant position.
    /// - Wait for intersection via NotifyIntersectionReached.
    /// - Show route choice UI, obtain chosenRoute.
    /// - Log using actual chosenRoute, timeTaken, and parameters.
    /// - Cleanup NPCs.
    /// </summary>
    private IEnumerator RunTrial(TrialParameters tp, int globalIdx)
    {
        // 1. Configure NPCSpawner fields from tp
        npcSpawner.npcCount = tp.npcCount;
        npcSpawner.uniformRatio = tp.uniformRatio;
        npcSpawner.leftRatio = tp.destinationLeftRatio;
        npcSpawner.rightRatio = tp.destinationRightRatio;
        npcSpawner.moveSpeed = tp.moveSpeed;
        npcSpawner.directRatio = tp.directRatio;

        // 2. Spawn NPCs
        npcSpawner.SpawnNPCs();

        // 3. Reset participant position
        ResetParticipantPosition();

        // 4. Wait for intersection event
        intersectionReached = false;
        float startWaitTime = Time.time;
        while (!intersectionReached)
        {
            // Optionally add a timeout here if needed
            yield return null;
        }

        // 5. Show route choice UI and record actual chosenRoute
        chosenRoute = "";
        yield return StartCoroutine(uiController.ShowRouteChoice(choice => chosenRoute = choice));
        // chosenRoute now holds "Left", "Straight", or "Right"

        // 6. Compute results and log with actual chosenRoute
        float timeTaken = Time.time - startWaitTime;
        string pathData = RouteSelectionTracker.Instance != null
            ? RouteSelectionTracker.Instance.GetPathDataString()
            : "";

        string timestamp = DateTime.Now.ToString("o");
        string line = $"{timestamp},{participantID},{globalIdx},{tp.groupID},{tp.trialIndexInGroup}," +
                      $"{tp.npcCount},{tp.uniformRatio},{tp.destinationLeftRatio},{tp.destinationRightRatio}," +
                      $"{tp.moveSpeed},{tp.directRatio},{chosenRoute},{timeTaken:F2},\"{pathData}\"";
        try
        {
            logWriter.WriteLine(line);
            logWriter.Flush();
        }
        catch (Exception e)
        {
            Debug.LogError($"[ExperimentManager] Failed to write log: {e}");
        }

        // 7. Cleanup NPCs
        npcSpawner.CleanupNPCs();
    }

    /// <summary>
    /// Reset participant position & orientation to designated start location.
    /// Currently resets to world origin; if you have a specific start Transform, modify accordingly.
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
        public int groupID;               // 0~4: which variable group this trial belongs to
        public int trialIndexInGroup;     // index within its group (0-based or as constructed)
        public int npcCount;
        public float uniformRatio;
        public float destinationLeftRatio;
        public float destinationRightRatio;
        public float moveSpeed;
        public float directRatio;
    }

    /// <summary>
    /// Builds trial lists for selected group or all groups.
    /// Also records group order and trial counts per group for block-level prompts/rest.
    /// </summary>
    private class ExperimentConfig
    {
        public List<TrialParameters> allTrials;
        public List<int> groupOrder;         // sequence of groupIDs in blocks
        public List<int> groupTrialCounts;   // trial counts parallel to groupOrder

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
        /// Constructor: builds trials only for the specified group (or all if selGroup == All), using latinShift.
        /// Also populates groupOrder and groupTrialCounts.
        /// </summary>
        /// <param name="selGroup">Enum indicating which group to generate (-1 for All).</param>
        /// <param name="latinShift">Shift index for Latin square ordering.</param>
        public ExperimentConfig(ExperimentGroup selGroup, int latinShift)
        {
            allTrials = new List<TrialParameters>();
            groupOrder = new List<int>();
            groupTrialCounts = new List<int>();

            int selected = (int)selGroup; // -1 for All, or 0-4 for single group

            if (selected == -1)
            {
                // All groups: order group blocks by Latin square cyclic shift
                int numGroups = 5;
                for (int g = 0; g < numGroups; g++)
                {
                    int groupID = (latinShift + g) % numGroups; // cyclic group order
                    int countThisBlock = AddGroupTrials(groupID, latinShift);
                    groupOrder.Add(groupID);
                    groupTrialCounts.Add(countThisBlock);
                }
            }
            else
            {
                // Single selected group: just add that group's trials in Latin order
                int countThisBlock = AddGroupTrials(selected, latinShift);
                groupOrder.Add(selected);
                groupTrialCounts.Add(countThisBlock);
            }
        }

        /// <summary>
        /// Adds all trials for a given groupID, ordering by Latin square shift.
        /// Returns the number of trials added (i.e., number of levels for that group).
        /// </summary>
        private int AddGroupTrials(int groupID, int latinShift)
        {
            int numLevels = 0;
            switch (groupID)
            {
                case 0: // NPC Count group
                    numLevels = countValues.Length;
                    AddLatinOrderedTrials(numLevels, latinShift, (i, idx) => new TrialParameters
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
                    numLevels = uniformValues.Length;
                    AddLatinOrderedTrials(numLevels, latinShift, (i, idx) => new TrialParameters
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
                    numLevels = destinationLeftValues.Length;
                    AddLatinOrderedTrials(numLevels, latinShift, (i, idx) =>
                    {
                        float left = destinationLeftValues[i];
                        float right = destinationRightValues[i];
                        // Normalize if sum > 1
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
                    numLevels = speedValues.Length;
                    AddLatinOrderedTrials(numLevels, latinShift, (i, idx) => new TrialParameters
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
                    numLevels = directValues.Length;
                    AddLatinOrderedTrials(numLevels, latinShift, (i, idx) => new TrialParameters
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
            return numLevels;
        }

        /// <summary>
        /// Helper to add trials for one group in Latin-order: for numLevels, use cyclic row offset,
        /// where latinShift mod numLevels determines the starting index.
        /// The factory function takes the index in value array (i) and the trialIndexInGroup (pos).
        /// </summary>
        private void AddLatinOrderedTrials(int numLevels, int latinShift, Func<int, int, TrialParameters> factory)
        {
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
