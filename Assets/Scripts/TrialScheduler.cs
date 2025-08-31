using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// TrialScheduler: produces trial sequences for a participant under the
/// single-factor pilot design with group assignment and Latin-square control.
///
/// - Participants are split into 4 groups (selectable by experimenter).
/// - Each group contains three factors to be tested (see Group mapping).
/// - The order of the three factors (blocks) is controlled by a 3x3 Latin square
///   using row = participantID % 3.
/// - For each factor, the participant tests ALL levels of that factor; the order
///   of the levels is controlled by a cyclic Latin square of size L (L = number of levels),
///   using row = participantID % L. Each level sequence is repeated repeatsPerLevel times.
/// - Splitting of total NPCs into target/distractor counts is determined by the
///   direction-consistency proportions (distractorProportion p -> target = 1-p),
///   using rounding to get integer counts that sum to total.
/// 
/// NOTE: To actually make NPCs on target/distractor move at different speeds at runtime,
/// GameManager / NPCSpawner must pass per-path speeds to each NPC (currently NPCSpawner assigns
/// a single averaged moveSpeed to all NPCs). See notes in comments below.
/// </summary>
public class TrialScheduler : MonoBehaviour
{
    [Header("Design")]
    public int repeatsPerLevel = 3; // repeat each level 3 times (spec)

    // Factor level definitions
    public int[] numberLevels = new int[] { 5, 10, 15, 20, 25, 30 }; // TOTAL NPCs
    public float[] credibleLevels = new float[] { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f };
    public float[] directionConsistencyLevels = new float[] { 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f }; // distractor proportion p
    public float[] speedLevels = new float[] { 0.6f, 1.0f, 1.4f, 1.8f, 2.2f };
    // movement style levels are Direct and Uncertain
    private NPCMoverOffset.MovementStyle[] styleLevels = new NPCMoverOffset.MovementStyle[] {
        NPCMoverOffset.MovementStyle.Direct, NPCMoverOffset.MovementStyle.Uncertain
    };

    // Constants used when a factor is NOT being studied
    private const int BASE_TOTAL_N = 20;
    private const float BASE_CREDIBLE = 0f;
    private const float BASE_SPEED = 1.4f;
    private const NPCMoverOffset.MovementStyle BASE_STYLE = NPCMoverOffset.MovementStyle.Direct;

    // Mapping factor names to an enum
    public enum Factor { Number = 0, Credible = 1, Direction = 2, Speed = 3, Style = 4 }

    [Serializable]
    public class TrialSettings
    {
        public int trialID;
        public int participantID;
        public bool landmarkLeft;

        public PathConfig targetPath;
        public PathConfig distractorPath;

        // metadata
        public string manipulatedVariablesDescription;
        public int blockIndex; // which of the 3 factor-blocks (0..2)
        public int factorIndexWithinBlock; // 0..2 index of factor within group
        public int levelIndex; // index of level within the factor's level set
        public int repetitionIndex; // 0..repeatsPerLevel-1
        public Factor manipulatedFactor;
    }

    [Serializable]
    public class PathConfig
    {
        public int npcNumber = 20;
        public float credibleProportion = 0f;
        public float directionConsistency = 0.75f; // interpreted as proportion for splitting
        public float speed = 1.4f;
        public NPCMoverOffset.MovementStyle movementStyle = NPCMoverOffset.MovementStyle.Direct;

        public PathConfig Clone() { return (PathConfig)this.MemberwiseClone(); }
    }

    /// <summary>
    /// Main generator. participantGroup is 1..4 (choose in Inspector / GameManager).
    /// Returns a list of fully populated TrialSettings where:
    ///  - For Speed-study: target.speed == 1.4f AND distractor.speed == level
    ///  - For Credible-study: target.credibleProportion == 0f AND distractor.credibleProportion == level
    /// </summary>
    public List<TrialSettings> GenerateTrialsForParticipant(int participantID, int participantGroup)
    {
        // Ensure participantGroup is within 1..4
        int group = Mathf.Clamp(participantGroup, 1, 4);

        // deterministic RNG (reproducible): use participantID-based seed
        System.Random rng = new System.Random(participantID + 12345);

        // Build factor set for each group
        // Group 1: Number, Direction, Style
        // Group 2: Number, Speed, Style
        // Group 3: Credible, Direction, Style
        // Group 4: Credible, Speed, Style
        Factor[] groupFactors;
        switch (group)
        {
            case 1:
                groupFactors = new Factor[] { Factor.Number, Factor.Direction, Factor.Style };
                break;
            case 2:
                groupFactors = new Factor[] { Factor.Number, Factor.Speed, Factor.Style };
                break;
            case 3:
                groupFactors = new Factor[] { Factor.Credible, Factor.Direction, Factor.Style };
                break;
            case 4:
            default:
                groupFactors = new Factor[] { Factor.Credible, Factor.Speed, Factor.Style };
                break;
        }

        // Determine block (factor) order using 3x3 Latin square (cyclic)
        // Rows:
        // 0: 0,1,2
        // 1: 1,2,0
        // 2: 2,0,1
        int latinRowForFactorOrder = participantID % 3;
        int[] latinOrder = new int[3];
        for (int i = 0; i < 3; i++)
            latinOrder[i] = (latinRowForFactorOrder + i) % 3;

        // Prepare the final list
        var finalTrials = new List<TrialSettings>();

        int globalTrialCounter = 0;

        // For each block (in Latin order), produce all levels of that factor,
        // with levels ordered by a cyclic Latin square row determined by participantID % L,
        // and repeat the level sequence repeatsPerLevel times.
        for (int blockPos = 0; blockPos < 3; blockPos++)
        {
            int factorIdx = latinOrder[blockPos];
            Factor currentFactor = groupFactors[factorIdx];

            // get the levels array and L
            int L_int = 0; // for number levels (int)
            float[] L_float = null;
            NPCMoverOffset.MovementStyle[] L_style = null;
            bool isIntLevels = false, isFloatLevels = false;

            switch (currentFactor)
            {
                case Factor.Number:
                    isIntLevels = true;
                    L_int = numberLevels.Length;
                    break;
                case Factor.Credible:
                    isFloatLevels = true;
                    L_float = credibleLevels;
                    break;
                case Factor.Direction:
                    isFloatLevels = true;
                    L_float = directionConsistencyLevels;
                    break;
                case Factor.Speed:
                    isFloatLevels = true;
                    L_float = speedLevels;
                    break;
                case Factor.Style:
                    L_style = styleLevels;
                    break;
            }

            int L;
            if (isIntLevels)
            {
                L = L_int;
            }
            else if (isFloatLevels)
            {
                L = (L_float != null) ? L_float.Length : 0;
            }
            else // style levels
            {
                L = (L_style != null) ? L_style.Length : 0;
            }

            if (L <= 0)
            {
                Debug.LogWarning("[TrialScheduler] Factor has zero levels, skipping.");
                continue;
            }

            // Build cyclic Latin-square row index for levels: row = participantID % L
            int levelRow = (L > 0) ? (participantID % L) : 0;

            // Build the level index order (cyclic shift)
            int[] levelOrder = new int[L];
            for (int k = 0; k < L; k++)
            {
                levelOrder[k] = (levelRow + k) % L;
            }

            // Repeat level sequence repeatsPerLevel times
            for (int rep = 0; rep < repeatsPerLevel; rep++)
            {
                for (int li = 0; li < L; li++)
                {
                    int levelIndex = levelOrder[li];

                    // Build a TrialSettings for this factor-level instance
                    var ts = new TrialSettings();
                    ts.participantID = participantID;
                    ts.blockIndex = blockPos; // 0..2 within the group order
                    ts.factorIndexWithinBlock = factorIdx;
                    ts.repetitionIndex = rep;
                    ts.levelIndex = levelIndex;
                    ts.manipulatedFactor = currentFactor;

                    // By default, init path configs
                    ts.targetPath = new PathConfig();
                    ts.distractorPath = new PathConfig();

                    // Set baseline constants first (these may be overridden below)
                    ts.targetPath.credibleProportion = BASE_CREDIBLE;
                    ts.distractorPath.credibleProportion = BASE_CREDIBLE;
                    ts.targetPath.speed = BASE_SPEED;
                    ts.distractorPath.speed = BASE_SPEED;
                    ts.targetPath.movementStyle = BASE_STYLE;
                    ts.distractorPath.movementStyle = BASE_STYLE;

                    // Default total
                    int totalNPC = BASE_TOTAL_N;

                    // Determine specific settings according to currentFactor
                    if (currentFactor == Factor.Number)
                    {
                        // When studying Number:
                        totalNPC = numberLevels[levelIndex];

                        float distractorProportion = 0.75f; // spec for number-study
                        float targetProportion = 1f - distractorProportion;

                        int tCount = Mathf.RoundToInt(totalNPC * targetProportion);
                        tCount = Mathf.Clamp(tCount, 0, totalNPC);
                        int dCount = totalNPC - tCount;

                        ts.targetPath.npcNumber = tCount;
                        ts.distractorPath.npcNumber = dCount;

                        ts.targetPath.credibleProportion = 0f;
                        ts.distractorPath.credibleProportion = 0f;

                        ts.targetPath.directionConsistency = targetProportion;
                        ts.distractorPath.directionConsistency = distractorProportion;

                        // speeds remain constant in number-study
                        ts.targetPath.speed = BASE_SPEED;
                        ts.distractorPath.speed = BASE_SPEED;

                        ts.targetPath.movementStyle = BASE_STYLE;
                        ts.distractorPath.movementStyle = BASE_STYLE;

                        ts.manipulatedVariablesDescription = $"MANIP=Number total={totalNPC} (t={tCount},d={dCount}) pd={distractorProportion:F2}";
                    }
                    else if (currentFactor == Factor.Credible)
                    {
                        // Credible-study:
                        // - total = BASE_TOTAL_N
                        // - direction-consistency constant = 0.5 (per spec)
                        // - target.credible = 0, distractor.credible = level
                        totalNPC = BASE_TOTAL_N;
                        float distractorProportion = 0.5f;
                        float targetProportion = 1f - distractorProportion;

                        var split = ComputeSplit(totalNPC, targetProportion);
                        ts.targetPath.npcNumber = split.targetCount;
                        ts.distractorPath.npcNumber = split.distractCount;

                        ts.targetPath.credibleProportion = 0f; // target fixed at 0
                        ts.distractorPath.credibleProportion = Mathf.Clamp01(L_float[levelIndex]); // distractor varies

                        ts.targetPath.directionConsistency = targetProportion;
                        ts.distractorPath.directionConsistency = distractorProportion;

                        // speeds remain constant in credible-study
                        ts.targetPath.speed = BASE_SPEED;
                        ts.distractorPath.speed = BASE_SPEED;

                        ts.targetPath.movementStyle = BASE_STYLE;
                        ts.distractorPath.movementStyle = BASE_STYLE;

                        ts.manipulatedVariablesDescription = $"MANIP=Credible distract={ts.distractorPath.credibleProportion:F2}";
                    }
                    else if (currentFactor == Factor.Direction)
                    {
                        // Direction-study:
                        // - distractorProportion = level p, targetProportion = 1 - p
                        // - total = BASE_TOTAL_N
                        float distractorProportion = Mathf.Clamp01(L_float[levelIndex]);
                        float targetProportion = Mathf.Clamp01(1f - distractorProportion);

                        var split = ComputeSplit(BASE_TOTAL_N, targetProportion);
                        ts.targetPath.npcNumber = split.targetCount;
                        ts.distractorPath.npcNumber = split.distractCount;

                        ts.targetPath.credibleProportion = 0f;
                        ts.distractorPath.credibleProportion = 0f;

                        ts.targetPath.directionConsistency = targetProportion;
                        ts.distractorPath.directionConsistency = distractorProportion;

                        // speeds constant during direction-study
                        ts.targetPath.speed = BASE_SPEED;
                        ts.distractorPath.speed = BASE_SPEED;

                        ts.targetPath.movementStyle = BASE_STYLE;
                        ts.distractorPath.movementStyle = BASE_STYLE;

                        ts.manipulatedVariablesDescription = $"MANIP=Direction distractP={distractorProportion:F2} targetP={targetProportion:F2}";
                    }
                    else if (currentFactor == Factor.Speed)
                    {
                        // Speed-study:
                        // - target.speed ALWAYS 1.4 (BASE_SPEED)
                        // - distractor.speed varies over speedLevels
                        // - direction-consistency constant = 0.5 (per spec)
                        float distractorProportion = 0.5f;
                        float targetProportion = 1f - distractorProportion;

                        var split = ComputeSplit(BASE_TOTAL_N, targetProportion);
                        ts.targetPath.npcNumber = split.targetCount;
                        ts.distractorPath.npcNumber = split.distractCount;

                        ts.targetPath.credibleProportion = 0f;
                        ts.distractorPath.credibleProportion = 0f;

                        ts.targetPath.directionConsistency = targetProportion;
                        ts.distractorPath.directionConsistency = distractorProportion;

                        // **KEY POINT**: assign per-path speeds (target fixed at BASE_SPEED, distractor set to level)
                        ts.targetPath.speed = BASE_SPEED;                 // target fixed 1.4 m/s
                        ts.distractorPath.speed = L_float[levelIndex];    // distractor varies (0.6..2.2)

                        ts.targetPath.movementStyle = BASE_STYLE;
                        ts.distractorPath.movementStyle = BASE_STYLE;

                        ts.manipulatedVariablesDescription = $"MANIP=Speed distract={ts.distractorPath.speed:F2} target={ts.targetPath.speed:F2}";
                    }
                    else if (currentFactor == Factor.Style)
                    {
                        // Style-study:
                        // - target.style ALWAYS Direct
                        // - distractor.style varies
                        // - direction-consistency constant = 0.75 (per spec)
                        float distractorProportion = 0.75f;
                        float targetProportion = 1f - distractorProportion;

                        var split = ComputeSplit(BASE_TOTAL_N, targetProportion);
                        ts.targetPath.npcNumber = split.targetCount;
                        ts.distractorPath.npcNumber = split.distractCount;

                        ts.targetPath.credibleProportion = 0f;
                        ts.distractorPath.credibleProportion = 0f;

                        ts.targetPath.directionConsistency = targetProportion;
                        ts.distractorPath.directionConsistency = distractorProportion;

                        ts.targetPath.speed = BASE_SPEED;
                        ts.distractorPath.speed = BASE_SPEED;

                        ts.targetPath.movementStyle = NPCMoverOffset.MovementStyle.Direct;
                        ts.distractorPath.movementStyle = styleLevels[levelIndex];

                        ts.manipulatedVariablesDescription = $"MANIP=Style distract={ts.distractorPath.movementStyle.ToString()} target=Direct";
                    }
                    else
                    {
                        // Fallback default
                        var split = ComputeSplit(BASE_TOTAL_N, 0.5f);
                        ts.targetPath.npcNumber = split.targetCount;
                        ts.distractorPath.npcNumber = split.distractCount;

                        ts.targetPath.credibleProportion = BASE_CREDIBLE;
                        ts.distractorPath.credibleProportion = BASE_CREDIBLE;

                        ts.targetPath.directionConsistency = 0.5f;
                        ts.distractorPath.directionConsistency = 0.5f;

                        ts.targetPath.speed = BASE_SPEED;
                        ts.distractorPath.speed = BASE_SPEED;

                        ts.targetPath.movementStyle = BASE_STYLE;
                        ts.distractorPath.movementStyle = BASE_STYLE;

                        ts.manipulatedVariablesDescription = $"MANIP=Unknown";
                    }

                    // Assign other metadata
                    ts.trialID = globalTrialCounter++;
                    ts.landmarkLeft = rng.NextDouble() > 0.5;

                    finalTrials.Add(ts);
                } // level li
            } // rep
        } // blockPos

        return finalTrials;
    }

    /// <summary>
    /// Helper: compute integer split of total into target/distractor by target proportion.
    /// Returns targetCount (rounded) and distractCount = total - targetCount.
    /// </summary>
    private (int targetCount, int distractCount) ComputeSplit(int total, float targetProp)
    {
        targetProp = Mathf.Clamp01(targetProp);
        int tCount = Mathf.RoundToInt(total * targetProp);
        tCount = Mathf.Clamp(tCount, 0, total);
        int dCount = total - tCount;
        return (tCount, dCount);
    }
}