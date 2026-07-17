using System;
using System.Collections;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

namespace Team11.Ros
{
    [DefaultExecutionOrder(-800)]
    public sealed class RobotCameraView : MonoBehaviour
    {
        private const string PrimaryCameraFrameUrl = "http://192.168.2.158:10002/frame.jpg";
        private const string FallbackCameraStreamUrl = "http://192.168.2.158:8081/";
        private const float RefreshIntervalSeconds = 0.1f;
        private const float FallbackConnectTimeoutSeconds = 3f;
        private const float FallbackReadTimeoutSeconds = 3f;
        private const int MaxMjpegFrameBytes = 8 * 1024 * 1024;

        private Texture2D cameraTexture;
        private HttpClient snapshotHttpClient;
        private HttpClient streamHttpClient;
        private CancellationTokenSource streamCancellation;
        private Task streamTask;
        private byte[] pendingStreamFrame;
        private string streamError = "waiting for MJPEG stream";
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
            snapshotHttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
            snapshotHttpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            streamHttpClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            streamHttpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
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

            StopMjpegStream();
            snapshotHttpClient?.Dispose();
            snapshotHttpClient = null;
            streamHttpClient?.Dispose();
            streamHttpClient = null;
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
                Task<byte[]> downloadTask = snapshotHttpClient.GetByteArrayAsync(PrimaryCameraFrameUrl);
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
                    status = $"LIVE  {cameraTexture.width}x{cameraTexture.height}  [primary]";
                }
                else
                {
                    status = "Primary unavailable; connecting to MJPEG :8081";
                    StartMjpegStream();
                    while (enabled && streamTask != null && !streamTask.IsCompleted)
                    {
                        ApplyPendingStreamFrame();
                        yield return null;
                    }

                    ApplyPendingStreamFrame();
                    if (!enabled)
                    {
                        yield break;
                    }

                    StopMjpegStream();
                    status = $"MJPEG unavailable ({streamError}); retrying primary";
                }

                yield return new WaitForSecondsRealtime(RefreshIntervalSeconds);
            }
        }

        private void StartMjpegStream()
        {
            StopMjpegStream();
            streamError = "connecting";
            streamCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = streamCancellation.Token;
            streamTask = Task.Run(() => ReadMjpegStream(cancellationToken), cancellationToken);
        }

        private void StopMjpegStream()
        {
            streamCancellation?.Cancel();
            streamCancellation?.Dispose();
            streamCancellation = null;
            streamTask = null;
            Interlocked.Exchange(ref pendingStreamFrame, null);
        }

        private bool ApplyPendingStreamFrame()
        {
            byte[] encodedFrame = Interlocked.Exchange(ref pendingStreamFrame, null);
            if (encodedFrame == null ||
                encodedFrame.Length == 0 ||
                !cameraTexture.LoadImage(encodedFrame, false))
            {
                return false;
            }

            lastFrameTime = Time.unscaledTime;
            status = $"LIVE  {cameraTexture.width}x{cameraTexture.height}  [MJPEG :8081]";
            return true;
        }

        private async Task ReadMjpegStream(CancellationToken cancellationToken)
        {
            try
            {
                HttpResponseMessage connectedResponse;
                using (var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connectionCancellation.CancelAfter(TimeSpan.FromSeconds(FallbackConnectTimeoutSeconds));
                    connectedResponse = await streamHttpClient.GetAsync(
                            FallbackCameraStreamUrl,
                            HttpCompletionOption.ResponseHeadersRead,
                            connectionCancellation.Token)
                        .ConfigureAwait(false);
                }

                using (HttpResponseMessage response = connectedResponse)
                {
                    response.EnsureSuccessStatusCode();
                    using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var frame = new MemoryStream())
                    {
                        var readBuffer = new byte[16 * 1024];
                        byte previous = 0;
                        bool hasPrevious = false;
                        bool insideJpeg = false;

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            int bytesRead;
                            using (var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                            {
                                readCancellation.CancelAfter(TimeSpan.FromSeconds(FallbackReadTimeoutSeconds));
                                try
                                {
                                    bytesRead = await source.ReadAsync(
                                            readBuffer,
                                            0,
                                            readBuffer.Length,
                                            readCancellation.Token)
                                        .ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                                {
                                    throw new TimeoutException("camera stopped sending MJPEG data");
                                }
                            }
                            if (bytesRead == 0)
                            {
                                throw new EndOfStreamException("camera closed the MJPEG stream");
                            }

                            for (int index = 0; index < bytesRead; index++)
                            {
                                byte current = readBuffer[index];
                                if (!insideJpeg)
                                {
                                    if (hasPrevious && previous == 0xFF && current == 0xD8)
                                    {
                                        frame.SetLength(0);
                                        frame.WriteByte(0xFF);
                                        frame.WriteByte(0xD8);
                                        insideJpeg = true;
                                    }
                                }
                                else
                                {
                                    frame.WriteByte(current);
                                    if (previous == 0xFF && current == 0xD9)
                                    {
                                        Interlocked.Exchange(ref pendingStreamFrame, frame.ToArray());
                                        frame.SetLength(0);
                                        insideJpeg = false;
                                    }
                                    else if (frame.Length > MaxMjpegFrameBytes)
                                    {
                                        frame.SetLength(0);
                                        insideJpeg = false;
                                    }
                                }

                                previous = current;
                                hasPrevious = true;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                streamError = cancellationToken.IsCancellationRequested
                    ? "stopped"
                    : "connection timed out";
            }
            catch (Exception error)
            {
                streamError = error.Message;
            }
        }

        private void OnGUI()
        {
            GUI.depth = -500;
            const float panelWidth = 340f;
            const float panelHeight = 352f;
            var panel = new Rect(10f, 42f, panelWidth, panelHeight);
            GUI.Box(panel, "Robot camera");

            bool hasFrame = lastFrameTime >= 0f;
            bool frameIsFresh = hasFrame && Time.unscaledTime - lastFrameTime < 2f;
            var frameRect = new Rect(panel.x + 10f, panel.y + 24f, 320f, 240f);
            if (hasFrame && cameraTexture != null)
            {
                GUI.DrawTexture(frameRect, cameraTexture, ScaleMode.ScaleToFit, false);
                if (frameIsFresh)
                {
                    DrawYoloOverlay(frameRect);
                }
                else
                {
                    GUI.Box(new Rect(frameRect.x + 6f, frameRect.y + 6f, 148f, 22f), "STALE - reconnecting");
                }
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
            GUI.Label(
                new Rect(panel.x + 10f, panel.y + 308f, panelWidth - 20f, 20f),
                GetGeometryStatus());
            GUI.Label(
                new Rect(panel.x + 10f, panel.y + 328f, panelWidth - 20f, 20f),
                GetHorizontalStatus());
        }

        private string GetGeometryStatus()
        {
            if (realVision == null ||
                !realVision.TryGetFreshPacket(out YoloDataPacket packet) ||
                packet.sees <= 0.5f)
            {
                return "BBox area/aspect: --";
            }

            return $"BBox area: {packet.bbox_area_ratio:P1} | aspect: {packet.bbox_aspect_ratio:F2}";
        }

        private string GetHorizontalStatus()
        {
            if (realVision == null)
            {
                return "Horizontal angle (norm): --";
            }

            var targetInfo = realVision.GetTargetInfo();
            if (!targetInfo.visible)
            {
                return "Horizontal angle (norm): --";
            }

            string direction = targetInfo.angle < -0.05f
                ? "left"
                : targetInfo.angle > 0.05f
                    ? "right"
                    : "center";
            return $"Horizontal angle (norm): {targetInfo.angle:+0.00;-0.00;0.00} ({direction})";
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
