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
        private const int VirtualFrameWidth = 320;
        private const int VirtualFrameHeight = 240;

        private Texture2D cameraTexture;
        private HttpClient snapshotHttpClient;
        private HttpClient streamHttpClient;
        private CancellationTokenSource streamCancellation;
        private Task streamTask;
        private byte[] pendingStreamFrame;
        private Coroutine pollingCoroutine;
        private float lastFrameTime = -1f;
        private RealVision realVision;
        private RobotBrain robotBrain;
        private SimulatedYoloCamera simulatedVision;
        private GameObject simulatedCameraObject;
        private Camera simulatedCamera;
        private RenderTexture simulatedCameraTexture;

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
            robotBrain = FindAnyObjectByType<RobotBrain>();
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
            DestroySimulatedCameraPreview();
        }

        private void OnDestroy()
        {
            if (cameraTexture != null)
            {
                Destroy(cameraTexture);
            }

            DestroySimulatedCameraPreview();
        }

        private IEnumerator PollCamera()
        {
            while (enabled)
            {
                if (!UsesRealRobotSensors())
                {
                    StopMjpegStream();
                    yield return new WaitForSecondsRealtime(RefreshIntervalSeconds);
                    continue;
                }

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
                }
                else
                {
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
                }

                yield return new WaitForSecondsRealtime(RefreshIntervalSeconds);
            }
        }

        private void StartMjpegStream()
        {
            StopMjpegStream();
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
            }
            catch (Exception)
            {
            }
        }

        private void LateUpdate()
        {
            if (UsesRealRobotSensors() || !EnsureSimulatedCameraPreview())
            {
                return;
            }

            float aspect = Mathf.Max(0.1f, simulatedVision.cameraAspectRatio);
            float horizontalFovRadians = Mathf.Clamp(simulatedVision.horizontalFOV, 1f, 179f) * Mathf.Deg2Rad;
            simulatedCamera.aspect = aspect;
            simulatedCamera.fieldOfView = 2f * Mathf.Atan(
                Mathf.Tan(horizontalFovRadians * 0.5f) / aspect) * Mathf.Rad2Deg;
            simulatedCamera.farClipPlane = Mathf.Max(
                simulatedCamera.nearClipPlane + 0.1f,
                simulatedVision.maxVisibleDistance);
            simulatedCamera.Render();
        }

        private bool UsesRealRobotSensors()
        {
            if (robotBrain == null)
            {
                robotBrain = FindAnyObjectByType<RobotBrain>();
            }

            return robotBrain != null && robotBrain.UseRealRobotSensors;
        }

        private bool EnsureSimulatedCameraPreview()
        {
            SimulatedYoloCamera selectedVision = robotBrain != null ? robotBrain.yoloCamera : null;
            if (selectedVision == null)
            {
                return false;
            }

            if (simulatedVision != selectedVision)
            {
                DestroySimulatedCameraPreview();
                simulatedVision = selectedVision;
            }

            if (simulatedCamera != null && simulatedCameraTexture != null)
            {
                return true;
            }

            simulatedCameraObject = new GameObject("Virtual Camera Preview")
            {
                hideFlags = HideFlags.DontSave
            };
            simulatedCameraObject.transform.SetParent(simulatedVision.transform, false);

            simulatedCamera = simulatedCameraObject.AddComponent<Camera>();
            simulatedCamera.enabled = false;
            simulatedCamera.clearFlags = CameraClearFlags.SolidColor;
            simulatedCamera.backgroundColor = Color.black;
            simulatedCamera.nearClipPlane = 0.01f;
            simulatedCamera.allowHDR = false;
            simulatedCamera.allowMSAA = false;

            simulatedCameraTexture = new RenderTexture(
                VirtualFrameWidth,
                VirtualFrameHeight,
                16,
                RenderTextureFormat.ARGB32)
            {
                name = "Virtual Camera Preview Texture"
            };
            simulatedCameraTexture.Create();
            simulatedCamera.targetTexture = simulatedCameraTexture;
            return true;
        }

        private void DestroySimulatedCameraPreview()
        {
            if (simulatedCamera != null)
            {
                simulatedCamera.targetTexture = null;
            }

            if (simulatedCameraTexture != null)
            {
                simulatedCameraTexture.Release();
                Destroy(simulatedCameraTexture);
            }

            if (simulatedCameraObject != null)
            {
                Destroy(simulatedCameraObject);
            }

            simulatedCameraTexture = null;
            simulatedCamera = null;
            simulatedCameraObject = null;
            simulatedVision = null;
        }

        private void OnGUI()
        {
            GUI.depth = -500;
            const float frameWidth = 260f;
            const float frameHeight = 195f;
            const float panelWidth = 280f;
            const float panelHeight = 229f;
            var panel = new Rect(10f, 42f, panelWidth, panelHeight);
            bool useRealCamera = UsesRealRobotSensors();
            GUI.Box(panel, useRealCamera ? "Robot camera" : "Virtual camera");

            var frameRect = new Rect(panel.x + 10f, panel.y + 24f, frameWidth, frameHeight);
            if (!useRealCamera)
            {
                if (simulatedCameraTexture != null)
                {
                    GUI.DrawTexture(frameRect, simulatedCameraTexture, ScaleMode.ScaleToFit, false);
                }
                else
                {
                    GUI.Box(frameRect, "Virtual camera unavailable");
                }

                return;
            }

            bool hasFrame = lastFrameTime >= 0f;
            bool frameIsFresh = hasFrame && Time.unscaledTime - lastFrameTime < 2f;
            if (hasFrame && cameraTexture != null)
            {
                GUI.DrawTexture(frameRect, cameraTexture, ScaleMode.ScaleToFit, false);
                if (frameIsFresh)
                {
                    DrawYoloOverlay(frameRect);
                }
            }
            else
            {
                GUI.Box(frameRect, "No camera frame");
            }
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
