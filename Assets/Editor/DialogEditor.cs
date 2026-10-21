using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for DialoguesDatabase (put inside an "Editor" folder).
/// Lines are stored with [SerializeReference] so each line can be a different subclass of DialoguesAsset.
/// Unity's array "+" can't choose a type, so use the "Add Line ▾" button of each dialogue instead.
/// </summary>
[CustomEditor(typeof(DialoguesDatabase))]
public class DialoguesDatabaseEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        serializedObject.Update();
        SerializedProperty list = serializedObject.FindProperty("dialogues");

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Quick add (typed lines)", EditorStyles.boldLabel);

        for (int i = 0; i < list.arraySize; i++)
        {
            SerializedProperty chapter = list.GetArrayElementAtIndex(i);
            string title = chapter.FindPropertyRelative("DialogueName").stringValue;
            string linesPath = chapter.FindPropertyRelative("dialog").propertyPath;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(string.IsNullOrEmpty(title) ? $"(dialogue {i})" : title);
            if (GUILayout.Button("Add Line ▾", GUILayout.Width(90))) ShowTypeMenu(linesPath);
            if (GUILayout.Button("Clean nulls", GUILayout.Width(80))) RemoveNulls(linesPath);
            EditorGUILayout.EndHorizontal();
        }

        if (serializedObject.ApplyModifiedProperties())
            ((DialoguesDatabase)target).InvalidateLookup();
    }

    private void ShowTypeMenu(string linesPath)
    {
        var types = new List<Type> { typeof(DialoguesAsset) };
        foreach (Type t in TypeCache.GetTypesDerivedFrom<DialoguesAsset>())
            if (!t.IsAbstract && !t.IsGenericType) types.Add(t);

        var menu = new GenericMenu();
        foreach (Type t in types)
        {
            Type captured = t;
            menu.AddItem(new GUIContent(t.Name), false, () => AddLine(linesPath, captured));
        }
        menu.ShowAsContext();
    }

    private void AddLine(string linesPath, Type type)
    {
        serializedObject.Update();
        SerializedProperty lines = serializedObject.FindProperty(linesPath);
        if (lines == null) return;

        lines.arraySize++;
        lines.GetArrayElementAtIndex(lines.arraySize - 1).managedReferenceValue = Activator.CreateInstance(type);
        serializedObject.ApplyModifiedProperties();
        ((DialoguesDatabase)target).InvalidateLookup();
    }

    private void RemoveNulls(string linesPath)
    {
        SerializedProperty lines = serializedObject.FindProperty(linesPath);
        if (lines == null) return;

        for (int i = lines.arraySize - 1; i >= 0; i--)
            if (lines.GetArrayElementAtIndex(i).managedReferenceValue == null)
                lines.DeleteArrayElementAtIndex(i);
    }
}