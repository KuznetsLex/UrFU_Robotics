using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using UnityEngine.InputSystem;

[RequireComponent(typeof(TrackController), typeof(VirtualSensors), typeof(Rigidbody))]
public class RobotBrain : Agent
{
    [Header("Component references")]
    public TrackController trackController;
    public VirtualSensors sensors;
    public SimulatedYoloCamera yoloCamera;
    public Transform gripperTransform;
    public Transform holdPoint;
    public Transform ball;

    [Header("Training")]
    public float wallPenalty = -0f;
    public float moveRewardScale = 0.01f;
    public float gripReward = 1f;
    public float outOfBoundsPenalty = -0.5f;
    public Vector2 arenaHalfExtents = new Vector2(300f, 300f);
    public float fallDistance = 3f;
    public float noDetectionSteps = 100f;
    public float stepPenalty = -0.0002f;
    public float grabAttemptReward = 0.1f;        // бонус за попытку захвата рядом

    private Rigidbody rb;
    private Vector3 startPos;
    private Quaternion startRot;
    private Vector3 ballStartPos;
    private Quaternion ballStartRot;
    private Transform ballStartParent;
    private Rigidbody ballRb;
    private Collider ballCollider;
    private float lastDistanceToBall;
    private bool hasBall;
    private bool targetVisible;
    private float stepsSinceLastDetection;
    private float lastKnownAngle;
    private float lastKnownDistance = 1f;
    private float prevGas;
    private float prevSteer;
    private float prevAbsAngle = 180f; // инициализируем большим значением
    private bool grabZoneRewardGranted;

    protected override void Awake()
    {
        base.Awake();
        rb = GetComponent<Rigidbody>();
        if (trackController == null) trackController = GetComponent<TrackController>();
        if (sensors == null) sensors = GetComponent<VirtualSensors>();
    }

    public override void Initialize()
    {
        startPos = rb.position;
        startRot = rb.rotation;

        if (ball == null)
            ball = GameObject.FindWithTag("TargetBall")?.transform;

        if (ball != null)
        {
            ballStartPos = ball.position;
            ballStartRot = ball.rotation;
            ballStartParent = ball.parent;
            ballRb = ball.GetComponent<Rigidbody>();
            ballCollider = ball.GetComponent<Collider>();
            lastDistanceToBall = holdPoint != null
                ? Vector3.Distance(holdPoint.position, ball.position)
                : 0f;
        }
    }

    public override void OnEpisodeBegin()
    {
        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        if (gripper != null)
            gripper.Release();

        trackController?.Stop();
        rb.position = startPos;
        rb.rotation = startRot;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        ResetBall();

        hasBall = false;
        targetVisible = false;
        stepsSinceLastDetection = 0f;
        lastKnownAngle = 0f;
        lastKnownDistance = 1f;
        prevGas = 0f;
        prevSteer = 0f;
        prevAbsAngle = 180f;
        grabZoneRewardGranted = false;
        lastDistanceToBall = ball != null && holdPoint != null
            ? Vector3.Distance(holdPoint.position, ball.position)
            : 0f;
    }

    private void ResetBall()
    {
        if (ball == null)
            return;

        ball.SetParent(ballStartParent);
        ball.SetPositionAndRotation(ballStartPos, ballStartRot);

        if (ballRb != null)
        {
            ballRb.isKinematic = false;
            ballRb.linearVelocity = Vector3.zero;
            ballRb.angularVelocity = Vector3.zero;
        }

        if (ballCollider != null)
            ballCollider.enabled = true;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(sensors.GetUltrasonicNormalized());
        sensor.AddObservation(sensors.GetLeftIR());
        sensor.AddObservation(sensors.GetRightIR());
        sensor.AddObservation(sensors.GetGripperIR());

        var targetInfo = yoloCamera != null
            ? yoloCamera.GetTargetInfo()
            : (angle: 0f, distance: 1f, visible: false);

        targetVisible = targetInfo.visible;
        if (targetInfo.visible)
        {
            lastKnownAngle = targetInfo.angle;
            lastKnownDistance = targetInfo.distance;
            stepsSinceLastDetection = 0f;
        }

        sensor.AddObservation(targetInfo.visible ? targetInfo.angle : lastKnownAngle);
        sensor.AddObservation(targetInfo.visible ? targetInfo.distance : lastKnownDistance);
        sensor.AddObservation(targetInfo.visible ? 1f : 0f);
        sensor.AddObservation(hasBall ? 1f : 0f);

        Vector3 deltaPos = rb.position - startPos;
        float arenaX = Mathf.Max(arenaHalfExtents.x, 0.01f);
        float arenaZ = Mathf.Max(arenaHalfExtents.y, 0.01f);
        sensor.AddObservation(Mathf.Clamp(deltaPos.x / arenaX, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(deltaPos.z / arenaZ, -1f, 1f));

        float signedHeading = Mathf.DeltaAngle(0f, rb.rotation.eulerAngles.y) / 180f;
        sensor.AddObservation(signedHeading);
        sensor.AddObservation(Mathf.Clamp01(rb.linearVelocity.magnitude / 2f));
        sensor.AddObservation(Mathf.Clamp01(stepsSinceLastDetection / noDetectionSteps));
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // Обновляем счётчик шагов без детекции
        if (!targetVisible)
            stepsSinceLastDetection++;
        else
            stepsSinceLastDetection = 0; // сбрасываем, если цель видна

        if (trackController == null)
            return;

        float gas = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
        float steer = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
        int gripCommand = actions.DiscreteActions[0];

        trackController.Move(gas, steer);
        AddReward(stepPenalty);

        float gasDelta = Mathf.Abs(gas - prevGas);
        float steerDelta = Mathf.Abs(steer - prevSteer);
        AddReward(-0.002f * (gasDelta + steerDelta));
        prevGas = gas;
        prevSteer = steer;

        // Награда за уменьшение угла до мяча (если цель видна)
        if (targetVisible)
        {
            float currentAbsAngle = Mathf.Abs(lastKnownAngle);
            // Симметричный shaping: улучшение даёт плюс, ухудшение — такой же минус.
            if (prevAbsAngle < 180f)
            {
                float angleImprovement = prevAbsAngle - currentAbsAngle;
                AddReward(0.005f * angleImprovement);
            }
            prevAbsAngle = currentAbsAngle;
        }
        else
        {
            // Если цель не видна, сбрасываем предыдущее значение, чтобы не награждать за "мнимое" уменьшение
            prevAbsAngle = 180f;
        }

        GripperController gripper = gripperTransform?.GetComponent<GripperController>();
        if (gripper != null)
        {
            if (!grabZoneRewardGranted && gripper.IsTargetInGrabZone())
            {
                AddReward(grabAttemptReward);
                grabZoneRewardGranted = true;
            }

            if (gripCommand == 1) gripper.Grab();
            else if (gripCommand == 2) gripper.Release();
        }

        if (ball != null)
        {
            float currentDistance = Vector3.Distance(holdPoint.position, ball.position);
            AddReward((lastDistanceToBall - currentDistance) * moveRewardScale);
            lastDistanceToBall = currentDistance;
        }

        if (sensors.GetUltrasonicNormalized() < 0.01f)
            AddReward(wallPenalty * 0.5f);
        if (sensors.GetLeftIR() > 0.5f || sensors.GetRightIR() > 0.5f)
            AddReward(wallPenalty);

        if (gripper != null && gripper.IsGrabbing)
        {
            hasBall = true;
            AddReward(gripReward);
            EndEpisode();
            return;
        }

        Vector3 displacement = rb.position - startPos;
        bool outsideArena = Mathf.Abs(displacement.x) > arenaHalfExtents.x
            || Mathf.Abs(displacement.z) > arenaHalfExtents.y
            || displacement.y < -fallDistance;

        if (outsideArena)
        {
            AddReward(outOfBoundsPenalty);
            EndEpisode();
            return;
        }

        if (stepsSinceLastDetection > noDetectionSteps)
        {
            AddReward(-0.2f);
            EndEpisode();
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        float gas = 0f;
        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) gas += 1f;
        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) gas -= 1f;

        float steer = 0f;
        if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) steer -= 1f;
        if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) steer += 1f;

        var continuousActions = actionsOut.ContinuousActions;
        var discreteActions = actionsOut.DiscreteActions;
        continuousActions[0] = gas;
        continuousActions[1] = steer;

        if (keyboard.spaceKey.wasPressedThisFrame)
            discreteActions[0] = 1;
        else if (keyboard.rKey.wasPressedThisFrame)
            discreteActions[0] = 2;
        else
            discreteActions[0] = 0;
    }
}
