using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Team11.Ros
{
    [Serializable]
    public sealed class YoloDataPacket
    {
        public float angle;
        public float bbox_area_ratio;
        public float bbox_aspect_ratio;
        public float sees;
        public float conf;
        public float w;
        public float h;
        public float x1;
        public float y1;
        public float x2;
        public float y2;
        public float frame_w;
        public float frame_h;
    }

    /// <summary>
    /// Receives YOLO detections from yolo_vision_node.py without blocking Unity.
    /// Inherits the simulated camera contract so it can be assigned directly to RobotBrain.yoloCamera.
    /// </summary>
    [DefaultExecutionOrder(-750)]
    public sealed class RealVision : SimulatedYoloCamera
    {
        [Header("YOLO UDP")]
        [SerializeField] private int udpPort = 5005;
        [SerializeField, Min(0.05f)] private float packetTimeoutSeconds = 0.75f;

        [Header("Live telemetry")]
        [SerializeField] private bool useYOLO;
        [SerializeField] private bool seesBall;
        [SerializeField, Range(-1f, 1f)] private float normalizedAngle;
        [SerializeField, Range(0f, 1f)] private float bboxAreaRatio;
        [SerializeField] private float bboxAspectRatio;
        [SerializeField, Range(0f, 1f)] private float confidence;
        [SerializeField] private string receiverStatus = "Waiting for YOLO";

        private readonly ConcurrentQueue<string> incomingJson = new ConcurrentQueue<string>();
        private Thread receiverThread;
        private UdpClient udpClient;
        private volatile bool receiverRunning;
        private YoloDataPacket latestPacket;
        private float lastPacketTime = -1f;

        public bool HasFreshPacket =>
            lastPacketTime >= 0f && Time.unscaledTime - lastPacketTime <= packetTimeoutSeconds;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<RealVision>() != null)
            {
                return;
            }

            var instance = new GameObject("Real YOLO Vision");
            DontDestroyOnLoad(instance);
            instance.AddComponent<RealVision>();
        }

        private void OnEnable()
        {
            receiverRunning = true;
            receiverThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "YOLO UDP receiver"
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
                    seesBall = false;
                    normalizedAngle = 0f;
                    bboxAreaRatio = 0f;
                    bboxAspectRatio = 0f;
                    receiverStatus = "YOLO packets timed out";
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
            useYOLO = true;
            seesBall = packet.sees > 0.5f;
            confidence = Mathf.Clamp01(packet.conf);
            normalizedAngle = seesBall ? Mathf.Clamp(packet.angle, -1f, 1f) : 0f;

            bboxAreaRatio = seesBall ? Mathf.Clamp01(packet.bbox_area_ratio) : 0f;
            bboxAspectRatio = seesBall ? Mathf.Max(0f, packet.bbox_aspect_ratio) : 0f;
            receiverStatus = seesBall
                ? $"BALL  conf {confidence:F2}"
                : "YOLO live - no ball";
        }

        public override (float angle, float areaRatio, float aspectRatio, bool visible) GetTargetInfo()
        {
            if (!HasFreshPacket || !seesBall)
            {
                return (0f, 0f, 0f, false);
            }

            return (normalizedAngle, bboxAreaRatio, bboxAspectRatio, true);
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
