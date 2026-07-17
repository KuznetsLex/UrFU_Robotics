using System;
using System.Collections;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

namespace Team11.Ros
{
    [DefaultExecutionOrder(-800)]
    public sealed class RobotCameraView : MonoBehaviour
    {
        private const string CameraFrameUrl = "http://192.168.2.158:10002/frame.jpg";
        private const float RefreshIntervalSeconds = 0.1f;

        private Texture2D cameraTexture;
        private HttpClient httpClient;
        private Coroutine pollingCoroutine;
        private string status = "Waiting for robot camera";
        private float lastFrameTime = -1f;
        private RealVision realVision;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<RobotCameraView>() != null)
            {
                return;
            }

            ROSConnection.GetOrCreateInstance().gameObject.AddComponent<RobotCameraView>();
        }

        private void OnEnable()
        {
            realVision = FindAnyObjectByType<RealVision>();
            cameraTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
            httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            pollingCoroutine = StartCoroutine(PollCamera());
        }

        private void OnDisable()
        {
            if (pollingCoroutine != null)
            {
                StopCoroutine(pollingCoroutine);
                pollingCoroutine = null;
            }

            httpClient?.Dispose();
            httpClient = null;
        }

        private void OnDestroy()
        {
            if (cameraTexture != null)
            {
                Destroy(cameraTexture);
            }
        }

        private IEnumerator PollCamera()
        {
            while (enabled)
            {
                Task<byte[]> downloadTask = httpClient.GetByteArrayAsync(CameraFrameUrl);
                while (!downloadTask.IsCompleted)
                {
                    yield return null;
                }

                if (downloadTask.Status == TaskStatus.RanToCompletion &&
                    downloadTask.Result != null &&
                    downloadTask.Result.Length > 0 &&
                    cameraTexture.LoadImage(downloadTask.Result, false))
                {
                    lastFrameTime = Time.unscaledTime;
                    status = $"LIVE  {cameraTexture.width}x{cameraTexture.height}";
                }
                else
                {
                    status = "Camera unavailable: " +
                        (downloadTask.Exception?.GetBaseException().Message ?? "request failed");
                }

                yield return new WaitForSecondsRealtime(RefreshIntervalSeconds);
            }
        }

        private void OnGUI()
        {
            GUI.depth = -500;
            const float panelWidth = 340f;
            const float panelHeight = 312f;
            var panel = new Rect(10f, 42f, panelWidth, panelHeight);
            GUI.Box(panel, "Robot camera");

            bool frameIsFresh = lastFrameTime >= 0f && Time.unscaledTime - lastFrameTime < 2f;
            var frameRect = new Rect(panel.x + 10f, panel.y + 24f, 320f, 240f);
            if (frameIsFresh && cameraTexture != null)
            {
                GUI.DrawTexture(frameRect, cameraTexture, ScaleMode.ScaleToFit, false);
                DrawYoloOverlay(frameRect);
            }
            else
            {
                GUI.Box(frameRect, "No camera frame");
            }

            GUI.Label(
                new Rect(panel.x + 10f, panel.y + 268f, panelWidth - 20f, 20f),
                status);
            GUI.Label(
                new Rect(panel.x + 10f, panel.y + 288f, panelWidth - 20f, 20f),
                realVision != null ? realVision.GetReceiverStatus() : "YOLO receiver unavailable");
        }

        private void DrawYoloOverlay(Rect frameRect)
        {
            if (realVision == null)
            {
                realVision = FindAnyObjectByType<RealVision>();
            }

            if (realVision == null ||
                !realVision.TryGetFreshPacket(out YoloDataPacket packet) ||
                packet.sees <= 0.5f ||
                packet.frame_w <= 0f ||
                packet.frame_h <= 0f)
            {
                return;
            }

            Rect imageRect = FitRect(frameRect, packet.frame_w / packet.frame_h);
            float scaleX = imageRect.width / packet.frame_w;
            float scaleY = imageRect.height / packet.frame_h;
            var box = new Rect(
                imageRect.x + packet.x1 * scaleX,
                imageRect.y + packet.y1 * scaleY,
                Mathf.Max(1f, (packet.x2 - packet.x1) * scaleX),
                Mathf.Max(1f, (packet.y2 - packet.y1) * scaleY));

            DrawBorder(box, new Color(0.15f, 1f, 0.2f), 2f);
            GUI.Label(
                new Rect(box.x, Mathf.Max(imageRect.y, box.y - 20f), 130f, 20f),
                $"ball {packet.conf:F2}");
        }

        private static Rect FitRect(Rect container, float contentAspect)
        {
            float containerAspect = container.width / container.height;
            if (contentAspect > containerAspect)
            {
                float height = container.width / contentAspect;
                return new Rect(container.x, container.y + (container.height - height) * 0.5f, container.width, height);
            }

            float width = container.height * contentAspect;
            return new Rect(container.x + (container.width - width) * 0.5f, container.y, width, container.height);
        }

        private static void DrawBorder(Rect rect, Color color, float thickness)
        {
            Color previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.color = previousColor;
        }
    }
}
