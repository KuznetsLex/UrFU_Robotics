using UnityEngine;

// Синхронное открытие/закрытие клешни вращением двух пальцев (Circle.003, Circle.004)
// в противоположные стороны — каждый вокруг СВОЕГО внешнего пивота (Rotator.003 / Rotator.004),
// а не вокруг собственного центра. Без физики, прямое управление transform по параметру.
public class ClawController : MonoBehaviour
{
    [Header("Пальцы клешни")]
    public Transform fingerLeft;   // Circle.003
    public Transform fingerRight;  // Circle.004

    [Header("Пивоты вращения (точки, вокруг которых крутятся пальцы)")]
    public Transform pivotLeft;    // Rotator.003
    public Transform pivotRight;   // Rotator.004

    [Header("Оси вращения (локальные координаты пивота, подберите визуально в инспекторе)")]
    public Vector3 rotationAxisLeft = Vector3.up;
    public Vector3 rotationAxisRight = Vector3.up;

    [Header("Углы раскрытия, градусы")]
    public float closedAngle = -20f;
    public float openAngle = 30f;

    [Header("Состояние")]
    [Range(0f, 1f)]
    public float openness = 0f; // 0 = закрыта, 1 = раскрыта полностью

    // Поза пальца, записанная в локальном пространстве его пивота (в закрытом состоянии)
    private Vector3 leftLocalOffset;
    private Quaternion leftLocalRotation;
    private Vector3 rightLocalOffset;
    private Quaternion rightLocalRotation;

    void Start()
    {
        // Запоминаем исходную (закрытую) позу пальца относительно пивота,
        // чтобы вращение накладывалось поверх неё, а не заменяло её.
        if (fingerLeft != null && pivotLeft != null)
        {
            leftLocalOffset = pivotLeft.InverseTransformPoint(fingerLeft.position);
            leftLocalRotation = Quaternion.Inverse(pivotLeft.rotation) * fingerLeft.rotation;
        }

        if (fingerRight != null && pivotRight != null)
        {
            rightLocalOffset = pivotRight.InverseTransformPoint(fingerRight.position);
            rightLocalRotation = Quaternion.Inverse(pivotRight.rotation) * fingerRight.rotation;
        }
    }

    void Update()
    {
        float angle = Mathf.Lerp(closedAngle, openAngle, openness);

        // Пальцы вращаются в противоположные стороны — за счёт этого
        // клешня раздвигается/сдвигается симметрично.
        ApplyRotation(fingerLeft, pivotLeft, rotationAxisLeft, angle, leftLocalOffset, leftLocalRotation);
        ApplyRotation(fingerRight, pivotRight, rotationAxisRight, -angle, rightLocalOffset, rightLocalRotation);
    }

    private void ApplyRotation(Transform finger, Transform pivot, Vector3 axis, float angle,
        Vector3 localOffset, Quaternion localRotation)
    {
        if (finger == null || pivot == null) return;

        Quaternion delta = Quaternion.AngleAxis(angle, axis);

        // Позиция пальца вращается вокруг пивота в его локальном пространстве
        // (корректно следует за пивотом, даже если тот сам двигается вместе с рукой).
        finger.position = pivot.TransformPoint(delta * localOffset);

        // Собственная ориентация пальца поворачивается на ту же дельту.
        finger.rotation = pivot.rotation * delta * localRotation;
    }

    // Публичный метод для вызова извне (ManualController, SliderController, ИИ-агент)
    public void SetOpenness(float value)
    {
        openness = Mathf.Clamp01(value);
    }
}
