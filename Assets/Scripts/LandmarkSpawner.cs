using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LandmarkSpawner : MonoBehaviour
{
    public Transform leftAnchor;
    public Transform rightAnchor;
    public GameObject landmarkPrefab;

    private GameObject currentLandmark;

    public void SetLandmarkSide(bool left)
    {
        if (currentLandmark != null)
            Destroy(currentLandmark);

        Transform anchor = left ? leftAnchor : rightAnchor;
        if (landmarkPrefab != null && anchor != null)
        {
            currentLandmark = Instantiate(landmarkPrefab, anchor.position, anchor.rotation, anchor);
            currentLandmark.name = "Landmark";
        }
        else
        {
            Debug.LogWarning("[LandmarkSpawner] missing prefab or anchors");
        }
    }
}