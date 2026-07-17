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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<RobotCameraView>() != null)
            {
                return;
            }

            // Не опрашиваем HTTP-камеру реального робота во время обучения — см. тот
            // же комментарий в RobotRosTeleop.Install().
            RobotBrain brain = FindAnyObjectByType<RobotBrain>();
            if (brain != null && brain.isTraining)
            {
                return;
            }

            ROSConnection.GetOrCreateInstance().gameObject.AddComponent<RobotCameraView>();
        }

        private void OnEnable()
        {
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
            const float panelHeight = 292f;
            var panel = new Rect(10f, 42f, panelWidth, panelHeight);
            GUI.Box(panel, "Robot camera");

            bool frameIsFresh = lastFrameTime >= 0f && Time.unscaledTime - lastFrameTime < 2f;
            var frameRect = new Rect(panel.x + 10f, panel.y + 24f, 320f, 240f);
            if (frameIsFresh && cameraTexture != null)
            {
                GUI.DrawTexture(frameRect, cameraTexture, ScaleMode.ScaleToFit, false);
            }
            else
            {
                GUI.Box(frameRect, "No camera frame");
            }

            GUI.Label(
                new Rect(panel.x + 10f, panel.y + 268f, panelWidth - 20f, 20f),
                status);
        }
    }
}
