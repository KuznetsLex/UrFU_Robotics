using UnityEngine;
using UnityEngine.InputSystem; // Обязательно подключаем библиотеку нового API

public class ManualController : MonoBehaviour
{
    [Header("Связь с моторами")]
    public TrackController trackController;

    void Update()
    {
        // Проверяем, что контроллер подключен
        if (trackController == null)
        {
            Debug.LogWarning("TrackController не привязан к ManualController!");
            return;
        }

        float gas = 0f;
        float steer = 0f;

        // Безопасная проверка: подключена ли вообще клавиатура к компьютеру
        if (Keyboard.current != null)
        {
            // Обработка газа (вперед/назад)
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed)
                gas = 1f;
            else if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed)
                gas = -1f;

            // Обработка руля (вправо/влево)
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed)
                steer = 1f;
            else if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed)
                steer = -1f;
        }

        // Передаем сигналы в драйвер гусениц
        trackController.Move(gas, steer);
    }
}
