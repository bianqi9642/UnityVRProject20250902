using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// UI controller for the experiment, matching the provided hierarchy:
/// - GroupStartPanel (with GroupStartText and ConfirmButton)
/// - TrialStartPanel (with PromptText and ConfirmButton)
/// - GroupFinishPanel (with FinishText and ConfirmButton)
/// - RestPanel (optional)
/// - RouteChoicePanel (with ButtonLeft, ButtonStraight, ButtonRight, and optional RouteChoiceText)
/// 
/// Provides coroutines:
///   ShowGroupStart(groupID, trialCount),
///   ShowTrialStart(trialIndex, totalTrials),
///   ShowRouteChoice(callback),
///   ShowGroupFinish(groupID),
///   (optional) ShowRest(duration)
/// 
/// Each method positions its panel in front of Camera.main at specified distance/offset,
/// shows it, waits for the button click, then hides it.
/// </summary>
public class ExperimentUIController : MonoBehaviour
{
    [Header("Group Start UI")]
    [Tooltip("Panel containing group start prompt. Should be inactive initially.")]
    public GameObject groupStartPanel;
    [Tooltip("TMP_Text under GroupStartPanel to display 'Starting Group X...'")]
    public TMP_Text groupStartText;
    [Tooltip("Confirm Button under GroupStartPanel")]
    public Button groupStartConfirmButton;

    [Header("Trial Start UI")]
    [Tooltip("Panel containing trial start prompt. Should be inactive initially.")]
    public GameObject trialStartPanel;
    [Tooltip("TMP_Text under TrialStartPanel to display 'Get ready for Trial...'")]
    public TMP_Text trialStartText;
    [Tooltip("Confirm Button under TrialStartPanel")]
    public Button trialStartConfirmButton;

    [Header("Group Finish UI")]
    [Tooltip("Panel containing group finish prompt. Should be inactive initially.")]
    public GameObject groupFinishPanel;
    [Tooltip("TMP_Text under GroupFinishPanel to display 'Group X complete...'")]
    public TMP_Text groupFinishText;
    [Tooltip("Confirm Button under GroupFinishPanel")]
    public Button groupFinishConfirmButton;

    [Header("Rest UI (optional)")]
    [Tooltip("Panel containing rest countdown. Should be inactive initially.")]
    public GameObject restPanel;
    [Tooltip("TMP_Text under RestPanel to display remaining time or instructions.")]
    public TMP_Text restText;
    [Tooltip("Confirm Button under RestPanel for early skip (optional).")]
    public Button restConfirmButton;

    [Header("Route Choice UI")]
    [Tooltip("Panel containing Left/Straight/Right buttons. Should be inactive initially.")]
    public GameObject routeChoicePanel;
    [Tooltip("Button under RouteChoicePanel for choosing Left.")]
    public Button buttonLeft;
    [Tooltip("Button under RouteChoicePanel for choosing Straight.")]
    public Button buttonStraight;
    [Tooltip("Button under RouteChoicePanel for choosing Right.")]
    public Button buttonRight;
    [Tooltip("TMP_Text under RouteChoicePanel for optional instructions (e.g. 'Choose route:').")]
    public TMP_Text routeChoiceText;

    [Header("UI Positioning (VR)")]
    [Tooltip("Distance in meters in front of camera to position the panel.")]
    public float panelDistance = 2.0f;
    [Tooltip("Vertical offset in meters relative to camera position. Positive moves panel up, negative moves down.")]
    public float verticalOffset = -0.5f;

    // Internal flags
    private bool groupStartConfirmed = false;
    private bool trialStartConfirmed = false;
    private bool groupFinishConfirmed = false;
    private bool restConfirmed = false;

    private bool routeChoiceMade = false;
    private string routeChoice = "";

    private void Awake()
    {
        // Ensure all panels are hidden initially
        if (groupStartPanel != null) groupStartPanel.SetActive(false);
        if (trialStartPanel != null) trialStartPanel.SetActive(false);
        if (groupFinishPanel != null) groupFinishPanel.SetActive(false);
        if (restPanel != null) restPanel.SetActive(false);
        if (routeChoicePanel != null) routeChoicePanel.SetActive(false);
    }

    /// <summary>
    /// Position the panel in front of Camera.main at panelDistance and verticalOffset, facing the camera horizontally.
    /// </summary>
    private void PositionPanelInFrontOfCamera(GameObject panel)
    {
        if (panel == null || Camera.main == null) return;
        Transform camT = Camera.main.transform;
        Vector3 forward = camT.forward;
        Vector3 targetPos = camT.position + forward * panelDistance + Vector3.up * verticalOffset;
        panel.transform.position = targetPos;

        // Face panel toward camera, only yaw rotation
        Vector3 lookDir = camT.position - panel.transform.position;
        lookDir.y = 0;
        if (lookDir.sqrMagnitude > 0.001f)
        {
            panel.transform.rotation = Quaternion.LookRotation(lookDir);
        }
    }

    #region Group Start

    /// <summary>
    /// Show “Starting Group {groupID}: {trialCount} trials. Press button to start.”
    /// Waits until groupStartConfirmButton is clicked.
    /// </summary>
    public IEnumerator ShowGroupStart(int groupID, int trialCount)
    {
        groupStartConfirmed = false;
        if (groupStartPanel != null)
        {
            PositionPanelInFrontOfCamera(groupStartPanel);
            groupStartPanel.SetActive(true);
        }
        if (groupStartText != null)
            groupStartText.text = $"Starting Group {groupID}: {trialCount} trials. Press button to start.";
        if (groupStartConfirmButton != null)
            groupStartConfirmButton.onClick.AddListener(OnGroupStartConfirmed);

        while (!groupStartConfirmed)
        {
            yield return null;
        }

        if (groupStartConfirmButton != null)
            groupStartConfirmButton.onClick.RemoveListener(OnGroupStartConfirmed);
        if (groupStartPanel != null)
            groupStartPanel.SetActive(false);
    }

    private void OnGroupStartConfirmed()
    {
        groupStartConfirmed = true;
    }

    #endregion

    #region Trial Start

    /// <summary>
    /// Show “Get ready for Trial {trialIndex}/{totalTrials}. Press button to start.”
    /// Waits until trialStartConfirmButton is clicked.
    /// </summary>
    public IEnumerator ShowTrialStart(int trialIndex, int totalTrials)
    {
        trialStartConfirmed = false;
        if (trialStartPanel != null)
        {
            PositionPanelInFrontOfCamera(trialStartPanel);
            trialStartPanel.SetActive(true);
        }
        if (trialStartText != null)
            trialStartText.text = $"Get ready for Trial {trialIndex}/{totalTrials}. Press button to start.";
        if (trialStartConfirmButton != null)
            trialStartConfirmButton.onClick.AddListener(OnTrialStartConfirmed);

        while (!trialStartConfirmed)
        {
            yield return null;
        }

        if (trialStartConfirmButton != null)
            trialStartConfirmButton.onClick.RemoveListener(OnTrialStartConfirmed);
        if (trialStartPanel != null)
            trialStartPanel.SetActive(false);
    }

    private void OnTrialStartConfirmed()
    {
        trialStartConfirmed = true;
    }

    #endregion

    #region Route Choice

    /// <summary>
    /// Show RouteChoicePanel, waits for Left/Straight/Right button click, then invokes callback with chosen route.
    /// </summary>
    public IEnumerator ShowRouteChoice(Action<string> callback)
    {
        routeChoiceMade = false;
        routeChoice = "";

        if (routeChoicePanel != null)
        {
            PositionPanelInFrontOfCamera(routeChoicePanel);
            routeChoicePanel.SetActive(true);
        }
        // Optionally set routeChoiceText
        if (routeChoiceText != null)
            routeChoiceText.text = "Choose route:";

        // Register listeners
        if (buttonLeft != null)
            buttonLeft.onClick.AddListener(() => OnRouteChoiceClicked("Left"));
        if (buttonStraight != null)
            buttonStraight.onClick.AddListener(() => OnRouteChoiceClicked("Straight"));
        if (buttonRight != null)
            buttonRight.onClick.AddListener(() => OnRouteChoiceClicked("Right"));

        while (!routeChoiceMade)
        {
            yield return null;
        }

        // Cleanup listeners
        if (buttonLeft != null)
            buttonLeft.onClick.RemoveListener(() => OnRouteChoiceClicked("Left"));
        if (buttonStraight != null)
            buttonStraight.onClick.RemoveListener(() => OnRouteChoiceClicked("Straight"));
        if (buttonRight != null)
            buttonRight.onClick.RemoveListener(() => OnRouteChoiceClicked("Right"));

        if (routeChoicePanel != null)
            routeChoicePanel.SetActive(false);

        callback?.Invoke(routeChoice);
    }

    private void OnRouteChoiceClicked(string choice)
    {
        routeChoice = choice;
        routeChoiceMade = true;
    }

    #endregion

    #region Group Finish

    /// <summary>
    /// Show “Group {groupID} complete. Thank you!” and wait until groupFinishConfirmButton is clicked.
    /// </summary>
    public IEnumerator ShowGroupFinish(int groupID)
    {
        groupFinishConfirmed = false;
        if (groupFinishPanel != null)
        {
            PositionPanelInFrontOfCamera(groupFinishPanel);
            groupFinishPanel.SetActive(true);
        }
        if (groupFinishText != null)
            groupFinishText.text = $"Group {groupID} complete. Thank you!";
        if (groupFinishConfirmButton != null)
            groupFinishConfirmButton.onClick.AddListener(OnGroupFinishConfirmed);

        while (!groupFinishConfirmed)
        {
            yield return null;
        }

        if (groupFinishConfirmButton != null)
            groupFinishConfirmButton.onClick.RemoveListener(OnGroupFinishConfirmed);
        if (groupFinishPanel != null)
            groupFinishPanel.SetActive(false);
    }

    private void OnGroupFinishConfirmed()
    {
        groupFinishConfirmed = true;
    }

    #endregion

    #region Rest (optional)

    /// <summary>
    /// Show RestPanel for duration seconds, or allow early skip via restConfirmButton if assigned.
    /// </summary>
    public IEnumerator ShowRest(float duration)
    {
        restConfirmed = false;
        float elapsed = 0f;

        if (restPanel != null)
        {
            PositionPanelInFrontOfCamera(restPanel);
            restPanel.SetActive(true);
        }
        if (restConfirmButton != null)
            restConfirmButton.onClick.AddListener(OnRestConfirmed);

        while (elapsed < duration && !restConfirmed)
        {
            if (restText != null)
                restText.text = $"Resting: {duration - elapsed:F0} sec remaining\n(or click to continue)";
            yield return new WaitForSeconds(1f);
            elapsed += 1f;
        }

        if (restConfirmButton != null)
            restConfirmButton.onClick.RemoveListener(OnRestConfirmed);
        if (restPanel != null)
            restPanel.SetActive(false);
    }

    private void OnRestConfirmed()
    {
        restConfirmed = true;
    }

    #endregion
}
