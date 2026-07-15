using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Слайдер в инспекторе, границы которого берутся из значений других полей
// того же компонента (в отличие от [Range], которое требует constant-выражений).
public class DynamicRangeAttribute : PropertyAttribute
{
    public readonly string minFieldName;
    public readonly string maxFieldName;

    public DynamicRangeAttribute(string minFieldName, string maxFieldName)
    {
        this.minFieldName = minFieldName;
        this.maxFieldName = maxFieldName;
    }
}

#if UNITY_EDITOR
[CustomPropertyDrawer(typeof(DynamicRangeAttribute))]
public class DynamicRangeDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        var range = (DynamicRangeAttribute)attribute;
        SerializedObject so = property.serializedObject;

        SerializedProperty minProp = so.FindProperty(range.minFieldName);
        SerializedProperty maxProp = so.FindProperty(range.maxFieldName);

        if (minProp == null || maxProp == null || property.propertyType != SerializedPropertyType.Float)
        {
            EditorGUI.PropertyField(position, property, label);
            return;
        }

        float min = minProp.floatValue;
        float max = maxProp.floatValue;

        EditorGUI.BeginChangeCheck();
        float newValue = EditorGUI.Slider(position, label, property.floatValue, min, max);
        if (EditorGUI.EndChangeCheck())
        {
            property.floatValue = newValue;
        }
    }
}
#endif
