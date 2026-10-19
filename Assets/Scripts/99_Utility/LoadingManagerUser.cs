using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Drag onto a Button (or any object) to load something through LoadingManager.
/// - On a Button: choose an action; it runs on click (autoBindButton).
/// - Anywhere else: call Execute() from a UnityEvent, trigger, timeline, or enable "Load On Start".
/// - Public methods below can also be wired manually in an OnClick list.
/// If no LoadingManager exists in the scene it falls back to a plain SceneManager load.
/// </summary>
[DisallowMultipleComponent]
public class LoadingManagerUser : MonoBehaviour
{
    public enum LoadAction
    {
        SceneByName,
        SceneByIndex,
        NextScene,
        PreviousScene,
        ReloadScene,
        UnloadSceneByName
    }

    [SerializeField] private LoadAction action = LoadAction.SceneByName;
    [SerializeField] private string sceneName;
    [SerializeField] private int sceneIndex;
    [SerializeField] private LoadSceneMode mode = LoadSceneMode.Single;
    [Tooltip("Additive only: make the loaded scene the active scene.")]
    [SerializeField] private bool setActiveScene;
    [Tooltip("Next / Previous: wrap around the build list.")]
    [SerializeField] private bool wrap = true;
    [SerializeField] private bool autoBindButton = true;
    [SerializeField] private bool loadOnStart;

    private Button _button;

    private void Awake()
    {
        if (!autoBindButton) return;
        _button = GetComponent<Button>();
        if (_button != null) _button.onClick.AddListener(Execute);
    }

    private void Start()
    {
        if (loadOnStart) Execute();
    }

    private void OnDestroy()
    {
        if (_button != null) _button.onClick.RemoveListener(Execute);
    }

    /// <summary>Runs the action chosen in the Inspector.</summary>
    public void Execute()
    {
        switch (action)
        {
            case LoadAction.SceneByName: LoadScene(sceneName); break;
            case LoadAction.SceneByIndex: LoadScene(sceneIndex); break;
            case LoadAction.NextScene: LoadNext(); break;
            case LoadAction.PreviousScene: LoadPrevious(); break;
            case LoadAction.ReloadScene: Reload(); break;
            case LoadAction.UnloadSceneByName: UnloadScene(sceneName); break;
        }
    }

    // ????????? Public methods (manual OnClick wiring) ?????????

    public void LoadScene(string name)
    {
        var lm = LoadingManager.Instance;
        if (lm != null) lm.LoadScene(name, mode, setActiveScene);
        else SceneManager.LoadScene(name, mode);
    }

    public void LoadScene(int index)
    {
        var lm = LoadingManager.Instance;
        if (lm != null) lm.LoadScene(index, mode, setActiveScene);
        else SceneManager.LoadScene(index, mode);
    }

    public void LoadSceneAdditive(string name)
    {
        var lm = LoadingManager.Instance;
        if (lm != null) lm.LoadScene(name, LoadSceneMode.Additive, setActiveScene);
        else SceneManager.LoadScene(name, LoadSceneMode.Additive);
    }

    public void LoadNext()
    {
        var lm = LoadingManager.Instance;
        if (lm != null) { lm.LoadNextScene(wrap); return; }

        int next = SceneManager.GetActiveScene().buildIndex + 1;
        if (next >= SceneManager.sceneCountInBuildSettings) { if (!wrap) return; next = 0; }
        SceneManager.LoadScene(next);
    }

    public void LoadPrevious()
    {
        var lm = LoadingManager.Instance;
        if (lm != null) { lm.LoadPreviousScene(wrap); return; }

        int prev = SceneManager.GetActiveScene().buildIndex - 1;
        if (prev < 0) { if (!wrap) return; prev = SceneManager.sceneCountInBuildSettings - 1; }
        SceneManager.LoadScene(prev);
    }

    public void Reload()
    {
        var lm = LoadingManager.Instance;
        if (lm != null) lm.ReloadCurrentScene();
        else SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    public void UnloadScene(string name)
    {
        var lm = LoadingManager.Instance;
        if (lm != null) lm.UnloadScene(name);
        else SceneManager.UnloadSceneAsync(name);
    }

    // ????????? Step reporting (for UnityEvents or quick scripts) ?????????

    /// <summary>Register more steps. Same as LoadingManager.ReportTotal.</summary>
    public void AddTotalStep(int amount) => LoadingManager.ReportTotal(amount);

    /// <summary>Report one finished step and name the asset shown in the label.</summary>
    public void AddStep(string asset) => LoadingManager.ReportStep(asset);

    /// <summary>Report one finished step with the default label ("Asset").</summary>
    public void AddStepDefault() => LoadingManager.ReportStep();
}