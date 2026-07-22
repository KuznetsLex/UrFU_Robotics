using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class EnvironmentManager : MonoBehaviour
{
    private const string ObstacleLayerName = "Obstacle";

    [Header("Связи с объектами")]
    public Transform robot;
    public Transform targetBall;
    public GameObject boxPrefab;

    [Header("Масштабирование мира")]
    public float globalScale = 10f;

    [Header("Базовые настройки арены")]
    public Vector2 baseArenaSize = new Vector2(3f, 6f);
    public int boxCount = 8;
    public float baseSafeRadius = 0.5f;
    [Tooltip("Насколько далеко от края пути могут появляться коробки")]
    public float scatterMargin = 0.8f;
    public LayerMask obstacleMask;

    [Tooltip("Максимальное расстояние (в мировых юнитах) от мяча до стены, противоположной старту робота")]
    public float maxBallDistanceFromFarWall = 1.15f;

    [Header("Настройки границ (Стены)")]
    public bool createWalls = true;       // Создавать ли физические границы
    public bool visibleWalls = true;      // Оставить ли стены видимыми (MeshRenderer)
    public float baseWallHeight = 0.5f;   // Высота стен до масштабирования
    public Color wallColor = Color.black; // Цвет стен — чтобы их было видно при виде сверху

    [Header("Domain Randomization")]
    [Tooltip("Рандомизировать количество коробок при каждой генерации арены (диапазон boxCountRange) вместо фиксированного boxCount")]
    public bool randomizeBoxCount = true;
    [Tooltip("Диапазон количества коробок при randomizeBoxCount (включительно)")]
    public Vector2Int boxCountRange = new Vector2Int(4, 10);

    [Tooltip("Рандомизировать массу каждой коробки (диапазон boxMassRange) вместо фиксированной boxMass")]
    public bool randomizeBoxMass = true;
    [Tooltip("Масса коробки в кг, если randomizeBoxMass выключен")]
    public float boxMass = 0.5f;
    [Tooltip("Диапазон массы коробки в кг при randomizeBoxMass (0.5 = 500 г)")]
    public Vector2 boxMassRange = new Vector2(0.35f, 0.65f);

    private readonly float[] baseBoxDimensions = { 0.14f, 0.26f, 0.36f };
    private List<GameObject> activeBoxes = new List<GameObject>();
    private List<Vector3> pathSegments = new List<Vector3>();
    private GameObject wallsContainer;    // Контейнер для сгенерированных стен
    private int obstacleLayer = -1;
    private Material wallMaterial;        // Общий материал для всех стен (создаётся один раз)

    private void Awake()
    {
        // Параметры должны быть применены до первой генерации арены и до
        // дополнения obstacleMask обязательным слоем препятствий.
        TrainingConfig.ApplyOverrides(this, "EnvironmentManager");

        obstacleLayer = LayerMask.NameToLayer(ObstacleLayerName);
        if (obstacleLayer < 0)
        {
            Debug.LogError($"EnvironmentManager: слой {ObstacleLayerName} не настроен в TagManager.", this);
            return;
        }

        // Проверка свободного места должна учитывать уже созданные стены и коробки.
        obstacleMask |= 1 << obstacleLayer;
    }

    void Update()
    {
        // F5, а не Space — Space занят захватом мяча в RobotBrain.Heuristic() и
        // аварийным стопом реального робота в RobotRosTeleop; при общей клавише
        // одно нажатие Space одновременно пыталось бы схватить мяч, перегенерировать
        // все арены и остановить реального робота.
        if (Keyboard.current != null && Keyboard.current.f5Key.wasPressedThisFrame)
        {
            GenerateArena();
        }
    }

    public void GenerateArena()
    {
        // Количество коробок фиксируем один раз на всю генерацию (а не на
        // каждую попытку перекрыть LOS), чтобы оно не "плавало" между
        // повторными попытками внутри одного вызова.
        int currentBoxCount = randomizeBoxCount
            ? Random.Range(boxCountRange.x, boxCountRange.y + 1)
            : boxCount;

        int maxRetries = 15;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            ClearArena();
            BuildBoundaries(); // Генерируем стены вокруг полигона
            RandomizeStartAndTarget();

            if (Random.value > 0.66f)
            {
                GeneratePolylinePath();
            }
            else
            {
                GenerateSmoothPath();
            }

            if (TryBlockLineOfSight())
            {
                SpawnObstaclesTight(currentBoxCount - 1);
                return;
            }
        }

        Debug.LogWarning("Не удалось идеально перекрыть LOS, спавним арену как есть.");
        SpawnObstaclesTight(currentBoxCount);
    }

    private void ClearArena()
    {
        foreach (GameObject box in activeBoxes)
        {
            if (box != null) Destroy(box);
        }
        activeBoxes.Clear();
        pathSegments.Clear();
    }

    // НОВЫЙ МЕТОД: Автоматическое построение стен
    private void BuildBoundaries()
    {
        if (wallsContainer != null) Destroy(wallsContainer);
        if (!createWalls) return;

        wallsContainer = new GameObject("ArenaBoundaries");
        wallsContainer.transform.SetParent(transform);
        wallsContainer.transform.localPosition = Vector3.zero;
        wallsContainer.transform.localRotation = Quaternion.identity;

        float currentArenaX = baseArenaSize.x * globalScale;
        float currentArenaZ = baseArenaSize.y * globalScale;
        float height = baseWallHeight * globalScale;
        float thickness = 0.1f * globalScale; // Базовая толщина стены 10 см

        // Стены вокруг полигона. Wall_Bottom (сзади стартовой позиции робота,
        // -Z) намеренно не строим — с этой стороны арена теперь открыта.
        CreateWall("Wall_Top", new Vector3(0, height / 2f, currentArenaZ / 2f + thickness / 2f), new Vector3(currentArenaX + thickness * 2f, height, thickness));
        CreateWall("Wall_Right", new Vector3(currentArenaX / 2f + thickness / 2f, height / 2f, 0), new Vector3(thickness, height, currentArenaZ));
        CreateWall("Wall_Left", new Vector3(-currentArenaX / 2f - thickness / 2f, height / 2f, 0), new Vector3(thickness, height, currentArenaZ));
    }

    private void CreateWall(string wallName, Vector3 localPos, Vector3 scale)
    {
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = wallName;
        wall.transform.SetParent(wallsContainer.transform);

        // Позиционируем относительно центра EnvironmentManager
        wall.transform.localPosition = localPos;
        wall.transform.localRotation = Quaternion.identity;
        wall.transform.localScale = scale;
        MarkAsObstacle(wall);

        // Если не хотим видеть стены, удаляем компонент отрисовки, оставляя только коллайдер
        if (!visibleWalls)
        {
            Destroy(wall.GetComponent<MeshRenderer>());
        }
        else
        {
            wall.GetComponent<MeshRenderer>().sharedMaterial = GetWallMaterial();
        }
    }

    private Material GetWallMaterial()
    {
        if (wallMaterial == null)
        {
            wallMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            wallMaterial.color = wallColor;
        }
        return wallMaterial;
    }

    private void RandomizeStartAndTarget()
    {
        float currentArenaX = baseArenaSize.x * globalScale;
        float currentArenaZ = baseArenaSize.y * globalScale;
        float padding = 0.4f * globalScale;

        float halfX = (currentArenaX / 2f) - padding;
        float halfZ = (currentArenaZ / 2f) - padding;

        // Высота (Y) не берётся из фиксированной константы — она зависит от
        // конкретной модели робота (положение pivot над полом). Вместо этого
        // сохраняем текущую высоту робота/мяча относительно арены, меняя только X/Z.
        float robotY = robot.position.y - transform.position.y;

        Vector3 startPos = transform.position + new Vector3(
            Random.Range(-halfX, halfX),
            robotY,
            Random.Range(-halfZ, -halfZ + 1.5f * globalScale)
        );
        Quaternion startRot = Quaternion.Euler(0, Random.Range(-45f, 45f), 0);

        if (robot.TryGetComponent<Rigidbody>(out Rigidbody robotRb))
        {
            robotRb.linearVelocity = Vector3.zero;
            robotRb.angularVelocity = Vector3.zero;
            robotRb.MovePosition(startPos);
            robotRb.MoveRotation(startRot);
        }
        robot.SetPositionAndRotation(startPos, startRot);

        // Мяч (цель) — у дальней от старта робота стены (+Z), не дальше
        // maxBallDistanceFromFarWall от неё, но с отступом padding, чтобы не
        // вонзиться в саму стену.
        float wallZ = currentArenaZ / 2f;
        float ballZMax = wallZ - padding;
        float ballZMin = Mathf.Max(-halfZ, ballZMax - maxBallDistanceFromFarWall);

        float ballY = targetBall.position.y - transform.position.y;

        Vector3 targetPos = transform.position + new Vector3(
            Random.Range(-halfX, halfX),
            ballY,
            Random.Range(ballZMin, ballZMax)
        );

        if (targetBall.TryGetComponent<Rigidbody>(out Rigidbody ballRb))
        {
            ballRb.linearVelocity = Vector3.zero;
            ballRb.angularVelocity = Vector3.zero;
            ballRb.MovePosition(targetPos);
        }
        targetBall.position = targetPos;

        Physics.SyncTransforms();
    }

    private void GeneratePolylinePath()
    {
        Vector3 startP = robot.position;
        Vector3 endP = targetBall.position;
        pathSegments.Add(startP);

        int waypointsCount = Random.Range(2, 6);
        float zStep = (endP.z - startP.z) / (waypointsCount + 1);

        float currentArenaX = baseArenaSize.x * globalScale;
        float currentSafeRadius = baseSafeRadius * globalScale;
        float safeBoundX = (currentArenaX / 2f) - currentSafeRadius - (0.1f * globalScale);

        float sideMultiplier = Random.value > 0.5f ? 1f : -1f;

        for (int i = 1; i <= waypointsCount; i++)
        {
            float randomX = transform.position.x + Random.Range(safeBoundX * 0.5f, safeBoundX) * sideMultiplier;
            float targetZ = startP.z + (zStep * i);

            pathSegments.Add(new Vector3(randomX, 0f, targetZ));
            sideMultiplier *= -1f;
        }

        pathSegments.Add(endP);
    }

    private void GenerateSmoothPath()
    {
        Vector3 p0 = robot.position;
        Vector3 p3 = targetBall.position;

        pathSegments.Add(p0);

        float currentArenaX = baseArenaSize.x * globalScale;
        float currentSafeRadius = baseSafeRadius * globalScale;
        float safeBoundX = (currentArenaX / 2f) - currentSafeRadius - (0.1f * globalScale);

        bool isSCurve = Random.value > 0.3f;
        float waves = isSCurve ? 2f : 1f;
        float sign = Random.value > 0.5f ? 1f : -1f;

        int steps = 15;
        for (int i = 1; i < steps; i++)
        {
            float t = i / (float)steps;
            Vector3 basePos = Vector3.Lerp(p0, p3, t);
            float sineValue = Mathf.Sin(t * Mathf.PI * waves) * sign;

            float maxOffsetRight = (transform.position.x + safeBoundX) - basePos.x;
            float maxOffsetLeft = basePos.x - (transform.position.x - safeBoundX);

            float offsetX = sineValue > 0 ? sineValue * maxOffsetRight : sineValue * maxOffsetLeft;

            Vector3 point = basePos;
            point.x += offsetX * 0.9f;
            point.y = 0f;

            pathSegments.Add(point);
        }

        pathSegments.Add(p3);
    }

    private bool TryBlockLineOfSight()
    {
        Vector3 startP = robot.position; startP.y = 0f;
        Vector3 endP = targetBall.position; endP.y = 0f;

        Vector3 dir = endP - startP;
        float dist = dir.magnitude;
        dir.Normalize();

        ShuffleArray(baseBoxDimensions);
        Vector3 localScale = new Vector3(
            baseBoxDimensions[0] * globalScale,
            baseBoxDimensions[1] * globalScale,
            baseBoxDimensions[2] * globalScale
        );
        float boxRadius = Mathf.Sqrt(localScale.x * localScale.x + localScale.z * localScale.z) / 2f;

        for (float t = 0.3f; t <= 0.7f; t += 0.05f)
        {
            Vector3 testPos = startP + dir * (dist * t);

            if (IsPositionSafeFromPath(testPos, boxRadius))
            {
                Vector3 boxCenter = testPos + Vector3.up * (localScale.y / 2f);
                Quaternion blockRot = Quaternion.LookRotation(dir) * Quaternion.Euler(0, 90f, 0);

                GameObject newBox = Instantiate(boxPrefab, boxCenter, blockRot, transform);
                newBox.transform.localScale = localScale;
                MarkAsObstacle(newBox);

                if (newBox.TryGetComponent<Rigidbody>(out Rigidbody rb))
                    ConfigureBoxRigidbody(rb);

                activeBoxes.Add(newBox);
                Physics.SyncTransforms();
                return true;
            }
        }
        return false;
    }

    private void SpawnObstaclesTight(int boxesToSpawn)
    {
        int maxAttemptsPerBox = 50;
        float currentSafeRadius = baseSafeRadius * globalScale;
        float currentScatter = scatterMargin * globalScale;

        for (int i = 0; i < boxesToSpawn; i++)
        {
            for (int attempt = 0; attempt < maxAttemptsPerBox; attempt++)
            {
                float fraction = (float)i / Mathf.Max(1, boxesToSpawn - 1);
                float baseT = Mathf.Lerp(0.05f, 0.95f, fraction);
                float t = Mathf.Clamp(baseT + Random.Range(-0.1f, 0.1f), 0.05f, 0.95f);

                float floatIndex = t * (pathSegments.Count - 1);
                int segmentIndex = Mathf.Clamp(Mathf.FloorToInt(floatIndex), 0, pathSegments.Count - 2);
                float localT = floatIndex - segmentIndex;

                Vector3 pA = pathSegments[segmentIndex];
                Vector3 pB = pathSegments[segmentIndex + 1];
                Vector3 pathPoint = Vector3.Lerp(pA, pB, localT);

                Vector3 dir = (pB - pA).normalized;
                if (dir.sqrMagnitude == 0) continue;
                Vector3 normal = new Vector3(-dir.z, 0, dir.x);

                if (Random.value > 0.5f) normal = -normal;

                ShuffleArray(baseBoxDimensions);
                Vector3 localScale = new Vector3(
                    baseBoxDimensions[0] * globalScale,
                    baseBoxDimensions[1] * globalScale,
                    baseBoxDimensions[2] * globalScale
                );

                float boxRadius = Mathf.Sqrt(localScale.x * localScale.x + localScale.z * localScale.z) / 2f;
                float offset = currentSafeRadius + boxRadius + Random.Range(0.02f * globalScale, currentScatter);
                Vector3 testPos = pathPoint + normal * offset;

                float currentArenaX = baseArenaSize.x * globalScale;
                float currentArenaZ = baseArenaSize.y * globalScale;
                Vector3 localPos = transform.InverseTransformPoint(testPos);
                if (Mathf.Abs(localPos.x) > currentArenaX / 2f || Mathf.Abs(localPos.z) > currentArenaZ / 2f)
                    continue;

                if (!IsPositionSafeFromPath(testPos, boxRadius)) continue;

                Vector3 boxCenter = testPos + Vector3.up * (localScale.y / 2f);
                Quaternion randomRot = Quaternion.Euler(0, Random.Range(0f, 360f), 0);

                float physicalGap = 0.05f * globalScale;
                Vector3 checkExtents = (localScale / 2f) + new Vector3(physicalGap, physicalGap, physicalGap);

                if (Physics.CheckBox(boxCenter, checkExtents, randomRot, obstacleMask)) continue;

                GameObject newBox = Instantiate(boxPrefab, boxCenter, randomRot, transform);
                newBox.transform.localScale = localScale;
                MarkAsObstacle(newBox);

                if (newBox.TryGetComponent<Rigidbody>(out Rigidbody rb))
                    ConfigureBoxRigidbody(rb);

                activeBoxes.Add(newBox);
                Physics.SyncTransforms();
                break;
            }
        }
    }

    private bool IsPositionSafeFromPath(Vector3 pos, float boxRadius)
    {
        float currentSafeRadius = baseSafeRadius * globalScale;
        float requiredDist = currentSafeRadius + boxRadius;

        for (int i = 0; i < pathSegments.Count - 1; i++)
        {
            float dist = DistanceToSegment(pos, pathSegments[i], pathSegments[i + 1]);
            if (dist < requiredDist) return false;
        }
        return true;
    }

    private void ConfigureBoxRigidbody(Rigidbody rb)
    {
        rb.mass = randomizeBoxMass
            ? Random.Range(boxMassRange.x, boxMassRange.y)
            : boxMass;
        rb.centerOfMass = new Vector3(0, -0.4f, 0);
        rb.angularDamping = 2f;
        rb.Sleep();
    }

    private void MarkAsObstacle(GameObject obstacle)
    {
        if (obstacle == null || obstacleLayer < 0)
            return;

        obstacle.layer = obstacleLayer;
        foreach (Transform child in obstacle.transform)
            SetLayerRecursively(child, obstacleLayer);
    }

    private static void SetLayerRecursively(Transform target, int layer)
    {
        target.gameObject.layer = layer;
        foreach (Transform child in target)
            SetLayerRecursively(child, layer);
    }

    private float DistanceToSegment(Vector3 point, Vector3 v, Vector3 w)
    {
        float lengthSquared = (w - v).sqrMagnitude;
        if (lengthSquared == 0) return Vector3.Distance(point, v);

        float t = Mathf.Max(0, Mathf.Min(1, Vector3.Dot(point - v, w - v) / lengthSquared));
        Vector3 projection = v + t * (w - v);

        return Vector3.Distance(point, projection);
    }

    private void ShuffleArray(float[] array)
    {
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            float temp = array[i];
            array[i] = array[j];
            array[j] = temp;
        }
    }

    private void OnDrawGizmos()
    {
        float currentArenaX = baseArenaSize.x * globalScale;
        float currentArenaZ = baseArenaSize.y * globalScale;
        float currentSafeRadius = baseSafeRadius * globalScale;

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(transform.position, new Vector3(currentArenaX, 0.1f, currentArenaZ));

        if (pathSegments == null || pathSegments.Count < 2) return;

        Gizmos.color = Color.green;
        for (int i = 0; i < pathSegments.Count - 1; i++)
        {
            Gizmos.DrawLine(pathSegments[i], pathSegments[i + 1]);
        }

        Gizmos.color = new Color(1f, 0f, 0f, 0.2f);
        foreach (Vector3 pt in pathSegments)
        {
            Gizmos.DrawWireSphere(pt, currentSafeRadius);
        }
    }
}
