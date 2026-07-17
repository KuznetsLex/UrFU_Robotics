using UnityEngine;
using UnityEngine.InputSystem;

public class GripperController : MonoBehaviour
{
    [Header("Настройки захвата")]
    [Tooltip("Точка, к которой будет прикреплён мяч")]
    public Transform holdPoint;            // сюда перетащите HoldPoint из иерархии

    [Tooltip("Радиус проверки наличия мяча (если не используете триггер)")]
    public float grabRadius = 0.007f;        // запасной вариант – можно проверять через сферу

    [Tooltip("Тег, который должен быть у мяча")]
    public string targetTag = "TargetBall";

    // Приватные переменные
    private GameObject grabbedBall;        // ссылка на захваченный мяч
    private bool isGrabbing = false;       // флаг, что мяч уже захвачен

    // Ссылка на мяч, который находится в триггере (если используем триггер)
    private GameObject ballInTrigger;

    void Start()
    {
        // Если holdPoint не назначен вручную, пытаемся найти дочерний объект с именем "HoldPoint"
        if (holdPoint == null)
        {
            holdPoint = transform.Find("HoldPoint");
            if (holdPoint == null)
                Debug.LogWarning("HoldPoint не найден! Назначьте его в инспекторе.");
        }
    }

    // ========== МЕТОДЫ ДЛЯ ВЫЗОВА ИЗВНЕ (например, из скрипта управления) ==========

    /// <summary>
    /// Вызовите этот метод, чтобы захватить мяч (если он находится в зоне триггера)
    /// </summary>
    public void Grab()
    {
        if (isGrabbing) return; // уже держим мяч

        // Если есть мяч в триггере – захватываем его
        if (ballInTrigger != null)
        {
            PerformGrab(ballInTrigger);
        }
        else
        {
            // Альтернативно: можно проверить наличие мяча через физику (если триггер не сработал)
            Collider[] hitColliders = Physics.OverlapSphere(holdPoint.position, grabRadius);
            foreach (var col in hitColliders)
            {
                if (col.CompareTag(targetTag))
                {
                    PerformGrab(col.gameObject);
                    break;
                }
            }
        }
    }

    public bool IsTargetInGrabZone()
    {
        if (ballInTrigger != null)
            return true;

        if (holdPoint == null)
            return false;

        Collider[] hitColliders = Physics.OverlapSphere(holdPoint.position, grabRadius);
        foreach (var col in hitColliders)
        {
            if (col.CompareTag(targetTag))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Вызовите этот метод, чтобы отпустить захваченный мяч
    /// </summary>
    public void Release()
    {
        if (!isGrabbing || grabbedBall == null) return;

        // Возвращаем мячу физику
        Rigidbody rb = grabbedBall.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = false;          // включаем физику
        }

        // Включаем коллайдер мяча
        Collider col = grabbedBall.GetComponent<Collider>();
        if (col != null)
        {
            col.enabled = true;
        }

        // Открепляем от родителя (освобождаем)
        grabbedBall.transform.SetParent(null);

        // Опционально: можно добавить небольшую скорость, чтобы мяч вылетел
        // Например, передать скорость робота вперёд:
        // Rigidbody robotRb = GetComponentInParent<Rigidbody>();
        // if (robotRb != null) rb.velocity = robotRb.velocity * 0.5f;

        // Сбрасываем флаги
        isGrabbing = false;
        grabbedBall = null;
    }

    // ========== ВНУТРЕННЯЯ ЛОГИКА ==========

    private void PerformGrab(GameObject ball)
    {
        // Отключаем физику мяча
        Rigidbody rb = ball.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;           // мяч перестаёт реагировать на силы
        }

        // Отключаем коллайдер, чтобы мяч не сталкивался с другими объектами
        Collider col = ball.GetComponent<Collider>();
        if (col != null)
        {
            col.enabled = false;
        }

        // Делаем мяч дочерним объектом HoldPoint (он будет двигаться вместе с клешней)
        ball.transform.SetParent(holdPoint);
        ball.transform.localPosition = Vector3.zero;   // центрируем в точке удержания
        ball.transform.localRotation = Quaternion.identity;

        // Сохраняем ссылку
        grabbedBall = ball;
        isGrabbing = true;

        // Очищаем триггерную ссылку, чтобы не захватить тот же мяч повторно
        ballInTrigger = null;

        Debug.Log("Мяч захвачен!");
    }

    // ========== ОБРАБОТКА ТРИГГЕРА ==========

    // Когда мяч входит в зону триггера
    private void OnTriggerEnter(Collider other)
    {
        if (isGrabbing) return; // если уже держим мяч, игнорируем новые
        if (other.CompareTag(targetTag))
        {
            ballInTrigger = other.gameObject;
            Debug.Log("Мяч вошёл в зону захвата");
        }
    }

    // Когда мяч выходит из зоны триггера
    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag(targetTag))
        {
            // Если этот мяч не захвачен, очищаем ссылку
            if (ballInTrigger == other.gameObject && !isGrabbing)
            {
                ballInTrigger = null;
                Debug.Log("Мяч покинул зону захвата");
            }
        }
    }

    // ========== ОТЛАДКА (для визуализации радиуса) ==========
    private void OnDrawGizmosSelected()
    {
        if (holdPoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(holdPoint.position, grabRadius);
        }
    }

    // testing
    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard.gKey.wasPressedThisFrame) Grab();
        if (keyboard.rKey.wasPressedThisFrame) Release();
    }

    public bool IsGrabbing => isGrabbing;
}
