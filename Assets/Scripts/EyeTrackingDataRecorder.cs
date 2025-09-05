using UnityEngine;
using System.IO;
using System.Text;

/// <summary>
/// Records eye tracking data (from OVREyeGaze) into a CSV file.
/// Attach this script to OVRCameraRig (or any object).
/// Make sure OVREyeGaze is present in the scene and linked in Inspector.
/// </summary>
public class EyeTrackingDataRecorder : MonoBehaviour
{
    [Header("References")]
    public OVREyeGaze eyeGaze; // Assign OVREyeGaze component from OVRCameraRig

    private StreamWriter writer;
    private string filePath;

    void Start()
    {
        // Save to persistentDataPath (works in Editor and on Quest)
        string timeStamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        filePath = Path.Combine(Application.persistentDataPath, $"eye_tracking_data_{timeStamp}.csv");

        writer = new StreamWriter(filePath, false, Encoding.UTF8);

        // CSV header
        writer.WriteLine("Time,GazeOriginX,GazeOriginY,GazeOriginZ,GazeDirX,GazeDirY,GazeDirZ,Confidence");
        Debug.Log($"Eye tracking data will be saved to: {filePath}");
    }

    void Update()
    {
        if (eyeGaze == null) Debug.LogError("EyeGaze reference is missing!");

        // Use transform position/forward instead of GetRay()
        if (eyeGaze.EyeTrackingEnabled && eyeGaze.Confidence > 0f)
        {
            Vector3 origin = eyeGaze.transform.position;
            Vector3 direction = eyeGaze.transform.forward;

            string line = string.Format(
                "{0:F4},{1:F4},{2:F4},{3:F4},{4:F4},{5:F4},{6:F4},{7:F2}",
                Time.time,
                origin.x, origin.y, origin.z,
                direction.x, direction.y, direction.z,
                eyeGaze.Confidence
            );

            writer.WriteLine(line);
        }

        Debug.Log("EyeTrackingEnabled: " + eyeGaze.EyeTrackingEnabled);
        Debug.Log("Confidence: " + eyeGaze.Confidence);

    }

    void OnApplicationQuit()
    {
        if (writer != null)
        {
            writer.Flush();
            writer.Close();
            writer.Dispose();
        }
    }
}
