using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Team11.Ros
{
    /// <summary>
    /// Простой калибровочный режим для реального робота: едет прямо
    /// driveDurationSeconds, разворачивается на 180°, едет обратно столько же.
    /// Не связан с RL-политикой/RobotBrain и с этапом эстафеты — только для
    /// проверки базового вождения/поворота на реальном железе. Команды шлёт
    /// через тот же IRobotCommandSink (RobotRosTeleop), что и обученная
    /// политика, поэтому E-stop (Escape) и остановка при потере фокуса
    /// работают как обычно, без дублирования этой логики здесь.
    /// </summary>
    public sealed class CalibrationDriveTest : MonoBehaviour
    {
        [Tooltip("Сколько секунд ехать прямо перед разворотом (столько же — обратно)")]
        [Min(0.1f)] public float driveDurationSeconds = 3f;

        [Tooltip("Скорость поворота, град/сек — подставьте фактическую скорость реального робота " +
            "(RobotRosTeleop.AngularSpeedRadiansPerSecond по умолчанию 0.8 рад/с ≈ 45.8°/с), " +
            "иначе разворот будет не ровно 180°")]
        [Min(1f)] public float turnSpeedDegreesPerSecond = 45.8f;

        [Tooltip("Клавиша запуска теста")]
        public Key startKey = Key.T;

        private RobotRosTeleop teleop;
        private RobotBrain robotBrain;
        private bool isRunning;

        // Тот же паттерн автоустановки, что RealVision.Install() и
        // RoverActivationListener.Install() — не подключаемся во время
        // обучения и не дублируем компонент, если он уже есть в сцене.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<CalibrationDriveTest>() != null)
                return;

            if (FindAnyObjectByType<SensorTestSceneSetup>() != null)
                return;

            RobotBrain brain = FindAnyObjectByType<RobotBrain>();
            if (brain != null && brain.isTraining)
                return;

            var instance = new GameObject("Calibration Drive Test");
            DontDestroyOnLoad(instance);
            instance.AddComponent<CalibrationDriveTest>();
        }

        private void Update()
        {
            if (isRunning) return;

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[startKey].wasPressedThisFrame)
                StartCoroutine(RunCalibration());
        }

        /// <summary>
        /// Запуск не по клавише, а программно — например из
        /// RoverActivationListener при получении JSON-активации от ровера.
        /// Игнорируется, если алгоритм уже выполняется.
        /// </summary>
        public void TriggerExternally()
        {
            if (isRunning) return;
            StartCoroutine(RunCalibration());
        }

        private IEnumerator RunCalibration()
        {
            isRunning = true;

            if (robotBrain == null) robotBrain = FindAnyObjectByType<RobotBrain>();
            if (teleop == null) teleop = FindAnyObjectByType<RobotRosTeleop>();

            if (teleop == null || robotBrain == null)
            {
                Debug.LogWarning("[Calibration] Нужен RobotRosTeleop и RobotBrain в сцене — иначе команды некому слать.");
                isRunning = false;
                yield break;
            }

            // Запуск калибровки (по клавише или по реальной JSON-активации от
            // ровера) сам по себе означает, что мы работаем с реальным
            // роботом — включаем real I/O автоматически, без отдельного
            // ручного тумблера перед стартом.
            if (!robotBrain.UseRealRobotIo)
            {
                Debug.Log("[Calibration] \"Use real robot I/O\" был выключен — включаю автоматически.");
                robotBrain.UseRealRobotIo = true;
            }

            Debug.Log($"[Calibration] Едем прямо {driveDurationSeconds:F1} с...");
            yield return DriveFor(linear: 1f, angular: 0f, seconds: driveDurationSeconds);

            float turnSeconds = 180f / turnSpeedDegreesPerSecond;
            Debug.Log($"[Calibration] Разворот на 180° ({turnSeconds:F2} с)...");
            yield return DriveFor(linear: 0f, angular: 1f, seconds: turnSeconds);

            Debug.Log($"[Calibration] Едем обратно {driveDurationSeconds:F1} с...");
            yield return DriveFor(linear: 1f, angular: 0f, seconds: driveDurationSeconds);

            teleop.Stop();
            Debug.Log("[Calibration] Готово.");
            isRunning = false;
        }

        private IEnumerator DriveFor(float linear, float angular, float seconds)
        {
            var command = new RobotCommand(linear, angular, cameraPanTarget: 0f, RobotGripperCommand.None);
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                teleop.ApplyCommand(command);
                elapsed += Time.deltaTime;
                yield return null;
            }
        }
    }
}
