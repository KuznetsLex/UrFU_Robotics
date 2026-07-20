using UnityEngine;
using UnityEngine.InputSystem;

// Обзорная камера для мульти-арена сцены: колесо мыши зумит (ортографический
// размер), [ и ] (или PageUp/PageDown, если есть) переключают фокус между
// аренами по очереди, Backspace или Home возвращает к общему виду всей сетки.
// Без этого при arenaCount=20 каждая отдельная арена — крошечная точка на
// экране, и разглядеть, что реально происходит в конкретной клетке, нельзя.
// Отдельный (не навешанный на саму камеру) объект — чтобы не трогать
// PrefabInstance основной камеры в сцене, скрипт сам находит Camera.main.
public class TrainingCameraController : MonoBehaviour
{
    [Header("Целевая камера")]
    [Tooltip("Если не назначена — используется Camera.main")]
    public Camera targetCamera;

    [Header("Зум (колесо мыши)")]
    public float zoomSpeed = 40f;
    public float minOrthographicSize = 3f;
    public float maxOrthographicSize = 400f;

    [Header("Переключение между аренами")]
    [Tooltip("Если не назначен — ищется автоматически через FindAnyObjectByType")]
    public ArenaSpawner arenaSpawner;
    [Tooltip("Ортографический размер камеры при приближении к конкретной арене")]
    public float focusedOrthographicSize = 20f;

    private Camera cam;
    private Vector3 overviewPosition;
    private float overviewOrthographicSize;
    private int focusedArenaIndex = -1; // -1 = общий обзор, ничего не выбрано

    private void Awake()
    {
        cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("TrainingCameraController: камера не найдена (назначьте Target Camera или пометьте камеру тегом MainCamera).");
            enabled = false;
            return;
        }

        overviewPosition = cam.transform.position;
        overviewOrthographicSize = cam.orthographicSize;

        if (arenaSpawner == null)
            arenaSpawner = FindAnyObjectByType<ArenaSpawner>();
    }

    private void Update()
    {
        HandleZoom();
        HandleArenaSwitching();
    }

    private void HandleZoom()
    {
        if (Mouse.current == null)
            return;

        float scroll = Mouse.current.scroll.ReadValue().y;
        if (Mathf.Approximately(scroll, 0f))
            return;

        cam.orthographicSize = Mathf.Clamp(
            cam.orthographicSize - scroll * zoomSpeed * 0.01f,
            minOrthographicSize,
            maxOrthographicSize);
    }

    private void HandleArenaSwitching()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || arenaSpawner == null || arenaSpawner.ArenaEnvironments.Count == 0)
            return;

        if (keyboard.pageDownKey.wasPressedThisFrame || keyboard.rightBracketKey.wasPressedThisFrame)
            FocusArena(WrapIndex(focusedArenaIndex + 1, arenaSpawner.ArenaEnvironments.Count));
        else if (keyboard.pageUpKey.wasPressedThisFrame || keyboard.leftBracketKey.wasPressedThisFrame)
            FocusArena(WrapIndex(focusedArenaIndex - 1, arenaSpawner.ArenaEnvironments.Count));
        else if (keyboard.homeKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame)
            ShowOverview();
    }

    private static int WrapIndex(int index, int count) => ((index % count) + count) % count;

    private void FocusArena(int index)
    {
        EnvironmentManager env = arenaSpawner.ArenaEnvironments[index];
        if (env == null)
            return;

        focusedArenaIndex = index;
        Vector3 target = env.transform.position;
        Vector3 camPos = cam.transform.position;
        cam.transform.position = new Vector3(target.x, camPos.y, target.z);
        cam.orthographicSize = focusedOrthographicSize;
    }

    private void ShowOverview()
    {
        focusedArenaIndex = -1;
        cam.transform.position = overviewPosition;
        cam.orthographicSize = overviewOrthographicSize;
    }
}
