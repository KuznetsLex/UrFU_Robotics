using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Team11.Ros
{
    // ФОРМАТ ПРИДУМАН С НУЛЯ — на момент написания готового канала/формата
    // от Яндекс Ровера или Qwen не было (см. регламент: Этап 1 Фаза 2,
    // "Фото мячика → Qwen → JSON-команды → Unity → запуск гусеничного
    // ровера"). При интеграции с реальным пайплайном сверьте это поле и
    // порт с фактическим выводом скрипта на стороне ровера/Qwen и поправьте.
    [Serializable]
    public sealed class RoverActivationPacket
    {
        public bool activate;
    }

    /// <summary>
    /// Слушает UDP JSON-активацию от Яндекс Ровера (порт 5007 по умолчанию,
    /// свободен: 5005 занят RealVision/мяч, 5006 — RealVisionCube/кубик) и по
    /// получении однократно запускает CalibrationDriveTest — временная
    /// заглушка вместо полноценного управления гусеничным ровером на этой
    /// ветке. Структурно и по потокам — тот же паттерн, что RealVision.cs
    /// (фоновый поток + ConcurrentQueue, разбор JSON в Update() на главном).
    /// </summary>
    [DefaultExecutionOrder(-750)]
    public sealed class RoverActivationListener : MonoBehaviour
    {
        [Tooltip("UDP-порт для приёма JSON-активации от ровера/Qwen")]
        [SerializeField] private int udpPort = 5007;

        [Tooltip("Алгоритм, который запускается по активации. Если не назначен — ищется в сцене автоматически.")]
        [SerializeField] private CalibrationDriveTest driveAlgorithm;

        [Tooltip("Клавиша для ручной проверки без реального UDP-пакета — эмулирует получение {\"activate\": true} через ту же логику (включая защиту \"одна попытка\")")]
        public Key testKey = Key.Y;

        private readonly ConcurrentQueue<string> incomingJson = new ConcurrentQueue<string>();
        private Thread receiverThread;
        private UdpClient udpClient;
        private volatile bool receiverRunning;
        private bool hasTriggered;
        private string receiverStatus = "Waiting for rover activation";

        // AfterSceneLoad — тот же паттерн, что RealVision.Install(): не
        // подключаемся во время обучения (RobotBrain.isTraining) и не
        // дублируем компонент, если он уже есть в сцене вручную.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<RoverActivationListener>() != null)
                return;

            if (FindAnyObjectByType<SensorTestSceneSetup>() != null)
                return;

            RobotBrain brain = FindAnyObjectByType<RobotBrain>();
            if (brain != null && brain.isTraining)
                return;

            var instance = new GameObject("Rover Activation Listener");
            DontDestroyOnLoad(instance);
            instance.AddComponent<RoverActivationListener>();
        }

        private void OnEnable()
        {
            receiverRunning = true;
            receiverThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "Rover activation UDP receiver"
            };
            receiverThread.Start();
        }

        private void Update()
        {
            while (incomingJson.TryDequeue(out string json))
            {
                ProcessPacket(json);
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[testKey].wasPressedThisFrame)
            {
                Debug.Log("[RoverActivationListener] Тестовая клавиша нажата — эмулирую JSON-активацию.");
                ProcessPacket("{\"activate\": true}");
            }
        }

        private void ProcessPacket(string json)
        {
            // Цепочка активации — одна попытка за заезд (см. регламент, пункт
            // про Этап 1: "цепочка сломалась — перезапустить нельзя"),
            // поэтому игнорируем всё после первой успешной активации.
            if (hasTriggered)
                return;

            RoverActivationPacket packet;
            try
            {
                packet = JsonUtility.FromJson<RoverActivationPacket>(json);
            }
            catch (ArgumentException error)
            {
                receiverStatus = $"Invalid JSON: {error.Message}";
                Debug.LogWarning($"[RoverActivationListener] {receiverStatus}");
                return;
            }

            if (packet == null || !packet.activate)
                return;

            hasTriggered = true;
            receiverStatus = "Activated";
            Debug.Log("[RoverActivationListener] Получена активация от ровера — запускаю CalibrationDriveTest.");

            if (driveAlgorithm == null)
                driveAlgorithm = FindAnyObjectByType<CalibrationDriveTest>();

            if (driveAlgorithm == null)
            {
                Debug.LogWarning("[RoverActivationListener] CalibrationDriveTest не найден в сцене — активация получена, но выполнять нечего.");
                return;
            }

            driveAlgorithm.TriggerExternally();
        }

        private void ReceiveLoop()
        {
            try
            {
                udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, udpPort));
                udpClient.Client.ReceiveTimeout = 250;
                receiverStatus = $"Listening for rover activation on UDP {udpPort}";

                while (receiverRunning)
                {
                    try
                    {
                        IPEndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                        byte[] bytes = udpClient.Receive(ref remoteEndpoint);
                        incomingJson.Enqueue(Encoding.UTF8.GetString(bytes));
                    }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut)
                    {
                        // Периодически просыпаемся, чтобы проверить receiverRunning.
                    }
                }
            }
            catch (SocketException error)
            {
                if (receiverRunning)
                {
                    receiverStatus = $"UDP error: {error.Message}";
                    Debug.LogError($"[RoverActivationListener] {receiverStatus}");
                }
            }
            catch (ObjectDisposedException)
            {
                // Ожидаемо при закрытии сокета в OnDisable().
            }
            finally
            {
                udpClient?.Close();
                udpClient = null;
            }
        }

        private void OnDisable()
        {
            receiverRunning = false;
            udpClient?.Close();

            if (receiverThread != null && receiverThread.IsAlive)
            {
                receiverThread.Join(500);
            }

            receiverThread = null;
        }
    }
}
