using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPCSpawner handles the instantiation of NPCs in a rectangular area, assigning each NPC:
/// - an outfit type (Uniform vs. Casual),
/// - a movement style (Direct vs. Uncertain),
/// - a destination choice (Left or Right).
/// 
/// Changes in this version:
/// - Adds optional spawn-over-time behaviour with random interval range (minSpawnInterval..maxSpawnInterval).
///   When spawnOverTime == true, NPCs are instantiated one-by-one with a random delay between spawns,
///   which creates a more natural appearance rhythm. When false, behaviour falls back to immediate batch spawn.
/// - Internally refactors spawning to first produce a list of NPC-configurations (role/outfit/speed/style),
///   then either instantiate them immediately or drive a coroutine that spawns them over time.
/// - All original per-path and legacy ratio logic preserved; this just changes *when* the created NPCs are instantianted.
/// 
/// All comments are in English.
/// </summary>
public class NPCSpawner : MonoBehaviour
{
    [Header("NPC Prefab")]
    [Tooltip("Prefab must have NPCMoverOffset component and a NavMeshAgent on the root.")]
    public GameObject npcPrefab;

    [Header("Spawn Settings")]
    [Tooltip("Total number of NPCs to spawn (legacy field, used when per-path counts are not provided).")]
    public int npcCount = 10;
    [Tooltip("Ratio (0–1) of NPCs that will wear Uniform. Others wear Casual.")]
    [Range(0f, 1f)]
    public float uniformRatio = 0.5f;
    [Tooltip("Spawn area center in world space.")]
    public Vector3 spawnAreaCenter = Vector3.zero;
    [Tooltip("Size (X,Z) of the rectangular spawn area. Y is ignored.")]
    public Vector3 spawnAreaSize = new Vector3(10f, 0f, 10f);
    [Tooltip("When spawning, sample NavMesh within this radius around the random spawn point.")]
    public float spawnSampleRadius = 2f;

    [Header("Destination Ratios")]
    [Tooltip("Ratio (0–1) choosing Left as target after center.")]
    [Range(0f, 1f)]
    public float leftRatio = 0.5f;
    [Tooltip("Ratio (0–1) choosing Right as target after center.")]
    [Range(0f, 1f)]
    public float rightRatio = 0.5f;
    // If leftRatio + rightRatio != 1, we normalize them so they sum to 1.

    [Header("Movement Settings")]
    [Tooltip("Movement speed (used for all NPCs) - legacy field, kept for backward compatibility.")]
    public float moveSpeed = 1.5f;
    [Tooltip("Ratio (0–1) of NPCs that will use Direct movement style. Others use Uncertain.")]
    [Range(0f, 1f)]
    public float directRatio = 0.7f;

    [Header("Path Points (Assign in Inspector)")]
    [Tooltip("Starting point Transform for NPCMoverOffset.")]
    public Transform pointStart;
    [Tooltip("Center point Transform for NPCMoverOffset.")]
    public Transform pointCenter;
    [Tooltip("Left target point Transform.")]
    public Transform pointLeft;
    [Tooltip("Right target point Transform.")]
    public Transform pointRight;

    // ---- New per-trial inputs (set by GameManager.ConfigureSpawnerFromTrial) ----
    [Header("Per-trial (optional) - if set, spawn uses these explicit counts & params")]
    [Tooltip("If >0 this is the total number of NPCs for the trial (targetCount + distractorCount).")]
    public int totalCount = 0;
    [Tooltip("Number of NPCs assigned to the target path.")]
    public int targetCount = 0;
    [Tooltip("Number of NPCs assigned to the distractor path.")]
    public int distractorCount = 0;
    [Tooltip("If true, the 'target' path corresponds to the Left destination; else Target -> Right.")]
    public bool targetIsLeft = true;
    [Tooltip("Per-path parameters (npcNumber, credibleProportion, directionConsistency, speed, movementStyle) for target.")]
    public PathConfigurator.PathParameters targetParams;
    [Tooltip("Per-path parameters (npcNumber, credibleProportion, directionConsistency, speed, movementStyle) for distractor.")]
    public PathConfigurator.PathParameters distractorParams;
    // ---------------------------------------------------------------------------

    // ---- New spawn-over-time settings ----
    [Header("Spawn Over Time (optional)")]
    [Tooltip("When true, NPCs will be spawned one-by-one over time using a random delay between minSpawnInterval and maxSpawnInterval.")]
    public bool spawnOverTime = true;
    [Tooltip("Minimum delay (seconds) between consecutive spawns when spawnOverTime is enabled.")]
    public float minSpawnInterval = 0.1f;
    [Tooltip("Maximum delay (seconds) between consecutive spawns when spawnOverTime is enabled.")]
    public float maxSpawnInterval = 0.5f;
    [Tooltip("Optional initial delay (seconds) before the first spawn when spawnOverTime is enabled.")]
    public float initialSpawnDelay = 0.0f;
    // --------------------------------------------------------------------------------

    // Nested enum for destination choice
    private enum DestChoice { Left, Right }

    // Internal handle for active spawning coroutine
    private Coroutine spawnCoroutine = null;

    void Start()
    {
        // Basic checks on required references
        if (npcPrefab == null)
        {
            Debug.LogError("[NPCSpawner] npcPrefab is not assigned.");
            return;
        }
        if (pointStart == null || pointCenter == null)
        {
            Debug.LogWarning("[NPCSpawner] pointStart and/or pointCenter not assigned. NPCMoverOffset may warn.");
        }
        // SpawnNPCs not automatically called here (GameManager calls it)
    }

    /// <summary>
    /// Main spawning entry point called by GameManager (or other controller).
    /// This method decides whether to use explicit per-path counts or legacy ratio logic,
    /// builds a list of spawn configurations, and either spawns them immediately or over time
    /// according to 'spawnOverTime', 'minSpawnInterval' and 'maxSpawnInterval'.
    /// </summary>
    public void SpawnNPCs()
    {
        // Validate spawn-over-time parameters to avoid negative or inverted ranges.
        minSpawnInterval = Mathf.Max(0f, minSpawnInterval);
        maxSpawnInterval = Mathf.Max(minSpawnInterval, maxSpawnInterval); // ensure max >= min
        initialSpawnDelay = Mathf.Max(0f, initialSpawnDelay);

        // If a previous coroutine is running, stop it to avoid duplicate spawns.
        if (spawnCoroutine != null)
        {
            StopCoroutine(spawnCoroutine);
            spawnCoroutine = null;
        }

        List<NPCConfig> spawnList = null;

        int explicitTotal = targetCount + distractorCount;
        if (explicitTotal > 0)
        {
            spawnList = BuildSpawnListUsingPerPathCounts();
        }
        else
        {
            spawnList = BuildSpawnListUsingLegacyRatioLogic();
        }

        if (spawnList == null || spawnList.Count == 0)
        {
            Debug.LogWarning("[NPCSpawner] Nothing to spawn (empty spawn list).");
            return;
        }

        if (spawnOverTime)
        {
            // Start spawning over time with random intervals
            spawnCoroutine = StartCoroutine(SpawnListOverTimeCoroutine(spawnList));
        }
        else
        {
            // Immediate, legacy-style batch spawn
            foreach (var cfg in spawnList)
            {
                InstantiateFromConfig(cfg);
            }
        }
    }

    /// <summary>
    /// Build a list of NPC configurations using explicit per-path counts and parameters.
    /// Does NOT actually instantiate any GameObjects; the returned list is later consumed
    /// either instantly or by a coroutine.
    /// </summary>
    private List<NPCConfig> BuildSpawnListUsingPerPathCounts()
    {
        // defensive checks
        int computedTotal = Mathf.Max(0, targetCount + distractorCount);
        totalCount = computedTotal;
        if (totalCount <= 0)
        {
            Debug.LogWarning("[NPCSpawner] Per-path spawn: totalCount <= 0, aborting.");
            return new List<NPCConfig>();
        }

        if (targetParams == null) targetParams = new PathConfigurator.PathParameters();
        if (distractorParams == null) distractorParams = new PathConfigurator.PathParameters();

        Debug.Log($"[NPCSpawner] Per-path spawn: total={totalCount}, targetCount={targetCount}, distractCount={distractorCount}");
        Debug.Log($"[NPCSpawner] target.speed={targetParams.speed:F2}, target.credible={targetParams.credibleProportion:F2}, target.style={targetParams.movementStyle}");
        Debug.Log($"[NPCSpawner] distract.speed={distractorParams.speed:F2}, distract.credible={distractorParams.credibleProportion:F2}, distract.style={distractorParams.movementStyle}");

        // Build role list (Target / Distractor)
        List<DestChoice> destRoles = new List<DestChoice>(totalCount);
        for (int i = 0; i < targetCount; i++) destRoles.Add(DestChoice.Left); // placeholder meaning "target"
        for (int i = 0; i < distractorCount; i++) destRoles.Add(DestChoice.Right); // placeholder meaning "distractor"
        ShuffleList(destRoles);

        // Build outfit lists for each role based on credibleProportion:
        List<NPCMoverOffset.OutfitType> outfitsTarget = new List<NPCMoverOffset.OutfitType>(targetCount);
        int targetUniformCount = Mathf.RoundToInt(Mathf.Clamp01(targetParams.credibleProportion) * targetCount);
        targetUniformCount = Mathf.Clamp(targetUniformCount, 0, targetCount);
        for (int i = 0; i < targetUniformCount; i++) outfitsTarget.Add(NPCMoverOffset.OutfitType.Uniform);
        for (int i = 0; i < (targetCount - targetUniformCount); i++) outfitsTarget.Add(NPCMoverOffset.OutfitType.Casual);
        ShuffleList(outfitsTarget);

        List<NPCMoverOffset.OutfitType> outfitsDistr = new List<NPCMoverOffset.OutfitType>(distractorCount);
        int distractUniformCount = Mathf.RoundToInt(Mathf.Clamp01(distractorParams.credibleProportion) * distractorCount);
        distractUniformCount = Mathf.Clamp(distractUniformCount, 0, distractorCount);
        for (int i = 0; i < distractUniformCount; i++) outfitsDistr.Add(NPCMoverOffset.OutfitType.Uniform);
        for (int i = 0; i < (distractorCount - distractUniformCount); i++) outfitsDistr.Add(NPCMoverOffset.OutfitType.Casual);
        ShuffleList(outfitsDistr);

        int tOutIdx = 0;
        int dOutIdx = 0;

        // Compose the spawn list
        List<NPCConfig> spawnList = new List<NPCConfig>(totalCount);
        for (int i = 0; i < totalCount; i++)
        {
            bool isTargetRole = (destRoles[i] == DestChoice.Left);

            NPCConfig cfg = new NPCConfig();

            // Common references: start and center will be assigned at instantiation time
            cfg.spawnPosition = SampleNavMeshPosition(GetRandomPointInArea(spawnAreaCenter, spawnAreaSize), spawnSampleRadius);

            if (isTargetRole)
            {
                // assign destination side according to targetIsLeft
                cfg.goLeft = targetIsLeft;
                cfg.outfit = (tOutIdx < outfitsTarget.Count) ? outfitsTarget[tOutIdx++] : NPCMoverOffset.OutfitType.Casual;
                cfg.movementStyle = targetParams.movementStyle;
                cfg.moveSpeed = Mathf.Max(0f, targetParams.speed);
            }
            else
            {
                // distractor -> opposite destination
                cfg.goLeft = !targetIsLeft;
                cfg.outfit = (dOutIdx < outfitsDistr.Count) ? outfitsDistr[dOutIdx++] : NPCMoverOffset.OutfitType.Casual;
                cfg.movementStyle = distractorParams.movementStyle;
                cfg.moveSpeed = Mathf.Max(0f, distractorParams.speed);
            }

            spawnList.Add(cfg);
        }

        return spawnList;
    }

    /// <summary>
    /// Build a list of NPC configurations using the legacy ratio logic (npcCount, uniformRatio, directRatio, left/right ratios).
    /// Does not instantiate objects; returns the config list for later instantiation (immediate or over time).
    /// </summary>
    private List<NPCConfig> BuildSpawnListUsingLegacyRatioLogic()
    {
        // 1. Compute outfit counts: Uniform vs Casual
        int uniformCount = Mathf.RoundToInt(uniformRatio * npcCount);
        uniformCount = Mathf.Clamp(uniformCount, 0, npcCount);
        int casualCount = npcCount - uniformCount;

        // 2. Compute movement style counts: Direct vs Uncertain
        int directCount = Mathf.RoundToInt(directRatio * npcCount);
        directCount = Mathf.Clamp(directCount, 0, npcCount);
        int uncertainCount = npcCount - directCount;

        // 3. Compute destination counts: Left, Right (normalized)
        float rawLeft = leftRatio;
        float rawRight = rightRatio;
        float sumLR = rawLeft + rawRight;

        if (sumLR <= 0f)
        {
            rawLeft = 0.5f;
            rawRight = 0.5f;
        }
        else if (Mathf.Abs(sumLR - 1f) > 1e-6f)
        {
            rawLeft = rawLeft / sumLR;
            rawRight = rawRight / sumLR;
        }

        float fLeft = rawLeft * npcCount;
        float fRight = rawRight * npcCount;

        int floorLeft = Mathf.FloorToInt(fLeft);
        int floorRight = Mathf.FloorToInt(fRight);

        int sumFloors = floorLeft + floorRight;
        int remainder = npcCount - sumFloors;

        var fracs = new List<(DestChoice choice, float frac)>();
        fracs.Add((DestChoice.Left, fLeft - floorLeft));
        fracs.Add((DestChoice.Right, fRight - floorRight));
        fracs.Sort((a, b) => b.frac.CompareTo(a.frac));

        int leftCount = floorLeft;
        int rightCount = floorRight;

        for (int i = 0; i < remainder; i++)
        {
            DestChoice key = fracs[i % fracs.Count].choice;
            switch (key)
            {
                case DestChoice.Left:
                    leftCount++;
                    break;
                case DestChoice.Right:
                    rightCount++;
                    break;
            }
        }

        // 4a. Build outfit assignments
        List<NPCMoverOffset.OutfitType> outfitList = new List<NPCMoverOffset.OutfitType>(npcCount);
        for (int i = 0; i < uniformCount; i++) outfitList.Add(NPCMoverOffset.OutfitType.Uniform);
        for (int i = 0; i < casualCount; i++) outfitList.Add(NPCMoverOffset.OutfitType.Casual);
        ShuffleList(outfitList);

        // 4b. Build movement style assignments
        List<NPCMoverOffset.MovementStyle> styleList = new List<NPCMoverOffset.MovementStyle>(npcCount);
        for (int i = 0; i < directCount; i++) styleList.Add(NPCMoverOffset.MovementStyle.Direct);
        for (int i = 0; i < uncertainCount; i++) styleList.Add(NPCMoverOffset.MovementStyle.Uncertain);
        ShuffleList(styleList);

        // 4c. Build destination assignments
        List<DestChoice> destList = new List<DestChoice>(npcCount);
        for (int i = 0; i < leftCount; i++) destList.Add(DestChoice.Left);
        for (int i = 0; i < rightCount; i++) destList.Add(DestChoice.Right);
        ShuffleList(destList);

        // 5. Build spawn list
        List<NPCConfig> spawnList = new List<NPCConfig>(npcCount);
        for (int i = 0; i < npcCount; i++)
        {
            NPCConfig cfg = new NPCConfig();
            cfg.spawnPosition = SampleNavMeshPosition(GetRandomPointInArea(spawnAreaCenter, spawnAreaSize), spawnSampleRadius);

            // destination
            cfg.goLeft = (destList[i] == DestChoice.Left);

            // speed: legacy single speed applied to all
            cfg.moveSpeed = moveSpeed;

            // movement style and outfit
            cfg.movementStyle = styleList[i];
            cfg.outfit = outfitList[i];

            spawnList.Add(cfg);
        }

        return spawnList;
    }

    /// <summary>
    /// Coroutine that spawns the provided list of NPCConfigs over time.
    /// Each spawn is separated by a random delay drawn from [minSpawnInterval, maxSpawnInterval].
    /// The coroutine respects an initialSpawnDelay (can be zero).
    /// </summary>
    private IEnumerator SpawnListOverTimeCoroutine(List<NPCConfig> spawnList)
    {
        if (spawnList == null || spawnList.Count == 0) yield break;

        if (initialSpawnDelay > 0f)
            yield return new WaitForSeconds(initialSpawnDelay);

        for (int i = 0; i < spawnList.Count; i++)
        {
            InstantiateFromConfig(spawnList[i]);

            // If this is the last one, don't wait
            if (i >= spawnList.Count - 1) break;

            float delay = Random.Range(minSpawnInterval, maxSpawnInterval);
            yield return new WaitForSeconds(delay);
        }

        spawnCoroutine = null;
    }

    /// <summary>
    /// Instantiate a single NPC from the provided configuration and assign mover fields.
    /// This centralizes the instantiate + configure logic so immediate and over-time spawning use identical setup.
    /// </summary>
    private void InstantiateFromConfig(NPCConfig cfg)
    {
        if (npcPrefab == null) return;

        GameObject npcInstance = Instantiate(npcPrefab, cfg.spawnPosition, Quaternion.identity, this.transform);
        NPCMoverOffset mover = npcInstance.GetComponent<NPCMoverOffset>();
        if (mover == null)
        {
            Debug.LogWarning($"[NPCSpawner] Instantiated prefab missing NPCMoverOffset on instance {npcInstance.name}. Destroying it.");
            Destroy(npcInstance);
            return;
        }

        // Assign common points
        mover.pointStart = pointStart;
        mover.pointCenter = pointCenter;

        // Assign destination side
        if (cfg.goLeft)
        {
            mover.pointLeft = pointLeft;
            mover.pointRight = null;
        }
        else
        {
            mover.pointLeft = null;
            mover.pointRight = pointRight;
        }

        // Movement style and speed
        mover.movementStyle = cfg.movementStyle;
        mover.moveSpeed = Mathf.Max(0f, cfg.moveSpeed);

        // Outfit
        mover.randomizeOutfitOnStart = false;
        mover.outfitType = cfg.outfit;

        // Optionally name instances for easier debugging
        npcInstance.name = $"NPC_spawn_{(cfg.goLeft ? "L" : "R")}_{Random.Range(1000, 9999)}";

        Debug.Log($"[NPCSpawner] Spawned {npcInstance.name} speed={mover.moveSpeed:F2} style={mover.movementStyle} outfit={mover.outfitType} -> left={(mover.pointLeft!=null)} right={(mover.pointRight!=null)}");
    }

    /// <summary>
    /// Shuffles a List<T> in place using Fisher–Yates and UnityEngine.Random.
    /// </summary>
    private void ShuffleList<T>(List<T> list)
    {
        int n = list.Count;
        for (int i = n - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            T tmp = list[i];
            list[i] = list[j];
            list[j] = tmp;
        }
    }

    /// <summary>
    /// Returns a random point within a box defined by center and size (X,Z). Y is fixed to center.y.
    /// </summary>
    private Vector3 GetRandomPointInArea(Vector3 center, Vector3 size)
    {
        float halfX = size.x * 0.5f;
        float halfZ = size.z * 0.5f;
        Vector3 offset = new Vector3(
            Random.Range(-halfX, halfX),
            0f,
            Random.Range(-halfZ, halfZ)
        );
        return new Vector3(center.x + offset.x, center.y, center.z + offset.z);
    }

    /// <summary>
    /// Sample the NavMesh around 'point' within 'radius'. If a valid NavMesh position is found, return it; otherwise return original point.
    /// </summary>
    private Vector3 SampleNavMeshPosition(Vector3 point, float radius)
    {
        NavMeshHit hit;
        if (NavMesh.SamplePosition(point, out hit, radius, NavMesh.AllAreas))
        {
            return hit.position;
        }
        return point;
    }

    /// <summary>
    /// Public cleanup method: destroy all spawned NPC children under this spawner.
    /// Call this at end of trial.
    /// </summary>
    public void CleanupNPCs()
    {
        // Stop any running spawn coroutine
        if (spawnCoroutine != null)
        {
            StopCoroutine(spawnCoroutine);
            spawnCoroutine = null;
        }

        // Destroy all child GameObjects (assuming spawned NPCs are parented here)
        for (int i = this.transform.childCount - 1; i >= 0; i--)
        {
            Transform child = this.transform.GetChild(i);
            Destroy(child.gameObject);
        }
    }

    /// <summary>
    /// Simple struct to describe a single NPC instance to be spawned (role/outfit/speed/destination/etc).
    /// This keeps instantiation logic decoupled from spawn-list building.
    /// </summary>
    private class NPCConfig
    {
        public Vector3 spawnPosition = Vector3.zero;
        public bool goLeft = true;
        public NPCMoverOffset.OutfitType outfit = NPCMoverOffset.OutfitType.Casual;
        public NPCMoverOffset.MovementStyle movementStyle = NPCMoverOffset.MovementStyle.Direct;
        public float moveSpeed = 1.5f;
    }
}