using Unity.MLAgents.Policies;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-10000)]
public sealed class SensorTestSceneSetup : MonoBehaviour
{
    private const int ObstacleLayer = 8;
    private const float IrTestDistance = 2f;
    private const float UltrasonicTestDistance = 6f;

    private VirtualSensors sensors;
    private GameObject testObstacle;
    private int activeTest;

    private void Awake()
    {
        RobotBrain brain = FindAnyObjectByType<RobotBrain>();
        if (brain != null)
        {
            brain.isTraining = false;
            brain.randomizeSpawn = false;
            brain.randomizeBall = false;
            brain.environmentManager = null;
            brain.MaxStep = 0;
            brain.UseRealRobotIo = false;

            BehaviorParameters behavior = brain.GetComponent<BehaviorParameters>();
            if (behavior != null)
                behavior.BehaviorType = BehaviorType.HeuristicOnly;

            sensors = brain.GetComponent<VirtualSensors>();
        }

        CreateFloor();
        CreateTestObstacle();
        ConfigureCamera();
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
    }

    private void SelectTest(int test)
    {
        activeTest = test;
        if (testObstacle == null || sensors == null)
            return;

        Transform sensorPoint = test switch
        {
            1 => sensors.leftIRPoint,
            2 => sensors.gripperIRPoint,
            3 => sensors.rightIRPoint,
            4 => sensors.ultrasonicPoint,
            _ => null
        };

        testObstacle.SetActive(sensorPoint != null);
        if (sensorPoint == null)
            return;

        float distance = test switch
        {
            2 => Mathf.Max(0.05f, sensors.gripperDistance * 0.75f),
            4 => UltrasonicTestDistance,
            _ => IrTestDistance
        };
        testObstacle.transform.SetPositionAndRotation(
            sensorPoint.position + sensorPoint.forward * distance,
            Quaternion.LookRotation(sensorPoint.forward, sensorPoint.up));
        Physics.SyncTransforms();
    }

    private void CreateFloor()
    {
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Sensor Test Floor";
        floor.layer = 31;
        floor.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        floor.transform.localScale = new Vector3(4f, 1f, 4f);
    }

    private void CreateTestObstacle()
    {
        testObstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        testObstacle.name = "Switchable Sensor Target";
        testObstacle.layer = ObstacleLayer;
        testObstacle.transform.localScale = new Vector3(0.8f, 0.8f, 0.4f);

        Renderer obstacleRenderer = testObstacle.GetComponent<Renderer>();
        if (obstacleRenderer != null)
            obstacleRenderer.material.color = new Color(1f, 0.45f, 0.05f);
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

    private void OnGUI()
    {
        const float panelWidth = 420f;
        Rect panel = new Rect(16f, 16f, panelWidth, 190f);
        GUI.Box(panel, "Sensor test");

        GUI.Label(new Rect(30f, 45f, panelWidth - 28f, 22f),
            "WASD — движение; 0 — убрать цель");
        GUI.Label(new Rect(30f, 68f, panelWidth - 28f, 22f),
            "1 — левый ИК; 2 — центральный ИК на захвате; 3 — правый ИК; 4 — УЗ");
        GUI.Label(new Rect(30f, 91f, panelWidth - 28f, 22f),
            $"Активный тест: {GetTestName(activeTest)}");

        if (sensors == null || !sensors.TryReadSimulationSensors(
                out float ultrasonicDistance,
                out bool leftIr,
                out bool rightIr,
                out bool gripperMountedIr))
        {
            GUI.Label(new Rect(30f, 120f, panelWidth - 28f, 22f), "Датчики не найдены");
            return;
        }

        GUI.Label(new Rect(30f, 120f, panelWidth - 28f, 22f),
            $"УЗ: {ultrasonicDistance:F2} ед. | боковые ИК: {sensors.irDistance:F2} ед. | захват: {sensors.gripperDistance:F2} ед.");
        GUI.Label(new Rect(30f, 145f, panelWidth - 28f, 22f),
            $"ИК: левый={AsSignal(leftIr)}  захват={AsSignal(gripperMountedIr)}  правый={AsSignal(rightIr)}");
    }

    private static string GetTestName(int test)
    {
        return test switch
        {
            1 => "левый ИК (цель на 20 см)",
            2 => "центральный ИК на захвате",
            3 => "правый ИК (цель на 20 см)",
            4 => "ультразвук (цель на 60 см)",
            _ => "без препятствия"
        };
    }

    private static string AsSignal(bool triggered) => triggered ? "1" : "0";
}
