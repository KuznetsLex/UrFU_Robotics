using UnityEngine;

public class SimulatedYoloCamera : MonoBehaviour
{
    [Header("Настройки камеры")]
    public float maxVisibleDistance = 2f;       // максимальная дальность обнаружения
    public float horizontalFOV = 40f;           // горизонтальный угол обзора (градусы)

    [Header("Ссылки")]
    public Transform targetBall;                // объект мяча
    public LayerMask obstacleLayer;             // слой препятствий (стены)

    private Camera cam;

    void Start()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main; // если камера является основной
    }

    /// <summary>
    /// Возвращает: (угол_от_центра_нормализованный, расстояние_нормализованное, флаг_видимости)
    /// угол: -1..1 (влево-вправо), расстояние: 0..1 (близко-далеко)
    /// </summary>
    public (float angle, float distance, bool visible) GetTargetInfo()
    {
        if (targetBall == null) return (0f, 1f, false);

        // Проекция в координаты Viewport (0..1)
        Vector3 viewportPos = cam.WorldToViewportPoint(targetBall.position);
        bool isInFront = viewportPos.z > 0;

        // Проверка, что мяч в пределах обзора по горизонтали
        float halfFOV = horizontalFOV * 0.5f;
        // Переводим координату x из 0..1 в градусы отклонения от центра
        float screenX = viewportPos.x - 0.5f;          // -0.5..0.5
        float angleDeg = screenX * horizontalFOV;      // отклонение в градусах
        bool inHorizontalFOV = Mathf.Abs(angleDeg) <= halfFOV;

        // Проверка дальности
        float distance = Vector3.Distance(cam.transform.position, targetBall.position);
        bool inRange = distance <= maxVisibleDistance;

        // Проверка прямой видимости (луч от камеры до мяча)
        bool hasLineOfSight = true;
        if (inRange)
        {
            Vector3 direction = (targetBall.position - cam.transform.position).normalized;
            if (Physics.Raycast(cam.transform.position, direction, out RaycastHit hit, maxVisibleDistance, obstacleLayer))
            {
                if (!hit.collider.CompareTag("TargetBall")) // если луч упёрся не в мяч
                    hasLineOfSight = false;
            }
        }

        bool visible = isInFront && inHorizontalFOV && inRange && hasLineOfSight;

        // Нормализованные значения
        float normalizedAngle = Mathf.Clamp(angleDeg / halfFOV, -1f, 1f);
        float normalizedDistance = Mathf.Clamp01(distance / maxVisibleDistance);

        return (normalizedAngle, normalizedDistance, visible);
    }
}
