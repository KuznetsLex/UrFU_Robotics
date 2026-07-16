using UnityEditor;
using UnityEngine;

namespace Team11.Ros.Editor
{
    [CustomEditor(typeof(RobotRosServoControl))]
    public sealed class RobotRosServoControlEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script");
            serializedObject.ApplyModifiedProperties();

            var controller = (RobotRosServoControl)target;

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Reset targets"))
                {
                    Undo.RecordObject(controller, "Reset servo targets");
                    controller.ResetTargets();
                    EditorUtility.SetDirty(controller);
                }

                if (GUILayout.Button("Apply to robot"))
                {
                    controller.PublishTargetAngles();
                }

                EditorGUILayout.EndHorizontal();
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to send servo commands to the physical robot.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(controller.Status, MessageType.None);
            }
        }
    }
}
