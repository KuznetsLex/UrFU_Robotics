using UnityEngine;
using UnityEngine.InputSystem;

// Обзорная камера для мульти-арена сцены: колесо мыши зумит (ортографический
// размер), средняя кнопка мыши (или I/J/K/L) свободно панорамирует вдоль пола,
// [ и ] (или PageUp/PageDown, если есть) переключают фокус между аренами по
// очереди, Backspace или Home возвращает к общему виду всей сетки. Без этого
// при arenaCount=20 каждая отдельная арена — крошечная точка на экране, и
// разглядеть, что реально происходит в конкретной клетке, нельзя.
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

    [Header("Панорамирование вдоль пола")]
    [Tooltip("Перетаскивание средней кнопкой мыши, как в самом редакторе Unity")]
    public bool dragToPan = true;
    [Tooltip("Скорость панорамирования клавишами I/J/K/L — доля текущего orthographic size в секунду")]
    public float keyboardPanSpeed = 1.5f;

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
        HandlePan();
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

    // Свободное панорамирование по X/Z (уровень пола) — в дополнение к дискретному
    // переключению между аренами, чтобы можно было встать, например, ровно между
    // двумя соседними аренами и видеть обе сразу, а не только центр одной из них.
    private void HandlePan()
    {
        Vector3 pan = Vector3.zero;

        if (dragToPan && Mouse.current != null && Mouse.current.middleButton.isPressed)
        {
            Vector2 delta = Mouse.current.delta.ReadValue();
            // Мировых юнитов на пиксель — одинаково по X и Y для ортографической
            // камеры без искажений (высота вьюпорта в мировых юнитах = 2*size).
            float worldPerPixel = 2f * cam.orthographicSize / Mathf.Max(1, Screen.height);
            pan -= (cam.transform.right * delta.x + cam.transform.up * delta.y) * worldPerPixel;
        }

        if (Keyboard.current != null)
        {
            float speed = keyboardPanSpeed * cam.orthographicSize * Time.unscaledDeltaTime;
            if (Keyboard.current.jKey.isPressed) pan -= cam.transform.right * speed;
            if (Keyboard.current.lKey.isPressed) pan += cam.transform.right * speed;
            if (Keyboard.current.iKey.isPressed) pan += cam.transform.up * speed;
            if (Keyboard.current.kKey.isPressed) pan -= cam.transform.up * speed;
        }

        if (pan != Vector3.zero)
            cam.transform.position += pan;
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
