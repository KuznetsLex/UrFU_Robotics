using UnityEngine;

public class RandomSpawner : MonoBehaviour
{
    [Header("Параметры спавна")]
    public Vector3 center = Vector3.zero;   // центр круга (обычно (0, 0, 0))
    public float radius = 10f;              // радиус круга
    public float height = 0f;               // высота (Y) появления (если поле на уровне земли)
    public GameObject objectToSpawn;        // префаб или объект для спавна

    [Header("Настройки")]
    public bool spawnOnStart = true;        // спавнить при старте?

    void Start()
    {
        if (spawnOnStart && objectToSpawn != null)
        {
            SpawnRandom();
        }
    }

    // Вызов извне (по кнопке, событию и т.д.)
    public void SpawnRandom()
    {
        // Генерируем случайную точку в круге радиусом 1
        Vector2 randomCircle = Random.insideUnitCircle * radius;

        // Преобразуем в 3D координаты (XZ) с фиксированной высотой Y
        Vector3 spawnPosition = new Vector3(
            center.x + randomCircle.x,
            height,
            center.z + randomCircle.y
        );

        // Создаём объект
        Instantiate(objectToSpawn, spawnPosition, Quaternion.identity);
    }

    // Для визуализации радиуса в редакторе (без запуска)
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(center, radius);
    }
}