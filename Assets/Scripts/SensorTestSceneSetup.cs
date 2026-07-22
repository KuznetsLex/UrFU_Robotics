using Team11.Ros;
using Unity.MLAgents.Policies;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-10000)]
public sealed class SensorTestSceneSetup : MonoBehaviour
{
    private const int ObstacleLayer = 8;
    private const float IrTestDistance = 2f;
    private const float UltrasonicTestDistance = 6f;
    private const float VisionTargetDistance = 12f;
    private const float VisionOccluderDistance = 6f;

    [Header("Arena test prefabs")]
    [SerializeField] private GameObject ballPrefab;
    [SerializeField] private GameObject boxPrefab;

    private VirtualSensors sensors;
    private CameraRotator cameraRotator;
    private RobotBrain brain;
    private SimulatedYoloCamera vision;
    private GameObject testObstacle;
    private GameObject testBall;
    private GameObject visionOccluder;
    private int activeTest;

    private void Awake()
    {
        brain = FindAnyObjectByType<RobotBrain>();
        if (brain != null)
        {
            brain.isTraining = false;
            brain.environmentManager = null;
            brain.MaxStep = 0;
            brain.UseRealRobotIo = false;

            BehaviorParameters behavior = brain.GetComponent<BehaviorParameters>();
            if (behavior != null)
                behavior.BehaviorType = BehaviorType.HeuristicOnly;

            sensors = brain.GetComponent<VirtualSensors>();
            cameraRotator = brain.cameraRotator != null
                ? brain.cameraRotator
                : brain.GetComponentInChildren<CameraRotator>(true);
            vision = brain.yoloCamera;
        }

        CreateFloor();
        CreateTestObjects();
        ConfigureCamera();
        EnsureCameraPreview();
        SelectTest(0);
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard.digit0Key.wasPressedThisFrame)
            SelectTest(0);
        else if (keyboard.digit1Key.wasPressedThisFrame)
            SelectTest(1);
        else if (keyboard.digit2Key.wasPressedThisFrame)
            SelectTest(2);
        else if (keyboard.digit3Key.wasPressedThisFrame)
            SelectTest(3);
        else if (keyboard.digit4Key.wasPressedThisFrame)
            SelectTest(4);
        else if (keyboard.digit5Key.wasPressedThisFrame)
            SelectTest(5);
        else if (keyboard.digit6Key.wasPressedThisFrame)
            SelectTest(6);
    }

    private void SelectTest(int test)
    {
        activeTest = test;
        SetActive(testObstacle, false);
        SetActive(testBall, false);
        SetActive(visionOccluder, false);
        if (vision != null)
            vision.targetBall = null;

        if (test >= 5 && test <= 6)
        {
            SelectVisionTest(test);
            return;
        }

        if (sensors == null)
            return;

        if (test == 2)
        {
            PlaceGripIrBall();
            return;
        }

        Transform sensorPoint = test switch
        {
            1 => sensors.leftIRPoint,
            3 => sensors.rightIRPoint,
            4 => sensors.ultrasonicPoint,
            _ => null
        };

        if (sensorPoint == null || testObstacle == null)
            return;

        float distance = test switch
        {
            4 => UltrasonicTestDistance,
            _ => IrTestDistance
        };
        float maximumRayDistance = test == 4
            ? UltrasonicTestDistance
            : sensors.irDistance;
        PlaceRayTestObstacle(sensorPoint, distance, maximumRayDistance);
    }

    private void PlaceRayTestObstacle(
        Transform sensorPoint,
        float desiredDistance,
        float maximumRayDistance)
    {
        if (sensorPoint == null || testObstacle == null)
            return;

        testObstacle.SetActive(true);
        Quaternion targetRotation = Quaternion.LookRotation(sensorPoint.forward, sensorPoint.up);
        testObstacle.transform.rotation = targetRotation;
        Physics.SyncTransforms();

        Collider targetCollider = testObstacle.GetComponent<Collider>();
        float halfDepth = targetCollider != null
            ? Vector3.Dot(targetCollider.bounds.extents, Abs(sensorPoint.forward))
            : 0.2f;
        float maximumCenterDistance = maximumRayDistance + Mathf.Max(0.05f, halfDepth * 0.8f);
        float startDistance = Mathf.Min(desiredDistance, maximumCenterDistance);

        for (float distance = startDistance;
             distance <= maximumCenterDistance;
             distance += 0.05f)
        {
            testObstacle.transform.position = sensorPoint.position + sensorPoint.forward * distance;
            Physics.SyncTransforms();
            if (!OverlapsRobot(targetCollider, includeTriggers: true))
                return;
        }

        testObstacle.SetActive(false);
        Debug.LogWarning(
            $"SensorTest: не удалось поставить цель для {sensorPoint.name} вне хитбоксов робота.",
            this);
    }

    private void SelectVisionTest(int test)
    {
        if (vision == null)
            return;

        bool occluded = test == 6;
        GameObject target = testBall;
        if (target == null)
            return;

        Transform cameraPoint = vision.transform;
        target.SetActive(true);
        target.transform.SetPositionAndRotation(
            cameraPoint.position + cameraPoint.forward * VisionTargetDistance,
            Quaternion.identity);
        vision.targetBall = target.transform;

        if (occluded && visionOccluder != null)
        {
            visionOccluder.SetActive(true);
            visionOccluder.transform.SetPositionAndRotation(
                cameraPoint.position + cameraPoint.forward * VisionOccluderDistance,
                Quaternion.LookRotation(cameraPoint.forward, cameraPoint.up));
        }

        Physics.SyncTransforms();
    }

    private void PlaceGripIrBall()
    {
        if (testBall == null || sensors?.gripperIRPoint == null)
            return;

        Transform sensorPoint = sensors.gripperIRPoint;
        testBall.SetActive(true);
        Collider ballCollider = testBall.GetComponentInChildren<Collider>();
        float ballRadius = ballCollider != null
            ? Mathf.Max(ballCollider.bounds.extents.x, ballCollider.bounds.extents.y, ballCollider.bounds.extents.z)
            : 0.25f;
        float minimumCenterDistance = ballRadius + 0.05f;
        float maximumCenterDistance = sensors.gripperDistance + ballRadius * 0.9f;

        for (float distance = minimumCenterDistance;
             distance <= maximumCenterDistance;
             distance += 0.05f)
        {
            testBall.transform.SetPositionAndRotation(
                sensorPoint.position + sensorPoint.forward * distance,
                Quaternion.identity);
            Physics.SyncTransforms();
            if (!OverlapsRobot(ballCollider, includeTriggers: false))
                return;
        }

        testBall.SetActive(false);
        Debug.LogWarning("SensorTest: не удалось поставить Grip IR target вне коллайдеров робота.", this);
    }

    private bool OverlapsRobot(Collider targetCollider, bool includeTriggers)
    {
        if (targetCollider == null || brain == null)
            return false;

        foreach (Collider robotCollider in brain.GetComponentsInChildren<Collider>())
        {
            if (robotCollider == null ||
                !robotCollider.enabled ||
                (!includeTriggers && robotCollider.isTrigger))
                continue;

            if (Physics.ComputePenetration(
                    targetCollider,
                    targetCollider.transform.position,
                    targetCollider.transform.rotation,
                    robotCollider,
                    robotCollider.transform.position,
                    robotCollider.transform.rotation,
                    out _,
                    out _))
            {
                return true;
            }
        }

        return false;
    }

    private static void SetActive(GameObject target, bool active)
    {
        if (target != null)
            target.SetActive(active);
    }

    private static Vector3 Abs(Vector3 value)
    {
        return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
    }

    private void CreateFloor()
    {
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Sensor Test Floor";
        floor.layer = 31;
        floor.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        floor.transform.localScale = new Vector3(4f, 1f, 4f);
    }

    private void CreateTestObjects()
    {
        testObstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        testObstacle.name = "Switchable Sensor Target";
        testObstacle.layer = ObstacleLayer;
        testObstacle.transform.localScale = new Vector3(0.8f, 0.8f, 0.4f);

        Renderer obstacleRenderer = testObstacle.GetComponent<Renderer>();
        if (obstacleRenderer != null)
            obstacleRenderer.material.color = new Color(1f, 0.45f, 0.05f);

        if (ballPrefab != null)
        {
            testBall = Instantiate(ballPrefab);
            testBall.name = "Arena Ball Test Target";
            FreezeRigidbody(testBall);
            if (brain != null)
                brain.ball = testBall.transform;
        }
        else
        {
            Debug.LogError("SensorTest: Ball prefab не назначен.", this);
        }

        if (boxPrefab != null)
        {
            visionOccluder = Instantiate(boxPrefab);
            visionOccluder.name = "Arena Box Vision Occluder";
            visionOccluder.layer = ObstacleLayer;
            visionOccluder.transform.localScale = new Vector3(3.6f, 2.6f, 1.4f);
            FreezeRigidbody(visionOccluder);
        }
        else
        {
            Debug.LogError("SensorTest: ObstacleBox prefab не назначен.", this);
        }

        SetActive(testObstacle, false);
        SetActive(testBall, false);
        SetActive(visionOccluder, false);
    }

    private static void FreezeRigidbody(GameObject target)
    {
        if (target != null && target.TryGetComponent(out Rigidbody body))
        {
            body.isKinematic = true;
            body.useGravity = false;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }

    private static void ConfigureCamera()
    {
        Camera camera = Camera.main;
        if (camera == null)
            return;

        camera.orthographic = true;
        camera.orthographicSize = 11f;
        camera.transform.position = new Vector3(0f, 18f, -14f);
        camera.transform.LookAt(new Vector3(0f, 0f, 3f));
    }

    private void EnsureCameraPreview()
    {
        if (FindAnyObjectByType<RobotCameraView>() == null)
            gameObject.AddComponent<RobotCameraView>();
    }

    private void OnGUI()
    {
        float panelWidth = Mathf.Clamp(Screen.width - 32f, 320f, 540f);
        const float panelHeight = 200f;
        const float contentX = 30f;
        float contentWidth = panelWidth - 28f;
        GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = true
        };

        Rect panel = new Rect(16f, 16f, panelWidth, panelHeight);
        GUI.Box(panel, "Sensor test");

        GUI.Label(new Rect(contentX, 45f, contentWidth, 22f),
            "WASD — движение; 0 — убрать цель", labelStyle);
        GUI.Label(new Rect(contentX, 68f, contentWidth, 22f),
            "1 — левый ИК; 2 — Grip IR с arena ball; 3 — правый ИК; 4 — УЗ", labelStyle);
        GUI.Label(new Rect(contentX, 91f, contentWidth, 22f),
            "5 — шар виден камерой; 6 — шар за коробкой", labelStyle);
        GUI.Label(new Rect(contentX, 118f, contentWidth, 22f),
            $"Активный тест: {GetTestName(activeTest)}", labelStyle);

        string panState = cameraRotator != null
            ? $"текущий={cameraRotator.CurrentAngle:F1}°, цель={cameraRotator.TargetAngle:F1}°"
            : "недоступен";
        GUI.Label(new Rect(contentX, 144f, contentWidth, 22f),
            $"Поворот камеры/УЗ: {panState}", labelStyle);
        GUI.Label(new Rect(contentX, 169f, contentWidth, 22f),
            "Q/E — поворот; C — центр", labelStyle);

        float ultrasonicDistance = 0f;
        bool leftIr = false;
        bool rightIr = false;
        bool gripperMountedIr = false;
        bool hasSensorData = sensors != null && sensors.TryReadSimulationSensors(
            out ultrasonicDistance,
            out leftIr,
            out rightIr,
            out gripperMountedIr);
        DrawSensorPanel(
            hasSensorData,
            ultrasonicDistance,
            leftIr,
            rightIr,
            gripperMountedIr);
    }

    private void DrawSensorPanel(
        bool hasSensorData,
        float ultrasonicMeters,
        bool leftIr,
        bool rightIr,
        bool gripperIr)
    {
        const float width = 440f;
        const float height = 142f;
        const float cameraPanelHeight = 229f;
        float panelX = Mathf.Max(10f, Screen.width - width - 16f);
        var panel = new Rect(panelX, 16f + cameraPanelHeight + 12f, width, height);
        GUI.Box(panel, "Sensors — SIMULATION");

        string sensorState = hasSensorData
            ? $"Ultrasonic: {ultrasonicMeters * 100f:F0} cm"
            : "Simulation sensors: no data";
        GUI.Label(new Rect(panel.x + 12f, panel.y + 24f, width - 24f, 20f), sensorState);

        DrawUltrasonicBar(
            new Rect(panel.x + 12f, panel.y + 46f, width - 24f, 10f),
            hasSensorData,
            ultrasonicMeters);

        const float indicatorWidth = 128f;
        const float indicatorGap = 12f;
        float indicatorsX = panel.x + 12f;
        DrawIrIndicator(
            new Rect(indicatorsX, panel.y + 64f, indicatorWidth, 28f),
            "IR left",
            hasSensorData,
            leftIr);
        DrawIrIndicator(
            new Rect(indicatorsX + indicatorWidth + indicatorGap, panel.y + 64f, indicatorWidth, 28f),
            "IR right",
            hasSensorData,
            rightIr);
        DrawIrIndicator(
            new Rect(indicatorsX + (indicatorWidth + indicatorGap) * 2f, panel.y + 64f, indicatorWidth, 28f),
            "IR grip",
            hasSensorData,
            gripperIr);

        (float angle, float areaRatio, float aspectRatio, bool visible) vision =
            (0f, 0f, 0f, false);
        bool hasVisionData = brain != null && brain.TryGetSelectedVision(out vision);
        string visionState = hasVisionData
            ? $"Vision: visible={(vision.visible ? 1 : 0)} | angle={vision.angle:+0.000;-0.000;0.000} | " +
              $"area={vision.areaRatio:F4} | aspect={vision.aspectRatio:F3}"
            : "Vision: no data";
        GUI.Label(new Rect(panel.x + 12f, panel.y + 106f, width - 24f, 20f), visionState);
    }

    private static void DrawUltrasonicBar(Rect rect, bool hasData, float distanceMeters)
    {
        Color previousColor = GUI.color;
        GUI.color = new Color(0.15f, 0.15f, 0.15f, 0.9f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);

        if (hasData)
        {
            float normalizedDistance = Mathf.Clamp01(distanceMeters / 5f);
            GUI.color = Color.Lerp(
                new Color(0.9f, 0.2f, 0.15f),
                new Color(0.2f, 0.8f, 0.3f),
                normalizedDistance);
            GUI.DrawTexture(
                new Rect(rect.x, rect.y, rect.width * normalizedDistance, rect.height),
                Texture2D.whiteTexture);
        }

        GUI.color = previousColor;
    }

    private static void DrawIrIndicator(Rect rect, string label, bool hasData, bool triggered)
    {
        Color previousBackground = GUI.backgroundColor;
        GUI.backgroundColor = !hasData
            ? Color.gray
            : triggered
                ? new Color(0.95f, 0.25f, 0.2f)
                : new Color(0.2f, 0.75f, 0.3f);

        string value = !hasData ? "--" : triggered ? "BLOCKED" : "CLEAR";
        GUI.Box(rect, $"{label}: {value}");
        GUI.backgroundColor = previousBackground;
    }

    private static string GetTestName(int test)
    {
        return test switch
        {
            1 => "левый ИК (цель на 20 см)",
            2 => "центральный ИК на захвате",
            3 => "правый ИК (цель на 20 см)",
            4 => "ультразвук (цель на 60 см)",
            5 => "arena ball виден камерой",
            6 => "arena ball закрыт arena box",
            _ => "без препятствия"
        };
    }
}
