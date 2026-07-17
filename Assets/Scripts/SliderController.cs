using UnityEngine;
using UnityEngine.UI;

// Ручное управление через UI-ползунки для тестирования ШИМ-логики
// (в отличие от WASD, ползунки дают непрерывные значения gas/steer,
// поэтому деадзона и минимальный порог в TrackController видны при плавном перетаскивании)
public class SliderController : MonoBehaviour
{
    [Header("Связь с моторами")]
    public TrackController trackController;

    [Header("UI (Slider.minValue = -1, Slider.maxValue = 1)")]
    public Slider gasSlider;
    public Slider steerSlider;

    void Update()
    {
        if (trackController == null || gasSlider == null || steerSlider == null)
            return;

        trackController.Move(gasSlider.value, steerSlider.value);
    }
}
