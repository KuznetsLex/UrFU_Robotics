using UnityEngine;

public class CameraRotator : MonoBehaviour
{
    [Range(-90f, 90f)]
    public float angle = 0f; // ползунок в инспекторе

    private float previousAngle = -1f;

    void Update()
    {
        if (Mathf.Abs(angle - previousAngle) > 0.001f)
        {
            ApplyRotation();
            previousAngle = angle;
        }
    }

    // Применяет изменения даже в редакторе (без Play)
    void OnValidate()
    {
        ApplyRotation();
        previousAngle = angle;
    }

    private void ApplyRotation()
    {
        // Вращаем вокруг локальной оси Z (влево-вправо, если модель смотрит вперёд по Z)
        transform.localRotation = Quaternion.Euler(0f, angle, 0f);
    }
}