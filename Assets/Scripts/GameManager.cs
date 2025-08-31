using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// GameManager: runs experiment trials sequentially
/// - Configures environment & NPCs
/// - Monitors NPCs until arrival or timeout
/// - Records trial settings and participant choices via DataRecorder
///
/// Changes relevant to NPC choice removal:
/// - No per-NPC choice recording (per-NPC Record calls removed)
/// - GameManager no longer relies on EnvironmentManager.GetLandmarkX()
/// - When NPCs finish, they are cleaned up (destroyed) but not individually recorded
/// - UI integration remains (ExperimentUIManager) for Intro / Choice / Rest / End
/// </summary>
public class GameManager : MonoBehaviour
{
    [Header("References (assign in Inspector)")]
    public TrialScheduler trialScheduler;
    public EnvironmentManager environmentManager;
    public NPCSpawner npcSpawner;
    public DataRecorder dataRecorder;

    [Header("UI")]
    [Tooltip("Optional: UI manager that shows Intro/Choice/Rest/End panels. If assigned, UI triggers experiment Start.")]
    public ExperimentUIManager uiManager;

    [Header("Experiment")]
    public int participantID = 0;
    [Tooltip("Participant group (1..4) required by TrialScheduler")]
    [Range(1, 4)]
    public int participantGroup = 1;

    [Header("Trial timing")]
    public float trialTimeoutSeconds = 40f;
    public float interTrialInterval = 1.0f;
    public float minHoldBeforeRecord = 0.5f;

    [Header("Participant response")]
    [Tooltip("How long (s) to wait for participant to make a choice after NPCs finished.")]
    public float participantChoiceTimeoutSeconds = 30f;

    private List<TrialScheduler.TrialSettings> trials;
    private int currentTrialIndex = -1;
    private Dictionary<Transform, float> stopDetectedTime = new Dictionary<Transform, float>();

    // Participant response handling
    private bool awaitingParticipantChoice = false;
    private float participantChoicePromptTime = 0f;
    private bool participantChoiceReceived = false;
    private string participantChoicePath = "NoResponse";
    private float participantResponseTime = -1f;

    // Hold current trial settings for SubmitParticipantChoice use
    private TrialScheduler.TrialSettings currentTrialSettings = null;

    private void Start()
    {
        // If a UI Manager is assigned, show the intro UI and let it call StartExperiment when participant presses Start.
        if (uiManager != null)
        {
            uiManager.gameManager = this; // ensure bi-directional link (safe even if already set in Inspector)
            uiManager.ShowIntroPanel();
            // Do NOT auto-start the experiment here; UI will call StartExperiment when the participant presses Start.
        }
        else
        {
            // Fallback: no UI assigned -> start immediately (legacy behavior)
            StartExperiment();
        }
    }

    /// <summary>
    /// Public method to start the experiment run. UI can call this when Start is pressed.
    /// </summary>
    public void StartExperiment()
    {
        if (trialScheduler == null || environmentManager == null || npcSpawner == null)
        {
            Debug.LogError("[GameManager] Missing references.");
            return;
        }

        trials = trialScheduler.GenerateTrialsForParticipant(participantID, participantGroup);
        if (trials == null || trials.Count == 0)
        {
            Debug.LogWarning("[GameManager] No trials generated.");
            return;
        }

        currentTrialIndex = 0;
        StartCoroutine(RunTrials());
    }

    private IEnumerator RunTrials()
    {
        while (currentTrialIndex < trials.Count)
        {
            var ts = trials[currentTrialIndex];
            currentTrialSettings = ts;
            Debug.Log($"[GameManager] Starting trial {ts.trialID}");

            // 1) Configure environment
            environmentManager.ConfigureTrial(ts);
            yield return null;

            // Record trial settings
            if (dataRecorder != null)
                dataRecorder.RecordTrialSettings(ts);

            // 2) Configure NPCSpawner (pass per-path params)
            ConfigureSpawnerFromTrial(ts);
            stopDetectedTime.Clear();

            // 3) Spawn NPCs
            if (npcSpawner.npcCount > 0 || (npcSpawner.totalCount > 0))
            {
                npcSpawner.SpawnNPCs();

                // Set trial metadata on each NPCMoverOffset (but DO NOT assign DataRecorder or record per-NPC)
                for (int i = npcSpawner.transform.childCount - 1; i >= 0; i--)
                {
                    Transform child = npcSpawner.transform.GetChild(i);
                    if (child == null) continue;
                    var mover = child.GetComponent<NPCMoverOffset>();
                    if (mover == null) continue;

                    mover.participantID = participantID;
                    mover.trialID = ts.trialID;
                    mover.landmarkIsLeft = ts.landmarkLeft;
                    // NOTE: do not set mover.dataRecorder or call any per-NPC recording methods
                }

                // Wait briefly for agents to report placement
                float placeTimeout = 0.5f; // half-second max wait for placement (tunable)
                float waitElapsed = 0f;
                bool allPlaced = false;

                while (waitElapsed < placeTimeout)
                {
                    allPlaced = true;
                    for (int i = npcSpawner.transform.childCount - 1; i >= 0; i--)
                    {
                        Transform child = npcSpawner.transform.GetChild(i);
                        if (child == null) continue;
                        var mover = child.GetComponent<NPCMoverOffset>();
                        if (mover != null)
                        {
                            if (!mover.isPlacedOnNavMesh)
                            {
                                allPlaced = false;
                                break;
                            }
                        }
                    }
                    if (allPlaced) break;
                    waitElapsed += Time.deltaTime;
                    yield return null;
                }

                if (!allPlaced)
                {
                    Debug.LogWarning($"[GameManager] Not all NPCs reported isPlacedOnNavMesh within {placeTimeout}s. Proceeding but GameManager will guard agent accesses.");
                }
            }

            // 4) Wait until NPCs finish or timeout (no per-NPC recording)
            float elapsed = 0f;
            while (elapsed < trialTimeoutSeconds)
            {
                // Iterate children safely (reverse to allow destroy during iteration)
                for (int i = npcSpawner.transform.childCount - 1; i >= 0; i--)
                {
                    Transform child = npcSpawner.transform.GetChild(i);
                    if (child == null) continue;

                    // Try to get agent; if absent, destroy immediately
                    var agent = child.GetComponent<NavMeshAgent>();
                    if (agent == null)
                    {
                        Destroy(child.gameObject);
                        continue;
                    }

                    bool candidateFinished = false;

                    // Guard NavMeshAgent API calls
                    try
                    {
                        if (agent.isOnNavMesh)
                        {
                            bool pathPending = agent.pathPending;
                            bool hasPath = agent.hasPath;

                            float threshold = agent.stoppingDistance + 0.15f;
                            bool closeEnough = false;
                            if (!pathPending)
                            {
                                if (!hasPath)
                                {
                                    closeEnough = true;
                                }
                                else
                                {
                                    float remDist = agent.remainingDistance;
                                    closeEnough = (remDist <= threshold);
                                }
                            }

                            bool stopped = agent.isStopped;
                            candidateFinished = stopped && closeEnough;
                        }
                        else
                        {
                            candidateFinished = false;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[GameManager] Exception while checking NavMeshAgent on '{child.name}': {ex.Message}. Skipping this check for now.");
                        candidateFinished = false;
                    }

                    // When candidate finished for a hold period, destroy it (no recording)
                    if (candidateFinished)
                    {
                        if (!stopDetectedTime.ContainsKey(child))
                        {
                            stopDetectedTime[child] = Time.time;
                        }
                        else
                        {
                            if (Time.time - stopDetectedTime[child] >= minHoldBeforeRecord)
                            {
                                Destroy(child.gameObject);
                                stopDetectedTime.Remove(child);
                            }
                        }
                    }
                    else
                    {
                        if (stopDetectedTime.ContainsKey(child))
                            stopDetectedTime.Remove(child);
                    }
                }

                // If all children gone, break early
                if (npcSpawner.transform.childCount == 0)
                    break;

                elapsed += Time.deltaTime;
                yield return null;
            }

            // 5) Timeout cleanup: destroy remaining children
            if (npcSpawner.transform.childCount > 0)
            {
                Debug.LogWarning($"[GameManager] Trial {ts.trialID} timed out after {trialTimeoutSeconds} seconds; forcing cleanup.");
                for (int i = npcSpawner.transform.childCount - 1; i >= 0; i--)
                {
                    Transform child = npcSpawner.transform.GetChild(i);
                    if (child == null) continue;
                    Destroy(child.gameObject);
                }
            }

            // 6) Participant choice
            npcSpawner.CleanupNPCs();
            stopDetectedTime.Clear();

            awaitingParticipantChoice = true;
            participantChoiceReceived = false;
            participantChoicePath = "NoResponse";
            participantResponseTime = -1f;
            participantChoicePromptTime = Time.time;

            // Show choice UI if available
            if (uiManager != null)
            {
                uiManager.ShowChoicePanel();
            }

            Debug.Log($"[GameManager] Trial {ts.trialID} awaiting participant choice.");

            float waitElapsed2 = 0f;
            while (awaitingParticipantChoice && waitElapsed2 < participantChoiceTimeoutSeconds)
            {
                // Keep keyboard fallback so experiments without UI still work
                if (!participantChoiceReceived)
                {
                    if (Input.GetKeyDown(KeyCode.LeftArrow))
                        SubmitParticipantChoice("Left");
                    else if (Input.GetKeyDown(KeyCode.RightArrow))
                        SubmitParticipantChoice("Right");
                }

                waitElapsed2 += Time.deltaTime;
                yield return null;
            }

            // If participant never responded within timeout, record NoResponse
            if (!participantChoiceReceived)
            {
                int fallbackChoseTarget = -1; // No response
                float respTime = -1f;
                if (dataRecorder != null)
                    dataRecorder.RecordParticipantChoice(participantID, ts.trialID, "NoResponse", fallbackChoseTarget, respTime);

                // Ensure choice UI hidden
                if (uiManager != null)
                    uiManager.HideChoicePanel();
            }

            yield return new WaitForSeconds(interTrialInterval);

            // Block-boundary check: if next trial exists and its blockIndex differs -> show Rest panel and wait for Start
            int nextIndex = currentTrialIndex + 1;
            if (nextIndex < trials.Count)
            {
                var nextTs = trials[nextIndex];
                if (nextTs.blockIndex != ts.blockIndex)
                {
                    // block boundary reached: show Rest panel and wait for Start click
                    if (uiManager != null)
                    {
                        uiManager.ShowRestPanel();
                        // wait until participant clicks Start on rest panel
                        yield return StartCoroutine(uiManager.WaitForStartButton(showIntro: false));
                        uiManager.HideRestPanel();
                    }
                }
            }

            currentTrialIndex++;
        }

        EndExperiment();
    }

    /// <summary>
    /// Configure spawner using explicit per-path parameters from trial settings.
    /// This avoids averaging moveSpeed/uniformRatio across paths.
    /// We pass target/distractor PathParameters and integer counts to the spawner.
    /// </summary>
    private void ConfigureSpawnerFromTrial(TrialScheduler.TrialSettings ts)
    {
        var target = ts.targetPath;
        var distract = ts.distractorPath;

        int targetCount = Mathf.Max(0, target.npcNumber);
        int distractCount = Mathf.Max(0, distract.npcNumber);
        int totalCount = targetCount + distractCount;

        if (totalCount <= 0)
        {
            npcSpawner.npcCount = 0;
            return;
        }

        npcSpawner.totalCount = totalCount;
        npcSpawner.targetCount = targetCount;
        npcSpawner.distractorCount = distractCount;

        // Mark which side is the "target" for destination assignment
        npcSpawner.targetIsLeft = ts.landmarkLeft;

        // Build PathParameters and pass them through
        var tp = new PathConfigurator.PathParameters
        {
            npcNumber = target.npcNumber,
            credibleProportion = target.credibleProportion,
            directionConsistency = target.directionConsistency,
            speed = target.speed,
            movementStyle = target.movementStyle
        };

        var dp = new PathConfigurator.PathParameters
        {
            npcNumber = distract.npcNumber,
            credibleProportion = distract.credibleProportion,
            directionConsistency = distract.directionConsistency,
            speed = distract.speed,
            movementStyle = distract.movementStyle
        };

        npcSpawner.targetParams = tp;
        npcSpawner.distractorParams = dp;

        npcSpawner.npcCount = totalCount;

        Debug.Log($"[GameManager] Configured spawner for trial {ts.trialID}: total={totalCount}, targetCount={targetCount}, distractCount={distractCount}");
    }

    /// <summary>
    /// Submit participant choice.
    /// This method sanitizes input and computes choseTarget using currentTrialSettings when available,
    /// then records via DataRecorder.
    /// </summary>
    public void SubmitParticipantChoice(string pathName)
    {
        if (!awaitingParticipantChoice) return;

        // sanitize input: remove quotes and whitespace, normalize case
        string raw = pathName ?? "";
        raw = raw.Trim().Trim(new char[] { '"', '\'', '“', '”', '‘', '’' });
        string normalized = raw.ToLowerInvariant();

        participantChoiceReceived = true;
        awaitingParticipantChoice = false;
        participantChoicePath = string.IsNullOrEmpty(normalized) ? "NoResponse" : normalized;
        participantResponseTime = Time.time - participantChoicePromptTime;

        int choseTargetInt = -1;

        if (string.IsNullOrEmpty(normalized) || normalized == "noresponse")
        {
            choseTargetInt = -1;
        }
        else
        {
            bool choseLeft = normalized == "left";
            bool choseRight = normalized == "right";

            if (!choseLeft && !choseRight)
            {
                Debug.LogWarning($"[GameManager] SubmitParticipantChoice: unexpected normalized choice '{normalized}'. Marking as NoResponse.");
                choseTargetInt = -1;
            }
            else
            {
                if (currentTrialSettings != null)
                {
                    bool landmarkLeft = currentTrialSettings.landmarkLeft;
                    bool choseTarget = (choseLeft && landmarkLeft) || (choseRight && !landmarkLeft);
                    choseTargetInt = choseTarget ? 1 : 0;
                }
                else
                {
                    Debug.LogWarning("[GameManager] SubmitParticipantChoice: currentTrialSettings is null; falling back to -1 for choseTarget.");
                    choseTargetInt = -1;
                }
            }
        }

        if (dataRecorder != null)
        {
            dataRecorder.RecordParticipantChoice(participantID,
                currentTrialSettings != null ? currentTrialSettings.trialID : -1,
                (string.IsNullOrEmpty(raw) ? "NoResponse" : raw), choseTargetInt, participantResponseTime);
        }
        else
        {
            Debug.LogWarning("[GameManager] SubmitParticipantChoice: dataRecorder == null; choice not recorded.");
        }

        if (uiManager != null)
        {
            uiManager.HideChoicePanel();
        }

        Debug.Log($"[GameManager] Participant choice submitted: {participantChoicePath}, rt={participantResponseTime:F2}s, choseTarget={choseTargetInt}");
    }

    private void EndExperiment()
    {
        Debug.Log("[GameManager] Experiment finished.");
        // Show End panel if UI manager is present
        if (uiManager != null)
        {
            uiManager.ShowEndPanel();
        }
    }
}