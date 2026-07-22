using UnityEngine;
using System.IO;
using System.Text;
using System.Globalization; // Обязательно для InvariantCulture!

public class DiagnosticLogger : MonoBehaviour
{
    public bool enableLogging = false;  // Флаг включения записи логов
    public int logEveryN = 1;           // Записывать каждый N-й шаг (1 = каждый)
    public int maxRows = 2000;          // Ограничение размера файла (строк)

    [Tooltip("Сколько строк накапливать перед сбросом на диск. При логировании " +
        "одновременно многих арен частый Flush() на каждый шаг создаёт заметную " +
        "нагрузку на диск — увеличьте, если несколько DiagnosticLogger работают " +
        "параллельно.")]
    public int flushEveryNRows = 20;

    [Tooltip("Дополнительно сбрасывать буфер на диск не реже чем раз в столько " +
        "секунд, даже если flushEveryNRows строк ещё не накопилось — иначе " +
        "короткий/прерванный прогон (обучение остановили раньше, чем набрался " +
        "батч) не оставит на диске вообще ничего.")]
    public float flushEverySeconds = 2f;

    // Счётчик для уникального имени файла у роботов без арены (arenaIndex == -1) —
    // GetInstanceID()/GetEntityId() тут не нужны, простой статический счётчик проще.
    private static int nextFallbackId = 0;

    // Имя аргумента командной строки, которым Python-сторона (mlagents-learn)
    // сообщает билду, куда писать логи — см. ResolveCsvLogDir().
    private const string ResultsDirArgName = "--diagLogDir";

    // Присутствие этого аргумента (см. tools/run_training.sh) означает, что
    // mlagents-learn запущен с --resume — тогда CSV нужно дописывать, а не
    // перезатирать, как при обычном --force. Просто флаг, без значения.
    private const string ResumeArgName = "--resume";

    // Кэшируем на весь процесс: аргументы командной строки одни и те же для
    // всех экземпляров DiagnosticLogger, нет смысла парсить их каждый раз.
    private static string cachedCsvLogDir;

    private StreamWriter writer;
    private int rowsWritten = 0;
    private int rowsSinceFlush = 0;
    private float startTime;
    private float lastFlushTime;
    private int stepsSeen = 0;
    private int arenaIndex = -1;
    private bool opened = false;
    private bool openFailed = false;

    // Вызывается владельцем (RobotBrain.Initialize()) как можно раньше — здесь
    // только запоминаем номер арены, без файлового ввода-вывода. Сам файл
    // открывается лениво в EnsureWriterOpen() при первом LogStep(), а не в
    // Initialize()/Start(): открытие сразу нескольких десятков файлов синхронно
    // в Initialize() (общая точка для ML-Agents между всеми агентами разом)
    // на практике ломало симуляцию при логировании многих арен одновременно.
    public void SetArenaIndex(int index)
    {
        arenaIndex = index;
    }

    // mlagents-learn создаёт results/<run-id>/ на Python-стороне и ничего об этом
    // билду не сообщает — сам Unity-плеер понятия не имеет ни про run-id, ни про
    // эту папку. Поэтому путь нужно передавать явно через --env-args, например:
    //   mlagents-learn --run-id=test --env=Build/YandexCamp.app ... \
    //     --env-args --diagLogDir results/test
    // Без --env-args (запуск из редактора, Heuristic-тесты) используем корень
    // проекта — тот же фолбэк, что был раньше.
    private static string ResolveCsvLogDir()
    {
        if (cachedCsvLogDir != null)
            return cachedCsvLogDir;

        string[] args = System.Environment.GetCommandLineArgs();
        string baseDir = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == ResultsDirArgName)
            {
                baseDir = Path.GetFullPath(args[i + 1]);
                break;
            }
        }

        if (baseDir == null)
            baseDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        cachedCsvLogDir = Path.Combine(baseDir, "csv_logs");
        Directory.CreateDirectory(cachedCsvLogDir);
        return cachedCsvLogDir;
    }

    private static bool ResolveResumeFlag()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        foreach (string arg in args)
        {
            if (arg == ResumeArgName)
                return true;
        }
        return false;
    }

    private void EnsureWriterOpen()
    {
        if (opened || openFailed || !enableLogging) return;
        opened = true;

        try
        {
            // Без ArenaSpawner (arenaIndex == -1) может существовать больше одного
            // такого робота одновременно (например SensorTestSceneSetup) — общее
            // имя файла для всех них даёт "Sharing violation", т.к. второй писатель
            // не может открыть уже занятый первым файл. Различаем счётчиком экземпляров.
            string fileName = arenaIndex >= 0
                ? $"diagnostic_log_arena{arenaIndex}.csv"
                : $"diagnostic_log_{nextFallbackId++}.csv";
            string path = Path.Combine(ResolveCsvLogDir(), fileName);

            // При --resume (см. tools/run_training.sh) дописываем в уже существующий
            // файл, а не перезатираем его, как при обычном --force — иначе история
            // CSV из прошлого прогона терялась бы при каждом продолжении обучения.
            // Заголовок в этом случае не дублируем: он уже есть в существующем файле.
            bool append = ResolveResumeFlag() && File.Exists(path);
            writer = new StreamWriter(path, append, Encoding.UTF8);

            if (!append)
            {
                // Записываем заголовок колонок CSV (строго в одну строчку без пробелов)
                writer.WriteLine("time,step,arena,ballSeen,ballAngle,ballDist,uz,irL,irR,gripIR,camYaw,gas,steering,hasBall,displacementX,displacementZ,heading,speed");
            }
            // Сразу на диск: без этого при коротком/прерванном прогоне (например,
            // обучение остановили раньше, чем накопилось flushEveryNRows строк, а
            // OnDestroy() не успел отработать) файл остаётся полностью пустым —
            // даже заголовка не будет видно.
            writer.Flush();

            startTime = Time.time;
            lastFlushTime = Time.realtimeSinceStartup;
            Debug.Log($"[DiagnosticLogger] Запись лога запущена в: {path}");
        }
        catch (IOException ex)
        {
            openFailed = true;
            Debug.LogError($"[DiagnosticLogger] Не удалось открыть файл лога (арена {arenaIndex}): {ex.Message}", this);
        }
    }

    // Закрываем файл при уничтожении объекта (например, при выходе из игры).
    // Close() сам сбрасывает буфер, так что несохранённых строк не остаётся.
    void OnDestroy()
    {
        writer?.Close();
    }

    public void LogStep(
        int step, int arena, bool ballSeen, float ballAngle, float ballDist,
        float uz, int irL, int irR, int gripIR, float camYaw,
        float gas, float steering, bool hasBall,
        float displacementX, float displacementZ,
        float heading, float speed)
    {
        // Защита от переполнения файла
        if (!enableLogging || openFailed || rowsWritten >= maxRows) return;

        EnsureWriterOpen();
        if (writer == null) return;

        stepsSeen++;
        if (logEveryN > 1 && stepsSeen % logEveryN != 0) return;

        float elapsed = Time.time - startTime;

        // Сборка строки с принудительным использованием CultureInfo.InvariantCulture
        string line = string.Format(CultureInfo.InvariantCulture,
            "{0:F3},{1},{2},{3},{4:F4},{5:F4},{6:F4},{7},{8},{9},{10:F4},{11:F4},{12:F4},{13},{14:F4},{15:F4},{16:F4},{17:F4}",
            elapsed, step, arena, ballSeen ? 1 : 0, ballAngle, ballDist, uz, irL, irR, gripIR, camYaw,
            gas, steering, hasBall ? 1 : 0,
            displacementX, displacementZ, heading, speed);

        try
        {
            writer.WriteLine(line);
            rowsWritten++;
            rowsSinceFlush++;

            // Сбрасываем буфер не на каждой строке, а пачками — иначе логирование
            // нескольких арен одновременно означает Flush() на диск каждый шаг
            // физики × число арен, что и создавало нагрузку, похожую на зависание.
            // Дополнительно — по времени: короткий/прерванный прогон иначе может
            // не дотянуть даже до одного flushEveryNRows-батча и не оставить на
            // диске ни строки, несмотря на то что LogStep() уже вызывался.
            bool dueByRows = rowsSinceFlush >= flushEveryNRows;
            bool dueByTime = Time.realtimeSinceStartup - lastFlushTime >= flushEverySeconds;
            if (dueByRows || dueByTime || rowsWritten >= maxRows)
            {
                writer.Flush();
                rowsSinceFlush = 0;
                lastFlushTime = Time.realtimeSinceStartup;
            }

            if (rowsWritten >= maxRows)
            {
                Debug.Log($"[DiagnosticLogger] Сбор лога завершен. Достигнут лимит {maxRows} строк.");
            }
        }
        catch (IOException ex)
        {
            openFailed = true;
            Debug.LogError($"[DiagnosticLogger] Ошибка записи лога (арена {arenaIndex}): {ex.Message}", this);
        }
    }
}
