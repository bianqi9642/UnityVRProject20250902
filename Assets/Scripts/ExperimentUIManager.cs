using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;

/// <summary>
/// ExperimentUIManager
/// - Manages Intro, Choice, Rest and End UI panels
/// - Exposes simple coroutine-based wait methods so GameManager can yield until user presses Start
/// - Routes Left/Right button clicks to GameManager.SubmitParticipantChoice
/// 
/// Usage:
/// - Create a Canvas with 4 Panels: introPanel, choicePanel, restPanel, endPanel
/// - Wire Buttons/Text in the Inspector
/// - Attach this script to a UI GameObject and assign the references
/// - Assign GameManager reference so this UI can call back for choices (or alternatively subscribe to events)
/// </summary>
public class ExperimentUIManager : MonoBehaviour
{
    [Header("Panels")]
    public GameObject introPanel;
    public GameObject choicePanel;
    public GameObject restPanel;
    public GameObject endPanel;

    [Header("Intro UI")]
    [Tooltip("Intro text object (optional)")]
    public TextMeshProUGUI introText;
    [Tooltip("Start button on intro panel")]
    public Button introStartButton;

    [Header("Choice UI")]
    [Tooltip("Prompt text above choice buttons (optional)")]
    public TextMeshProUGUI choicePromptText;
    public Button leftButton;
    public Button rightButton;

    [Header("Rest UI")]
    [Tooltip("Rest prompt text object (optional)")]
    public TextMeshProUGUI restText;
    [Tooltip("Start button on rest panel")]
    public Button restStartButton;

    [Header("End UI")]
    [Tooltip("End text object (optional)")]
    public TextMeshProUGUI endText;

    [Header("References")]
    [Tooltip("Reference to GameManager so UI can call SubmitParticipantChoice or StartExperiment")]
    public GameManager gameManager;

    // Internal flags used by WaitForStartButton coroutine
    private bool startClicked = false;

    void Awake()
    {
        // Safety: hide everything at Awake
        HideAllPanels();

        // Hook up buttons to local handlers
        if (introStartButton != null) introStartButton.onClick.AddListener(OnIntroStartClicked);
        if (restStartButton != null) restStartButton.onClick.AddListener(OnRestStartClicked);
        if (leftButton != null) leftButton.onClick.AddListener(OnLeftClicked);
        if (rightButton != null) rightButton.onClick.AddListener(OnRightClicked);
    }

    void OnDestroy()
    {
        // Unsubscribe to avoid memory leaks
        if (introStartButton != null) introStartButton.onClick.RemoveListener(OnIntroStartClicked);
        if (restStartButton != null) restStartButton.onClick.RemoveListener(OnRestStartClicked);
        if (leftButton != null) leftButton.onClick.RemoveListener(OnLeftClicked);
        if (rightButton != null) rightButton.onClick.RemoveListener(OnRightClicked);
    }

    #region Panel show/hide utilities

    /// <summary> Hide all managed panels </summary>
    public void HideAllPanels()
    {
        if (introPanel != null) introPanel.SetActive(false);
        if (choicePanel != null) choicePanel.SetActive(false);
        if (restPanel != null) restPanel.SetActive(false);
        if (endPanel != null) endPanel.SetActive(false);
    }

    /// <summary> Show intro panel with default message (message can be set via inspector Text) </summary>
    public void ShowIntroPanel()
    {
        HideAllPanels();
        if (introPanel != null) introPanel.SetActive(true);
    }

    /// <summary> Show choice buttons (Left/Right). Choice prompt can be set via choicePromptText. </summary>
    public void ShowChoicePanel()
    {
        if (choicePanel != null) choicePanel.SetActive(true);
        // ensure start flag is cleared (irrelevant here, but safe)
        startClicked = false;
    }

    public void HideChoicePanel()
    {
        if (choicePanel != null) choicePanel.SetActive(false);
    }

    /// <summary> Show rest panel with default message (edit restText in inspector if needed) </summary>
    public void ShowRestPanel()
    {
        HideAllPanels();
        if (restPanel != null) restPanel.SetActive(true);
        // reset startClicked so WaitForStartButton will wait for the new click
        startClicked = false;
    }

    public void HideRestPanel()
    {
        if (restPanel != null) restPanel.SetActive(false);
    }

    public void ShowEndPanel()
    {
        HideAllPanels();
        if (endPanel != null) endPanel.SetActive(true);
    }

    #endregion

    #region Button handlers (callbacks)

    private void OnIntroStartClicked()
    {
        // mark clicked for any WaitForStartButton coroutine
        startClicked = true;

        // also hide the intro panel immediately
        if (introPanel != null) introPanel.SetActive(false);

        // If GameManager provided, trigger StartExperiment (defensive check)
        if (gameManager != null)
        {
            // StartExperiment typically spawns trials etc.
            // We call the public StartExperiment so the UI controls experiment start.
            gameManager.StartExperiment();
        }
    }

    private void OnRestStartClicked()
    {
        startClicked = true;
        // hide rest panel and let GameManager continue when waiting coroutine resumes
        if (restPanel != null) restPanel.SetActive(false);
    }

    private void OnLeftClicked()
    {
        // route participant choice to GameManager
        if (gameManager != null)
        {
            gameManager.SubmitParticipantChoice("Left");
        }
        // hide choice UI (GameManager will also see participantChoiceReceived flag)
        HideChoicePanel();
    }

    private void OnRightClicked()
    {
        if (gameManager != null)
        {
            gameManager.SubmitParticipantChoice("Right");
        }
        HideChoicePanel();
    }

    #endregion

    #region Coroutine helpers

    /// <summary>
    /// Coroutine that yields until the Start button (intro or rest) is clicked.
    /// Use pattern: yield return StartCoroutine(uiManager.WaitForStartButton());
    /// This method also shows the introPanel if showIntro==true.
    /// </summary>
    public IEnumerator WaitForStartButton(bool showIntro = false)
    {
        if (showIntro)
            ShowIntroPanel();

        startClicked = false;
        // Wait until either clicked or the panel was removed externally (defensive)
        while (!startClicked)
        {
            yield return null;
        }
        // small frame wait to allow any UI state changes to settle
        yield return null;
    }

    #endregion
}