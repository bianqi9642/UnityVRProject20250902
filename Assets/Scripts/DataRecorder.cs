using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System;
using System.Linq;

/// <summary>
/// DataRecorder: collects and saves per-trial and participant-choice data.
/// 
/// Changes:
/// - Removed per-NPC recording (no NPCRecord, no RecordNPCChoice).
/// - Each trial is still written to CSV immediately after participant choice.
/// - npc_total in CSV is computed as target_N + distract_N from the recorded trial settings.
/// 
/// Notes:
/// - TrialRecord and ParticipantChoiceRecord are structs (value types). Use Equals(default(...)) to
///   test whether LastOrDefault returned a meaningful record or the default empty struct.
/// </summary>
public class DataRecorder : MonoBehaviour
{
    private struct TrialRecord
    {
        public int participantID;
        public int trialID;
        public bool landmarkLeft;

        // target path params
        public int target_N;
        public float target_C;
        public float target_D;
        public float target_S;
        public string target_M;

        // distractor path params
        public int distract_N;
        public float distract_C;
        public float distract_D;
        public float distract_S;
        public string distract_M;

        // metadata
        public string manipulatedVariablesDescription;
        public int blockIndex;

        public float timestamp;
    }

    private struct ParticipantChoiceRecord
    {
        public int participantID;
        public int trialID;
        public string chosenPath;
        public int choseTarget;
        public float responseTime;
        public float timestamp;
    }

    // -------------------- Storage --------------------
    // Removed npcRecords/list for per-NPC recording
    private List<TrialRecord> trialRecords = new List<TrialRecord>();
    private List<ParticipantChoiceRecord> participantChoices = new List<ParticipantChoiceRecord>();

    private string csvPath;
    private bool csvHeaderWritten = false;

    void Awake()
    {
        // Prepare output file once per experiment
        string timeStamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        csvPath = Path.Combine(Application.persistentDataPath, $"participant_results_{timeStamp}.csv");
    }

    // -------------------- Public Recording Methods --------------------

    /// <summary>
    /// Record trial settings for later export.
    /// </summary>
    public void RecordTrialSettings(TrialScheduler.TrialSettings ts)
    {
        if (ts == null) return;

        TrialRecord t = new TrialRecord
        {
            participantID = ts.participantID,
            trialID = ts.trialID,
            landmarkLeft = ts.landmarkLeft,
            target_N = ts.targetPath != null ? ts.targetPath.npcNumber : 0,
            target_C = ts.targetPath != null ? ts.targetPath.credibleProportion : 0f,
            target_D = ts.targetPath != null ? ts.targetPath.directionConsistency : 0f,
            target_S = ts.targetPath != null ? ts.targetPath.speed : 0f,
            target_M = ts.targetPath != null ? ts.targetPath.movementStyle.ToString() : "Unknown",
            distract_N = ts.distractorPath != null ? ts.distractorPath.npcNumber : 0,
            distract_C = ts.distractorPath != null ? ts.distractorPath.credibleProportion : 0f,
            distract_D = ts.distractorPath != null ? ts.distractorPath.directionConsistency : 0f,
            distract_S = ts.distractorPath != null ? ts.distractorPath.speed : 0f,
            distract_M = ts.distractorPath != null ? ts.distractorPath.movementStyle.ToString() : "Unknown",
            manipulatedVariablesDescription = ts.manipulatedVariablesDescription ?? "",
            blockIndex = ts.blockIndex,
            timestamp = Time.time
        };

        trialRecords.Add(t);
        Debug.Log($"[DataRecorder] Trial settings recorded: pid={t.participantID} trial={t.trialID} landmarkLeft={t.landmarkLeft}");
    }

    /// <summary>
    /// Record participant choice and export corresponding trial row to CSV.
    /// computedChoseTarget: 1 = chose target, 0 = chose non-target, -1 = NoResponse/unknown
    /// </summary>
    public void RecordParticipantChoice(int participantID, int trialID, string chosenPath, int providedChoseTarget, float responseTime)
    {
        Debug.Log($"[DataRecorder] RecordParticipantChoice called: pid={participantID} trial={trialID} chosenPath={chosenPath} providedChoseTarget={providedChoseTarget} trialRecords.count={trialRecords.Count}");

        string normalized = string.IsNullOrEmpty(chosenPath) ? "NoResponse" : chosenPath.Trim();
        int computedChoseTarget = -1;

        if (string.IsNullOrEmpty(normalized) || normalized.Equals("NoResponse", StringComparison.OrdinalIgnoreCase))
        {
            computedChoseTarget = -1;
        }
        else
        {
            var tr = trialRecords.LastOrDefault(trial => trial.participantID == participantID && trial.trialID == trialID);

            if (tr.Equals(default(TrialRecord)))
            {
                Debug.LogWarning($"[DataRecorder] Trial settings for pid={participantID}, trial={trialID} not found when recording participant choice. Falling back to provided choseTarget value.");
                computedChoseTarget = providedChoseTarget;
            }
            else
            {
                bool landmarkLeft = tr.landmarkLeft;
                string low = normalized.ToLowerInvariant();
                bool choseLeft = low == "left";
                bool choseRight = low == "right";

                if (!choseLeft && !choseRight)
                {
                    Debug.LogWarning($"[DataRecorder] Unexpected chosenPath='{chosenPath}' for pid={participantID}, trial={trialID}. Marking as NoResponse.");
                    computedChoseTarget = -1;
                }
                else
                {
                    if ((choseLeft && landmarkLeft) || (choseRight && !landmarkLeft))
                        computedChoseTarget = 1;
                    else
                        computedChoseTarget = 0;
                }
            }
        }

        ParticipantChoiceRecord p = new ParticipantChoiceRecord
        {
            participantID = participantID,
            trialID = trialID,
            chosenPath = string.IsNullOrEmpty(chosenPath) ? "NoResponse" : chosenPath,
            choseTarget = computedChoseTarget,
            responseTime = responseTime,
            timestamp = Time.time
        };
        participantChoices.Add(p);
        Debug.Log($"[DataRecorder] ParticipantChoice Rec: pid={participantID} trial={trialID} choice={p.chosenPath} choseTarget={p.choseTarget}");

        ExportTrialToCSV(participantID, trialID);
    }

    // -------------------- Per-trial CSV Export --------------------
    private void ExportTrialToCSV(int participantID, int trialID)
    {
        using (StreamWriter sw = new StreamWriter(csvPath, append: true))
        {
            if (!csvHeaderWritten)
            {
                sw.WriteLine("participantID,trialID,blockIndex,manipulatedVariablesDescription,landmarkLeft,"
                    + "target_N,target_C,target_D,target_S,target_M,"
                    + "distract_N,distract_C,distract_D,distract_S,distract_M,"
                    + "participantChoicePath,participantChoseTarget,participantResponseTime,"
                    + "npc_total");
                csvHeaderWritten = true;
            }

            // get most recent matching records (or default structs if not found)
            var t = trialRecords.LastOrDefault(tr => tr.participantID == participantID && tr.trialID == trialID);
            var p = participantChoices.LastOrDefault(pc => pc.participantID == participantID && pc.trialID == trialID);

            bool hasTrial = !t.Equals(default(TrialRecord));
            bool hasParticipantChoice = !p.Equals(default(ParticipantChoiceRecord));

            // safe extraction with fallbacks
            int out_participantID = hasTrial ? t.participantID : participantID;
            int out_trialID = hasTrial ? t.trialID : trialID;
            int out_blockIndex = hasTrial ? t.blockIndex : -1;
            string out_manipDesc = hasTrial ? (t.manipulatedVariablesDescription ?? "") : "";
            int landmarkLeftInt = (hasTrial && t.landmarkLeft) ? 1 : 0;

            int target_N = hasTrial ? t.target_N : 0;
            float target_C = hasTrial ? t.target_C : 0f;
            float target_D = hasTrial ? t.target_D : 0f;
            float target_S = hasTrial ? t.target_S : 0f;
            string target_M = hasTrial ? (t.target_M ?? "Unknown") : "Unknown";

            int distract_N = hasTrial ? t.distract_N : 0;
            float distract_C = hasTrial ? t.distract_C : 0f;
            float distract_D = hasTrial ? t.distract_D : 0f;
            float distract_S = hasTrial ? t.distract_S : 0f;
            string distract_M = hasTrial ? (t.distract_M ?? "Unknown") : "Unknown";

            string choicePath = hasParticipantChoice ? (p.chosenPath ?? "NoResponse") : "NoResponse";
            int choiceChoseTarget = hasParticipantChoice ? p.choseTarget : -1;
            float choiceRespTime = hasParticipantChoice ? p.responseTime : -1f;

            // npc_total is computed from trial params (target_N + distract_N)
            int npcTotal = hasTrial ? (t.target_N + t.distract_N) : 0;

            // sanitize manipulatedVariablesDescription to avoid CSV column breakage
            string safeManipDesc = out_manipDesc.Replace("\r", " ").Replace("\n", " ").Replace(",", "_");

            // sanitize choicePath (replace commas/newlines)
            string safeChoicePath = choicePath.Replace("\r", " ").Replace("\n", " ").Replace(",", "_");

            // write CSV line
            sw.WriteLine($"{out_participantID},{out_trialID},{out_blockIndex},{safeManipDesc},{landmarkLeftInt},"
                + $"{target_N},{target_C},{target_D},{target_S},{target_M},"
                + $"{distract_N},{distract_C},{distract_D},{distract_S},{distract_M},"
                + $"{safeChoicePath},{choiceChoseTarget},{choiceRespTime},"
                + $"{npcTotal}");
        }

        Debug.Log($"[DataRecorder] Trial {trialID} written to {csvPath}");
    }
}
