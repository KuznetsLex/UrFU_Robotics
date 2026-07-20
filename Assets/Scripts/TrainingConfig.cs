using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

// Читает Assets/StreamingAssets/training_config.yaml и переопределяет публичные
// поля компонентов значениями оттуда — чтобы менять параметры обучения (награды,
// размер арены, число коробок и т.д.) правкой текстового файла рядом со сборкой,
// а не значениями в инспекторе, которые требуют пересобрать билд для headless-
// обучения (mlagents-learn --env=Build.exe). Формат — простое подмножество YAML
// (секция: имя класса, ключ — имя публичного поля), без внешних библиотек:
//
//   RobotBrain:
//     gripReward: 1.0
//     regenerateEveryEpisodes: 10
//   EnvironmentManager:
//     boxCount: 10
//     baseArenaSize: [3, 6]
//
// Поддерживаются float/int/bool/string/Vector2. Если файла нет или в нём нет
// нужной секции/ключа — поле остаётся тем, что задано в инспекторе (файл
// полностью опционален и покрывает только то, что реально хотите переопределить).
public static class TrainingConfig
{
    private const string ConfigFileName = "training_config.yaml";

    private static bool attemptedLoad;
    private static Dictionary<string, Dictionary<string, string>> sections;

    // Переустанавливает значения полей target из секции sectionName конфига.
    // Вызывайте из Awake() каждого компонента, чьи публичные поля должны быть
    // переопределяемыми — до того, как эти поля используются.
    public static void ApplyOverrides(object target, string sectionName)
    {
        EnsureLoaded();
        if (sections == null || !sections.TryGetValue(sectionName, out Dictionary<string, string> values))
            return;

        Type type = target.GetType();
        foreach (KeyValuePair<string, string> entry in values)
        {
            FieldInfo field = type.GetField(entry.Key, BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                Debug.LogWarning($"TrainingConfig: в {sectionName} нет публичного поля \"{entry.Key}\" — проверьте опечатку в {ConfigFileName}.");
                continue;
            }

            object converted = ConvertValue(entry.Value, field.FieldType);
            if (converted != null)
                field.SetValue(target, converted);
        }
    }

    private static void EnsureLoaded()
    {
        if (attemptedLoad)
            return;
        attemptedLoad = true;

        string path = Path.Combine(Application.streamingAssetsPath, ConfigFileName);
        if (!File.Exists(path))
        {
            Debug.Log($"TrainingConfig: файл {path} не найден — используются значения из инспектора.");
            return;
        }

        try
        {
            sections = ParseYaml(File.ReadAllLines(path));
            Debug.Log($"TrainingConfig: загружен {path} ({sections.Count} секций).");
        }
        catch (Exception exception)
        {
            Debug.LogError($"TrainingConfig: ошибка чтения {path}: {exception.Message}");
            sections = null;
        }
    }

    // Минимальный парсер YAML-подмножества: секции без отступа ("Имя:"),
    // ключи с отступом ("  ключ: значение"), "#" — комментарий до конца строки.
    // Никакой вложенности глубже одного уровня и списков (кроме "[a, b]" —
    // это разбирается уже в ConvertValue, для парсера это просто строка).
    private static Dictionary<string, Dictionary<string, string>> ParseYaml(string[] lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        string currentSection = null;

        foreach (string rawLine in lines)
        {
            string withoutComment = StripComment(rawLine);
            string trimmed = withoutComment.Trim();
            if (trimmed.Length == 0)
                continue;

            int indent = withoutComment.Length - withoutComment.TrimStart().Length;
            if (indent == 0)
            {
                currentSection = trimmed.TrimEnd(':').Trim();
                if (!result.ContainsKey(currentSection))
                    result[currentSection] = new Dictionary<string, string>();
                continue;
            }

            if (currentSection == null)
                continue;

            int colonIndex = trimmed.IndexOf(':');
            if (colonIndex < 0)
                continue;

            string key = trimmed.Substring(0, colonIndex).Trim();
            string value = trimmed.Substring(colonIndex + 1).Trim();
            value = StripQuotes(value);
            result[currentSection][key] = value;
        }

        return result;
    }

    private static string StripComment(string line)
    {
        int hashIndex = line.IndexOf('#');
        return hashIndex >= 0 ? line.Substring(0, hashIndex) : line;
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[value.Length - 1] == '"') ||
             (value[0] == '\'' && value[value.Length - 1] == '\'')))
        {
            return value.Substring(1, value.Length - 2);
        }

        return value;
    }

    private static object ConvertValue(string raw, Type fieldType)
    {
        try
        {
            if (fieldType == typeof(float))
                return float.Parse(raw, CultureInfo.InvariantCulture);
            if (fieldType == typeof(int))
                return int.Parse(raw, CultureInfo.InvariantCulture);
            if (fieldType == typeof(bool))
                return bool.Parse(raw);
            if (fieldType == typeof(string))
                return raw;
            if (fieldType == typeof(Vector2))
            {
                string[] parts = raw.Trim('[', ']', ' ').Split(',');
                if (parts.Length == 2)
                {
                    return new Vector2(
                        float.Parse(parts[0], CultureInfo.InvariantCulture),
                        float.Parse(parts[1], CultureInfo.InvariantCulture));
                }
            }
        }
        catch (Exception)
        {
            Debug.LogWarning($"TrainingConfig: не удалось преобразовать значение \"{raw}\" в тип {fieldType.Name}.");
            return null;
        }

        Debug.LogWarning($"TrainingConfig: тип поля {fieldType.Name} не поддерживается конфигом (значение \"{raw}\" проигнорировано).");
        return null;
    }
}
