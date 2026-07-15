using UnityEngine;

// Управление одним суставом руки (например, Rotator.001 или Rotator.002).
// Вешается отдельным экземпляром на каждый Rotator — объект вращается
// вокруг собственной оси на заданный угол, всё, что к нему прицеплено
// в иерархии, следует автоматически.
public class ArmJointController : MonoBehaviour
{
    [Header("Ось вращения (локальные координаты, подберите визуально в инспекторе)")]
    public Vector3 rotationAxis = Vector3.right;

    [Header("Ограничения угла, градусы")]
    public float minAngle = -80f;
    public float maxAngle = 8f;

    [Header("Состояние")]
    [DynamicRange("minAngle", "maxAngle")]
    public float angle = 0f;

    private Quaternion closedRotation;

    void Start()
    {
        // Запоминаем исходную ориентацию сустава как базу,
        // чтобы вращение накладывалось поверх неё, а не заменяло её.
        closedRotation = transform.localRotation;
    }

    void Update()
    {
        transform.localRotation = closedRotation * Quaternion.AngleAxis(angle, rotationAxis);
    }

    // Публичный метод для вызова извне (ManualController, SliderController, ИИ-агент)
    public void SetAngle(float value)
    {
        angle = Mathf.Clamp(value, minAngle, maxAngle);
    }
}
