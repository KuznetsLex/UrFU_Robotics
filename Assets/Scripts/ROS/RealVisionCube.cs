using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Team11.Ros
{
    /// <summary>
    /// Receives YOLO detections of the red start-zone cube from a second
    /// yolo_vision_node.py instance (TARGET_CLASSES = [1], см.
    /// P7_YOLO_Deployment_Guide.md — один процесс = один класс на один UDP-порт,
    /// поэтому кубик слушается отдельным портом, а не полем класса в общем
    /// потоке с мячом). Структура идентична RealVision.cs (мяч), но
    /// самостоятельна — свой сокет, свой поток, не делит состояние с RealVision.
    /// Назначьте в RobotBrain.cubeVisionCamera так же, как RealVision — в
    /// RobotBrain.yoloCamera.
    /// </summary>
    [DefaultExecutionOrder(-750)]
    public sealed class RealVisionCube : SimulatedYoloCamera
    {
        [Header("YOLO UDP (кубик — второй yolo_vision_node.py, TARGET_CLASSES=[1])")]
        [SerializeField] private int udpPort = 5006;
        [SerializeField, Min(0.05f)] private float packetTimeoutSeconds = 0.75f;

        [Header("Live telemetry")]
        [SerializeField] private bool useYOLO;
        [SerializeField] private bool seesCube;
        [SerializeField, Range(-1f, 1f)] private float normalizedAngle;
        [SerializeField, Range(0f, 1f)] private float bboxAreaRatio;
        [SerializeField] private float bboxAspectRatio;
        [SerializeField, Range(0f, 1f)] private float confidence;
        [SerializeField] private string receiverStatus = "Waiting for YOLO (cube)";

        private readonly ConcurrentQueue<string> incomingJson = new ConcurrentQueue<string>();
        private Thread receiverThread;
        private UdpClient udpClient;
        private volatile bool receiverRunning;
        private YoloDataPacket latestPacket;
        private float lastPacketTime = -1f;
        private ulong measurementVersion;

        public override ulong MeasurementVersion => measurementVersion;

        public bool HasFreshPacket =>
            lastPacketTime >= 0f && Time.unscaledTime - lastPacketTime <= packetTimeoutSeconds;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<RealVisionCube>() != null)
            {
                return;
            }

            var instance = new GameObject("Real YOLO Vision (Cube)");
            DontDestroyOnLoad(instance);
            instance.AddComponent<RealVisionCube>();
        }

        private void OnEnable()
        {
            receiverRunning = true;
            receiverThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "YOLO UDP receiver (cube)"
            };
            receiverThread.Start();
        }

        private void Update()
        {
            string newestJson = null;
            while (incomingJson.TryDequeue(out string queuedJson))
            {
                newestJson = queuedJson;
            }

            if (newestJson == null)
            {
                if (!HasFreshPacket && useYOLO)
                {
                    bool targetWasVisible = seesCube;
                    seesCube = false;
                    normalizedAngle = 0f;
                    bboxAreaRatio = 0f;
                    bboxAspectRatio = 0f;
                    receiverStatus = "YOLO packets timed out";
                    if (targetWasVisible)
                    {
                        measurementVersion++;
                    }
                }
                return;
            }

            YoloDataPacket packet;
            try
            {
                packet = JsonUtility.FromJson<YoloDataPacket>(newestJson);
            }
            catch (ArgumentException error)
            {
                receiverStatus = $"Invalid YOLO JSON: {error.Message}";
                return;
            }

            if (packet == null)
            {
                receiverStatus = "Invalid YOLO packet";
                return;
            }

            latestPacket = packet;
            lastPacketTime = Time.unscaledTime;
            measurementVersion++;
            useYOLO = true;
            seesCube = packet.sees > 0.5f;
            confidence = Mathf.Clamp01(packet.conf);
            normalizedAngle = seesCube ? Mathf.Clamp(packet.angle, -1f, 1f) : 0f;

            bboxAreaRatio = seesCube ? Mathf.Clamp01(packet.bbox_area_ratio) : 0f;
            bboxAspectRatio = seesCube ? Mathf.Max(0f, packet.bbox_aspect_ratio) : 0f;
            receiverStatus = seesCube
                ? $"CUBE  conf {confidence:F2}"
                : "YOLO live - no cube";
        }

        public override (float angle, float areaRatio, float aspectRatio, bool visible) GetTargetInfo()
        {
            if (!HasFreshPacket || !seesCube)
            {
                return (0f, 0f, 0f, false);
            }

            return (normalizedAngle, bboxAreaRatio, bboxAspectRatio, true);
        }

        public override void RandomizeDomainParameters()
        {
            // Физические параметры реальной камеры не рандомизируются во время инференса.
        }

        public bool TryGetFreshPacket(out YoloDataPacket packet)
        {
            packet = latestPacket;
            return HasFreshPacket && packet != null;
        }

        public string GetReceiverStatus()
        {
            return receiverStatus;
        }

        private void ReceiveLoop()
        {
            try
            {
                udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, udpPort));
                udpClient.Client.ReceiveTimeout = 250;
                receiverStatus = $"Listening for YOLO on UDP {udpPort}";

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
                        // Periodically wake up to observe receiverRunning.
                    }
                }
            }
            catch (SocketException error)
            {
                if (receiverRunning)
                {
                    receiverStatus = $"YOLO UDP error: {error.Message}";
                }
            }
            catch (ObjectDisposedException)
            {
                // Expected when OnDisable closes the socket.
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
