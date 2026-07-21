using UnityEngine;

public class ManipulatorGroundGuard : MonoBehaviour
{
    [Header("Суставы манипулятора")]
    public ArmJointController shoulderJoint;  // первый сустав (плечо)
    public ArmJointController elbowJoint;     // второй сустав (локоть)

    [Header("Концевой эффектор (должен быть дочерним элементом)")]
    public Transform endEffector;             // объект на конце манипулятора

    [Header("Настройки")]
    public float groundHeight = 0f;           // высота пола (по Y)
    public float correctionSpeed = 30f;       // скорость подъёма (градусов/с)
    public float minHeightBuffer = 0.02f;     // небольшой запас, чтобы не дрожало

    private void Update()
    {
        if (endEffector == null || shoulderJoint == null) return;

        // Проверяем позицию кончика
        if (endEffector.position.y < groundHeight + minHeightBuffer)
        {
            // Поднимаем плечо (увеличиваем угол)
            float newAngle = shoulderJoint.angle + correctionSpeed * Time.deltaTime;
            shoulderJoint.SetAngle(newAngle);
        }
    }
}