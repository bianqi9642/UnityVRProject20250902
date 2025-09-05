using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.XR;

public class OpenXREyeTracker : MonoBehaviour
{
    InputDevice eyeDevice;
    string path;

    void Start()
    {
        path = Path.Combine(Application.persistentDataPath, "openxr_eyegaze.csv");
        File.WriteAllText(path, "time,origin_x,origin_y,origin_z,dir_x,dir_y,dir_z\n");
        TryFindEyeDevice();
    }

    void TryFindEyeDevice()
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.EyeTracking, devices);
        if (devices.Count > 0)
        {
            eyeDevice = devices[0];
            Debug.Log($"Found eye device: {eyeDevice.name}");
        }
        else
        {
            Debug.LogWarning("No eye tracking device found (yet).");
        }
    }

    void Update()
    {
        if (!eyeDevice.isValid)
        {
            TryFindEyeDevice();
            return;
        }

        // gaze origin (position) & rotation (direction)
        Vector3 gazeOrigin;
        Quaternion gazeRot;

        bool hasPos = eyeDevice.TryGetFeatureValue(UnityEngine.XR.OpenXR.Features.Interactions.EyeTrackingUsages.gazePosition, out gazeOrigin);
        bool hasRot = eyeDevice.TryGetFeatureValue(UnityEngine.XR.OpenXR.Features.Interactions.EyeTrackingUsages.gazeRotation, out gazeRot);

        if (hasPos && hasRot)
        {
            Vector3 gazeDir = gazeRot * Vector3.forward;
            float t = Time.realtimeSinceStartup;
            string line = $"{t},{gazeOrigin.x},{gazeOrigin.y},{gazeOrigin.z},{gazeDir.x},{gazeDir.y},{gazeDir.z}\n";
            File.AppendAllText(path, line);
        }
    }
}