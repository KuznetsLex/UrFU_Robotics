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

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Physical servo angles are configured on the Raspberry Pi. " +
                    "Unity publishes only semantic gripper and normalized camera commands.",
                    MessageType.Info);
            }
            else
            {
                var controller = (RobotRosServoControl)target;
                EditorGUILayout.HelpBox(controller.Status, MessageType.None);
            }
        }
    }
}
