using UnityEngine;

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
    private bool isOpen = true;            // флаг состояния клешни: true – открыта, false – закрыта
    private Transform grabbedBallOriginalParent; // родитель мяча до захвата (чтобы вернуть при отпускании)

    // Ссылка на мяч, который находится в триггере (если используем триггер)
    private GameObject ballInTrigger;

    // OnTriggerEnter/Exit могут дребезжать на каждом физическом шаге (мяч на
    // границе триггера), а Grab()/Release() агент дёргает часто в процессе
    // исследования — Debug.Log на каждое такое событие ощутимо грузит
    // диск/консоль при обучении на многих аренах, поэтому логируем только
    // вне обучения.
    private RobotBrain robotBrain;

    void Start()
    {
        robotBrain = GetComponentInParent<RobotBrain>();

        // Если holdPoint не назначен вручную, пытаемся найти дочерний объект с именем "HoldPoint"
        if (holdPoint == null)
        {
            holdPoint = transform.Find("HoldPoint");
            if (holdPoint == null)
                Debug.LogWarning("HoldPoint не найден! Назначьте его в инспекторе.");
        }
    }

    private bool IsTraining => robotBrain != null && robotBrain.isTraining;

    // ========== МЕТОДЫ ДЛЯ ВЫЗОВА ИЗВНЕ (например, из скрипта управления) ==========

    /// <summary>
    /// Вызовите этот метод, чтобы захватить мяч (если он находится в зоне триггера и клешня открыта).
    /// Если мяча нет – клешня просто закрывается (без захвата).
    /// </summary>
    /// <returns>true – мяч захвачен, false – мяч не найден (клешня закрыта без мяча)</returns>
    public bool Grab()
    {
        if (isGrabbing) return false; // уже держим мяч – повторный захват невозможен
        if (!isOpen) return false;    // клешня закрыта – захват запрещён

        // Пытаемся найти мяч в триггере или через физику
        GameObject ballToGrab = null;
        if (ballInTrigger != null)
        {
            ballToGrab = ballInTrigger;
        }
        else
        {
            Collider[] hitColliders = Physics.OverlapSphere(holdPoint.position, grabRadius);
            foreach (var col in hitColliders)
            {
                if (col.CompareTag(targetTag))
                {
                    ballToGrab = col.gameObject;
                    break;
                }
            }
        }

        if (ballToGrab != null)
        {
            // Мяч найден – захватываем его и закрываем клешню
            PerformGrab(ballToGrab);
            return true;
        }
        else
        {
            // Мяч не найден – просто закрываем клешню без захвата
            CloseWithoutGrab();
            return false;
        }
    }

    /// <summary>
    /// Вызовите этот метод, чтобы отпустить захваченный мяч (если клешня закрыта).
    /// Если клешня уже открыта – ничего не делает.
    /// </summary>
    public void Release()
    {
        if (isOpen) return; // уже открыта – ничего не делаем

        // Если держим мяч – освобождаем его
        if (isGrabbing && grabbedBall != null)
        {
            // Возвращаем мячу физику
            Rigidbody rb = grabbedBall.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
            }

            // Включаем коллайдер мяча
            Collider col = grabbedBall.GetComponent<Collider>();
            if (col != null)
            {
                col.enabled = true;
            }

            // Возвращаем родителя, который был до захвата (арену мяча), а не корень
            // сцены — иначе при отпускании посреди эпизода (например, агент решил
            // перехватить заново) мяч навсегда "выпадал" из иерархии своей арены,
            // хотя физически оставался на месте (SetParent сохраняет мировые координаты).
            grabbedBall.transform.SetParent(grabbedBallOriginalParent);
            grabbedBall = null;
            isGrabbing = false;
        }

        // Открываем клешню
        isOpen = true;
        if (!IsTraining) Debug.Log("Клешня открыта");
    }

    // ========== ВНУТРЕННЯЯ ЛОГИКА ==========

    private void PerformGrab(GameObject ball)
    {
        // Отключаем физику мяча
        Rigidbody rb = ball.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
        }

        // Отключаем коллайдер
        Collider col = ball.GetComponent<Collider>();
        if (col != null)
        {
            col.enabled = false;
        }

        // Запоминаем родителя мяча до захвата, чтобы вернуть его туда при отпускании
        grabbedBallOriginalParent = ball.transform.parent;

        // Делаем мяч дочерним объектом HoldPoint
        ball.transform.SetParent(holdPoint);
        ball.transform.localPosition = Vector3.zero;
        ball.transform.localRotation = Quaternion.identity;

        // Сохраняем ссылку и обновляем состояние
        grabbedBall = ball;
        isGrabbing = true;
        isOpen = false; // клешня закрыта

        // Очищаем триггерную ссылку
        ballInTrigger = null;

        if (!IsTraining) Debug.Log("Мяч захвачен!");
    }

    private void CloseWithoutGrab()
    {
        isOpen = false;
        // isGrabbing остаётся false
        if (!IsTraining) Debug.Log("Клешня закрыта без мяча");
    }

    // ========== ОБРАБОТКА ТРИГГЕРА ==========

    private void OnTriggerEnter(Collider other)
    {
        if (isGrabbing) return;
        if (other.CompareTag(targetTag))
        {
            ballInTrigger = other.gameObject;
            if (!IsTraining) Debug.Log("Мяч вошёл в зону захвата");
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag(targetTag))
        {
            if (ballInTrigger == other.gameObject && !isGrabbing)
            {
                ballInTrigger = null;
                if (!IsTraining) Debug.Log("Мяч покинул зону захвата");
            }
        }
    }

    // ========== ОТЛАДКА ==========
    private void OnDrawGizmosSelected()
    {
        if (holdPoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(holdPoint.position, grabRadius);
        }
    }

    // ========== СВОЙСТВА ДЛЯ ВНЕШНЕГО ДОСТУПА ==========
    public bool IsGrabbing => isGrabbing;
    public bool IsOpen => isOpen;
}
