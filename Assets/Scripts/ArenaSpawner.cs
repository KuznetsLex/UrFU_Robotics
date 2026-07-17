using UnityEngine;

// Размножает одну обучающую площадку (арену) в сетку из нескольких копий
// внутри ОДНОЙ сцены/окна Unity, чтобы ML-Agents могла собирать опыт
// параллельно со всех копий сразу — это ускоряет обучение без запуска
// нескольких билдов через --num-envs.
//
// Исходные объекты арены сначала группируются под временным общим родителем
// и клонируются ОДНИМ вызовом Instantiate() — так аккуратнее ложится сетка
// и проще считать смещения. Но клонирование само по себе НЕ гарантирует,
// что RobotBrain.ball у копии будет указывать на "свой" мяч: в префабе
// робота это поле по умолчанию ссылается на объект внутри Prefab-ассета
// Ball (кросс-префабная ссылка по GUID), а не на конкретный экземпляр
// в сцене. Поэтому после клонирования каждой арены ссылки (ball,
// environmentManager.robot/targetBall) переустанавливаются явно в WireArena() —
// без этого все роботы во всех арендах гонялись бы за одним и тем же
// исходным мячом, а ResetBall() падал бы с ошибкой
// "Transform resides in a Prefab asset and cannot be set".
public class ArenaSpawner : MonoBehaviour
{
    [Header("Шаблон арены (робот, Ball, EnvironmentManager — пол генерируется отдельно)")]
    public Transform[] arenaTemplate;

    [Header("Пол")]
    [Tooltip("Во сколько раз пол должен быть больше стен арены (запас по краям)")]
    public float floorMargin = 1.2f;

    [Header("Настройки размножения")]
    [Tooltip("Сколько всего арен должно быть в сцене (включая исходную)")]
    public int arenaCount = 8;

    [Tooltip("Расстояние между аренами по сетке. Должно быть больше размера одной арены, чтобы копии не пересекались")]
    public float spacing = 100f;

    [Tooltip("Сколько арен размещать в одном ряду сетки")]
    public int arenasPerRow = 4;

    void Awake()
    {
        if (arenaTemplate == null || arenaTemplate.Length == 0)
        {
            Debug.LogWarning("ArenaSpawner: шаблон арены не назначен, размножение пропущено.");
            return;
        }

        // Группируем исходную арену под общим родителем, чтобы удобно клонировать
        // и смещать её как единое целое.
        GameObject arena0Root = new GameObject("Arena_0");
        arena0Root.transform.position = Vector3.zero;
        foreach (Transform member in arenaTemplate)
        {
            if (member != null)
                member.SetParent(arena0Root.transform, true); // worldPositionStays: исходная арена остаётся на месте
        }
        WireArena(arena0Root);

        for (int i = 1; i < arenaCount; i++)
        {
            int row = i / arenasPerRow;
            int col = i % arenasPerRow;
            Vector3 offset = new Vector3(col * spacing, 0f, row * spacing);

            GameObject clone = Instantiate(arena0Root, offset, arena0Root.transform.rotation);
            clone.name = $"Arena_{i}";
            WireArena(clone);
        }
    }

    // Явно связывает робота, мяч и генератор арены внутри ОДНОЙ конкретной копии.
    // См. комментарий в начале файла — без этого шага ссылки, унаследованные из
    // префаба/клонирования, могут указывать не туда.
    private void WireArena(GameObject arenaRoot)
    {
        Transform ball = arenaRoot.transform.Find("Ball");
        RobotBrain brain = arenaRoot.GetComponentInChildren<RobotBrain>();
        EnvironmentManager env = arenaRoot.GetComponentInChildren<EnvironmentManager>();

        if (brain == null)
        {
            Debug.LogWarning($"ArenaSpawner: в {arenaRoot.name} не найден RobotBrain — проверьте Arena Template.");
            return;
        }
        if (ball == null)
            Debug.LogWarning($"ArenaSpawner: в {arenaRoot.name} не найден дочерний объект \"Ball\" — проверьте имя объекта мяча в шаблоне.");
        else
            brain.ball = ball;

        // Та же болезнь, что была у brain.ball: SimulatedYoloCamera.targetBall
        // в префабе робота тоже смотрит на объект внутри Prefab-ассета Ball по
        // GUID, а не на конкретный экземпляр в сцене — без этой строки все
        // роботы во всех арендах "видели" бы один и тот же (чужой) мяч.
        if (brain.yoloCamera != null)
            brain.yoloCamera.targetBall = ball;

        if (env != null)
        {
            env.robot = brain.transform;
            env.targetBall = ball;
            brain.environmentManager = env;
            CreateFloor(env);
        }
    }

    // Слой пола: намеренно НЕ Default. EnvironmentManager.SpawnObstaclesTight()
    // перед установкой каждой коробки делает Physics.CheckBox(...) по obstacleMask
    // (по умолчанию — только слой Default) с небольшим отрицательным запасом вниз
    // (physicalGap), который на масштабе globalScale=10 достигает 0.5 юнита — этого
    // достаточно, чтобы задевать пол на Y=0 и постоянно засчитывать "место занято".
    // Из-за этого реально ставится только первая коробка (та, что блокирует
    // видимость робот-мяч через TryBlockLineOfSight() — там такой проверки нет),
    // а весь остальной boxCount из SpawnObstaclesTight() не находит свободных
    // точек ни за одну из 50 попыток. Меняя сам EnvironmentManager.cs, это не
    // почини — правильно убрать пол из маски проверки, переложив его на
    // отдельный физический слой. Реальная физика (робот/коробки стоят на полу)
    // при этом не страдает — это отдельная физическая матрица столкновений,
    // а не эта точечная проверка в скрипте.
    private const int FloorLayer = 31;

    // Генерирует пол под конкретной ареной вместо клонирования общего Floor-объекта.
    // Раньше стены и коробки (которые EnvironmentManager спавнит на Y=0 относительно
    // своего transform) не совпадали по высоте с реальной поверхностью старого Floor
    // из сцены — из-за этого коробки спавнились над полом и физика долго "досыпала"
    // их вниз. Плоскость встроенного примитива Plane лежит ровно на Y=0 в локальных
    // координатах, поэтому при родителе = transform самого EnvironmentManager она
    // гарантированно совпадает с уровнем, на котором генератор реально строит стены
    // и препятствия.
    private void CreateFloor(EnvironmentManager env)
    {
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "GeneratedFloor";
        floor.layer = FloorLayer;
        floor.transform.SetParent(env.transform, false); // localPosition = (0,0,0) — центр арены

        // Стандартный примитив Plane занимает 10x10 при localScale (1,1,1).
        float sizeX = env.baseArenaSize.x * env.globalScale * floorMargin;
        float sizeZ = env.baseArenaSize.y * env.globalScale * floorMargin;
        floor.transform.localScale = new Vector3(sizeX / 10f, 1f, sizeZ / 10f);
    }
}
