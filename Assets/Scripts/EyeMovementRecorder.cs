using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
#if UNITY_2019_1_OR_NEWER
using UnityEngine.XR;
#endif

/// <summary>
/// EyeMovementRecorder: continuous sampling of head/eye/gaze with minimal trial metadata output.
///
/// Writes per-frame CSV rows including head/eye/gaze/raycast info plus only these trial metadata:
///   participant_id, trial_id, block_index, manipulated_variables_description
///
/// Metadata injection options:
///  - Call RecordTrialSettings(TrialScheduler.TrialSettings ts) to store trial info internally (like DataRecorder).
///  - Optionally call SetActiveTrial(TrialScheduler.TrialSettings ts) to set an explicit active trial used during sampling.
///  - Call ClearActiveTrial() to stop using an active trial.
/// </summary>
public class EyeMovementRecorder : MonoBehaviour
{
    [Header("References (set in Inspector)")]
    public Transform HeadTransform;               // VR head (Camera) transform
    public GameObject LeftEyeGazeObject;          // GameObject with OVREyeGaze (optional)
    public GameObject RightEyeGazeObject;         // GameObject with OVREyeGaze (optional)

    [Header("Recording")]
    public float flushIntervalSeconds = 5f;       // How often to flush buffered data to disk
    public bool recordEveryFrame = true;          // Record every frame or use timed sampling
    public int downsampleEveryNFrames = 1;        // Record every Nth frame if downsampling

    [Header("Raycast / ROI")]
    public LayerMask gazeLayerMask = ~0;          // Layers to detect gaze hits
    public float gazeRayDistance = 30f;           // Max distance for gaze raycast

    private StreamWriter writer;
    private StringBuilder sb = new StringBuilder(1024 * 8);
    private float lastFlushTime = 0f;
    private int frameCounter = 0;
    private string filePath;

    // Optional explicit active TrialSettings (SetActiveTrial)
    private TrialScheduler.TrialSettings activeTrialSettings = null;

    // Minimal TrialRecord (only fields you asked to output)
    private struct TrialRecord
    {
        public int participantID;
        public int trialID;
        public int blockIndex;
        public string manipulatedVariablesDescription;
        public float timestamp;
    }
    private List<TrialRecord> trialRecords = new List<TrialRecord>();

    // --------- Initialization ---------
    void Start()
    {
        if (HeadTransform == null && Camera.main != null)
            HeadTransform = Camera.main.transform;

        string timeStamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        filePath = Path.Combine(Application.persistentDataPath, $"eye_tracking_data_{timeStamp}.csv");
        writer = new StreamWriter(filePath, false, Encoding.UTF8);
        WriteHeader();
        lastFlushTime = Time.realtimeSinceStartup;

#if UNITY_EDITOR
        Debug.Log("[EyeMovementRecorder] Recording to: " + filePath);
#endif
    }

    void WriteHeader()
    {
        string header = string.Join(",",
            new string[] {
                "utc_iso","unix_ms","frame",
                // Head
                "head_px","head_py","head_pz","head_qx","head_qy","head_qz","head_qw",
                // Left eye
                "left_origin_x","left_origin_y","left_origin_z",
                "left_dir_x","left_dir_y","left_dir_z",
                "left_pupil_mm","left_eye_openness","left_confidence",
                // Right eye
                "right_origin_x","right_origin_y","right_origin_z",
                "right_dir_x","right_dir_y","right_dir_z",
                "right_pupil_mm","right_eye_openness","right_confidence",
                // Combined gaze
                "gaze_origin_x","gaze_origin_y","gaze_origin_z",
                "gaze_dir_x","gaze_dir_y","gaze_dir_z",
                // Raycast hit
                "hit_obj_name","hit_obj_tag","hit_obj_id","hit_point_x","hit_point_y","hit_point_z",
                // ROI / custom label
                "roi_id",
                // Minimal Trial metadata
                "participant_id","trial_id","block_index","manipulated_variables_description"
            });
        writer.WriteLine(header);
        writer.Flush();
    }

    // ---------------- Public API for trial linking ----------------

    /// <summary>
    /// Save trial settings into recorder's internal list (call at trial start).
    /// Only stores minimal metadata used in CSV (participantID, trialID, blockIndex, manipulatedVariablesDescription).
    /// </summary>
    public void RecordTrialSettings(TrialScheduler.TrialSettings ts)
    {
        if (ts == null) return;

        TrialRecord tr = new TrialRecord
        {
            participantID = ts.participantID,
            trialID = ts.trialID,
            blockIndex = ts.blockIndex,
            manipulatedVariablesDescription = ts.manipulatedVariablesDescription ?? "",
            timestamp = Time.time
        };
        trialRecords.Add(tr);
        Debug.Log($"[EyeMovementRecorder] Recorded trial metadata: pid={tr.participantID} trial={tr.trialID} block={tr.blockIndex} desc='{tr.manipulatedVariablesDescription}'");
    }

    /// <summary>
    /// Explicitly set an active TrialSettings (used preferentially when writing).
    /// </summary>
    public void SetActiveTrial(TrialScheduler.TrialSettings ts)
    {
        activeTrialSettings = ts;
        if (ts == null) Debug.LogWarning("[EyeMovementRecorder] SetActiveTrial called with NULL");
        else
        {
            try
            {
                Debug.Log($"[EyeMovementRecorder] SetActiveTrial -> participantID: {ts.participantID}, trialID: {ts.trialID}, blockIndex: {ts.blockIndex}");
            }
            catch (Exception ex)
            {
                Debug.LogError("[EyeMovementRecorder] Error reading fields from ts in SetActiveTrial: " + ex);
            }
        }
    }

    /// <summary>
    /// Clear explicit active trial (if you want blank metadata rows while sampling).
    /// </summary>
    public void ClearActiveTrial()
    {
        activeTrialSettings = null;
        Debug.Log("[EyeMovementRecorder] ClearActiveTrial called (active trial cleared).");
    }

    // ---------------- Update / Sampling ----------------
    void Update()
    {
        frameCounter++;
        if (!recordEveryFrame)
            return;

        if ((frameCounter % downsampleEveryNFrames) != 0)
            return;

        SampleAndWrite();

        if (Time.realtimeSinceStartup - lastFlushTime >= flushIntervalSeconds)
        {
            writer.Flush();
            lastFlushTime = Time.realtimeSinceStartup;
        }
    }

    // --------- Main sampling logic ---------
    void SampleAndWrite()
    {
        // Timestamp
        string utcIso = DateTime.UtcNow.ToString("o");
        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Head
        Vector3 headPos = HeadTransform != null ? HeadTransform.position : Vector3.zero;
        Quaternion headRot = HeadTransform != null ? HeadTransform.rotation : Quaternion.identity;

        // Eye data placeholders
        Vector3 leftOrigin = Vector3.zero, leftDir = Vector3.forward;
        Vector3 rightOrigin = Vector3.zero, rightDir = Vector3.forward;
        Vector3 combinedOrigin = Vector3.zero, combinedDir = Vector3.forward;
        float leftPupil = -1f, rightPupil = -1f;
        float leftOpenness = -1f, rightOpenness = -1f;
        float leftConfidence = 0f, rightConfidence = 0f;

        // ----- Prefer OVREyeGaze component (Movement SDK / Oculus Integration) -----
        if (LeftEyeGazeObject != null)
        {
            leftOrigin = LeftEyeGazeObject.transform.position;
            leftDir = LeftEyeGazeObject.transform.forward;
            var lComp = LeftEyeGazeObject.GetComponent("OVREyeGaze");
            if (lComp != null)
            {
                var t = lComp.GetType();
                var conf = t.GetProperty("Confidence");
                if (conf != null) leftConfidence = Convert.ToSingle(conf.GetValue(lComp, null));
                var pupilProp = t.GetProperty("PupilDiameter");
                if (pupilProp != null) leftPupil = Convert.ToSingle(pupilProp.GetValue(lComp, null));
                var openProp = t.GetProperty("EyeOpenness");
                if (openProp != null) leftOpenness = Convert.ToSingle(openProp.GetValue(lComp, null));
            }
        }

        if (RightEyeGazeObject != null)
        {
            rightOrigin = RightEyeGazeObject.transform.position;
            rightDir = RightEyeGazeObject.transform.forward;
            var rComp = RightEyeGazeObject.GetComponent("OVREyeGaze");
            if (rComp != null)
            {
                var t = rComp.GetType();
                var conf = t.GetProperty("Confidence");
                if (conf != null) rightConfidence = Convert.ToSingle(conf.GetValue(rComp, null));
                var pupilProp = t.GetProperty("PupilDiameter");
                if (pupilProp != null) rightPupil = Convert.ToSingle(pupilProp.GetValue(rComp, null));
                var openProp = t.GetProperty("EyeOpenness");
                if (openProp != null) rightOpenness = Convert.ToSingle(openProp.GetValue(rComp, null));
            }
        }

#if UNITY_2019_1_OR_NEWER
        // Optional: OpenXR fallback could be added here if desired.
#endif

        // Merge gaze direction: choose higher confidence or average
        if (leftConfidence > rightConfidence)
        {
            combinedOrigin = leftOrigin;
            combinedDir = leftDir;
        }
        else if (rightConfidence > leftConfidence)
        {
            combinedOrigin = rightOrigin;
            combinedDir = rightDir;
        }
        else
        {
            combinedOrigin = (leftOrigin + rightOrigin) * 0.5f;
            combinedDir = (leftDir.normalized + rightDir.normalized).normalized;
        }

        // Raycast to detect looked-at scene object / ROI
        RaycastHit hit;
        string hitName = "", hitTag = "", hitId = "";
        Vector3 hitPoint = Vector3.zero;
        string roiId = "";

        if (Physics.Raycast(combinedOrigin, combinedDir, out hit, gazeRayDistance, gazeLayerMask))
        {
            hitName = hit.collider.gameObject.name;
            hitTag = hit.collider.gameObject.tag;
            hitId = hit.collider.gameObject.GetInstanceID().ToString();
            hitPoint = hit.point;

            var roi = hit.collider.gameObject.GetComponent("RoiIdentifier");
            if (roi != null)
            {
                var t = roi.GetType();
                var idProp = t.GetProperty("roiId");
                if (idProp != null) roiId = idProp.GetValue(roi, null).ToString();
            }
        }

        // Determine trial metadata to write:
        // Priority: activeTrialSettings (if set) -> most recent recorded TrialRecord -> defaults
        int participantIdOut = -1;
        int trialIdOut = -1;
        int blockIndexOut = -1;
        string manipDescOut = "";

        if (activeTrialSettings != null)
        {
            try
            {
                participantIdOut = activeTrialSettings.participantID;
                trialIdOut = activeTrialSettings.trialID;
                blockIndexOut = activeTrialSettings.blockIndex;
                manipDescOut = activeTrialSettings.manipulatedVariablesDescription ?? "";
            }
            catch (Exception ex)
            {
                Debug.LogError("[EyeMovementRecorder] Exception reading activeTrialSettings: " + ex);
                participantIdOut = -1;
                trialIdOut = -1;
                blockIndexOut = -1;
                manipDescOut = "";
            }
        }
        else if (trialRecords.Count > 0)
        {
            var last = trialRecords[trialRecords.Count - 1];
            participantIdOut = last.participantID;
            trialIdOut = last.trialID;
            blockIndexOut = last.blockIndex;
            manipDescOut = last.manipulatedVariablesDescription ?? "";
        }

        // Build CSV line and write
        sb.Clear();
        void append(params object[] vals)
        {
            for (int i = 0; i < vals.Length; i++)
            {
                if (i > 0) sb.Append(",");
                var v = vals[i];
                if (v == null) sb.Append("");
                else
                {
                    string s = v.ToString();
                    if (s.Contains(",") || s.Contains("\n") || s.Contains("\r")) s = "\"" + s.Replace("\"", "\"\"") + "\"";
                    sb.Append(s);
                }
            }
        }

        append(
            utcIso, unixMs, Time.frameCount,
            headPos.x, headPos.y, headPos.z, headRot.x, headRot.y, headRot.z, headRot.w,
            leftOrigin.x, leftOrigin.y, leftOrigin.z,
            leftDir.x, leftDir.y, leftDir.z,
            leftPupil, leftOpenness, leftConfidence,
            rightOrigin.x, rightOrigin.y, rightOrigin.z,
            rightDir.x, rightDir.y, rightDir.z,
            rightPupil, rightOpenness, rightConfidence,
            combinedOrigin.x, combinedOrigin.y, combinedOrigin.z,
            combinedDir.x, combinedDir.y, combinedDir.z,
            hitName, hitTag, hitId, hitPoint.x, hitPoint.y, hitPoint.z,
            roiId,
            participantIdOut, trialIdOut, blockIndexOut, manipDescOut
        );

        writer.WriteLine(sb.ToString());
    }

    void OnApplicationQuit()
    {
        if (writer != null)
        {
            writer.Flush();
            writer.Close();
            writer = null;
        }
    }

    // Optional: manually stop and save (callable from other scripts)
    public void StopAndSave()
    {
        if (writer != null)
        {
            writer.Flush();
            writer.Close();
            Debug.Log("[EyeMovementRecorder] Saved to: " + filePath);
            writer = null;
        }
    }
}
