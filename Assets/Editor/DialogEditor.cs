#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(Dialogues))]
public class DialoguesDrawer : PropertyDrawer
{
    static Type[] _types;
    static Type[] Types => _types ??= TypeCache.GetTypesDerivedFrom<DialoguesAsset>()
        .Where(t => !t.IsAbstract && !t.IsGenericType)
        .Prepend(typeof(DialoguesAsset)).ToArray();

    const float Line = 18f, Gap = 2f;

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float h = Line;
        if (!property.isExpanded) return h;

        h += (Line + Gap) * 2;                             // name field + CanLoop
        var arr = property.FindPropertyRelative("dialog");
        for (int i = 0; i < arr.arraySize; i++)
        {
            var el = arr.GetArrayElementAtIndex(i);
            h += Line + Gap;                               // header row
            if (el.managedReferenceValue != null)
                h += EditorGUI.GetPropertyHeight(el, true) + Gap;
        }
        return h + Line + Gap;                             // Add Line button
    }

    public override void OnGUI(Rect pos, SerializedProperty property, GUIContent label)
    {
        var nameProp = property.FindPropertyRelative("DialogueName");
        var loopProp = property.FindPropertyRelative("CanLoop");
        var arr = property.FindPropertyRelative("dialog");
        var so = property.serializedObject;
        string path = property.propertyPath;

        var r = new Rect(pos.x, pos.y, pos.width, Line);
        property.isExpanded = EditorGUI.Foldout(r, property.isExpanded,
            $"{nameProp.stringValue}  ({arr.arraySize} lines)", true);
        if (!property.isExpanded) return;

        EditorGUI.indentLevel++;

        r.y += Line + Gap;
        EditorGUI.PropertyField(r, nameProp);

        r.y += Line + Gap;
        EditorGUI.PropertyField(r, loopProp);

        int removeAt = -1, fixAt = -1;
        for (int i = 0; i < arr.arraySize; i++)
        {
            var el = arr.GetArrayElementAtIndex(i);
            bool isNull = el.managedReferenceValue == null;
            string typeName = isNull ? "NULL" : el.managedReferenceValue.GetType().Name;

            r.y += Line + Gap;
            var header = new Rect(r.x, r.y, r.width - 70, Line);
            EditorGUI.LabelField(header, $"Line {i}  [{typeName}]", EditorStyles.boldLabel);
            if (isNull && GUI.Button(new Rect(r.xMax - 140, r.y, 66, Line), "Fix")) fixAt = i;
            if (GUI.Button(new Rect(r.xMax - 70, r.y, 70, Line), "Remove")) removeAt = i;

            if (!isNull)
            {
                float h = EditorGUI.GetPropertyHeight(el, true);
                r.y += Line + Gap;
                EditorGUI.PropertyField(new Rect(r.x, r.y, r.width, h), el, GUIContent.none, true);
                r.y += h - Line;
            }
        }

        r.y += Line + Gap;
        if (GUI.Button(new Rect(r.x, r.y, r.width, Line), "Add Line ▾"))
        {
            var menu = new GenericMenu();
            foreach (var t in Types)
            {
                var type = t;
                menu.AddItem(new GUIContent(type.Name), false, () =>
                {
                    so.Update();
                    var a = so.FindProperty(path).FindPropertyRelative("dialog");
                    a.arraySize++;
                    a.GetArrayElementAtIndex(a.arraySize - 1).managedReferenceValue = Activator.CreateInstance(type);
                    so.ApplyModifiedProperties();
                });
            }
            menu.ShowAsContext();
        }
        EditorGUI.indentLevel--;

        if (fixAt >= 0)
        {
            arr.GetArrayElementAtIndex(fixAt).managedReferenceValue = new DialoguesAsset();
            so.ApplyModifiedProperties();
            GUIUtility.ExitGUI();
        }
        if (removeAt >= 0)
        {
            arr.DeleteArrayElementAtIndex(removeAt);
            so.ApplyModifiedProperties();
            GUIUtility.ExitGUI();
        }
    }
}
#endif