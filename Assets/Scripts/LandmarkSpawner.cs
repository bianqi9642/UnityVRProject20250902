using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LandmarkSpawner : MonoBehaviour
{
    // The transform used when spawning on the left side
    public Transform leftAnchor;
    // The transform used when spawning on the right side
    public Transform rightAnchor;
    // The landmark prefab asset (set in the Inspector)
    public GameObject landmarkPrefab;

    // The currently spawned landmark instance
    private GameObject currentLandmark;

    /// <summary>
    /// Set the landmark to the left or right anchor.
    /// This implementation preserves the prefab's local transform (localPosition/localRotation/localScale)
    /// and makes the spawned instance a child of the selected anchor so the prefab's local offset
    /// is applied relative to the anchor.
    /// </summary>
    /// <param name="left">If true, spawn at leftAnchor; otherwise spawn at rightAnchor.</param>
    public void SetLandmarkSide(bool left)
    {
        // Destroy previous landmark instance if it exists
        if (currentLandmark != null)
        {
            Destroy(currentLandmark);
        }

        // Choose anchor based on the requested side
        Transform anchor = left ? leftAnchor : rightAnchor;

        if (landmarkPrefab != null && anchor != null)
        {
            // Instantiate the prefab without a parent first.
            // Instantiating without parent ensures the new instance initially copies the prefab's transform values.
            currentLandmark = Instantiate(landmarkPrefab);

            // Name the instance for clarity in the hierarchy
            currentLandmark.name = "Landmark";

            // Set the parent to the anchor.
            // Use worldPositionStays = false so the instance's local transform is preserved
            // and becomes the local transform relative to the anchor.
            currentLandmark.transform.SetParent(anchor, false);

            // As an extra safety step, explicitly copy the prefab's local transform values
            // to the instance so we are robust across Unity versions and prefab setups.
            Transform prefabT = landmarkPrefab.transform;
            currentLandmark.transform.localPosition = prefabT.localPosition;
            currentLandmark.transform.localRotation = prefabT.localRotation;
            currentLandmark.transform.localScale = prefabT.localScale;
        }
        else
        {
            Debug.LogWarning("[LandmarkSpawner] missing prefab or anchors");
        }
    }
}
