using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public sealed class RayTracingSceneGeneratorCreator : EditorWindow
{
    private const string CustomGeneratorFolder = "Assets/Editor";
    private const string RegistryPath = "Assets/Editor/CustomSceneGenerator.cs";
    private const string CallMarker = "        // New scene generator calls are inserted above this line.";

    private static readonly HashSet<string> CSharpKeywords = new HashSet<string>
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while"
    };

    private string _className = "MySceneGenerator";

    [MenuItem("Tools/Ray Tracing/Generate a New Scene Generator", priority = 201)]
    public static void ShowWindow()
    {
        var window = CreateInstance<RayTracingSceneGeneratorCreator>();
        window.titleContent = new GUIContent("New Scene Generator");
        window.minSize = new Vector2(420.0f, 105.0f);
        window.maxSize = new Vector2(420.0f, 105.0f);
        window.ShowUtility();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Create a custom scene generator class", EditorStyles.boldLabel);
        EditorGUILayout.Space();
        GUI.SetNextControlName("ClassName");
        _className = EditorGUILayout.TextField("Class name", _className);
        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_className)))
        {
            if (GUILayout.Button("Create Scene Generator"))
            {
                CreateSceneGenerator(_className.Trim());
            }
        }
    }

    private void OnFocus()
    {
        EditorGUI.FocusTextInControl("ClassName");
    }

    private void CreateSceneGenerator(string className)
    {
        if (!IsValidIdentifier(className))
        {
            EditorUtility.DisplayDialog(
                "Invalid Class Name",
                $"'{className}' is not a valid C# class name. Use letters, digits, and underscores, and do not start with a digit.",
                "OK");
            return;
        }

        string generatorPath = $"{CustomGeneratorFolder}/{className}.cs";
        if (File.Exists(generatorPath))
        {
            EditorUtility.DisplayDialog("Generator Already Exists", $"A script already exists at {generatorPath}.", "OK");
            return;
        }

        string registrySource = File.ReadAllText(RegistryPath);
        if (!registrySource.Contains(CallMarker))
        {
            EditorUtility.DisplayDialog("Insertion Marker Missing", $"Could not find the generator insertion marker in {RegistryPath}.", "OK");
            return;
        }
        if (registrySource.Contains($"{className}.CreateScene();"))
        {
            EditorUtility.DisplayDialog("Generator Already Registered", $"{className} is already registered in {RegistryPath}.", "OK");
            return;
        }

        string sceneName = GetSceneName(className);
        File.WriteAllText(generatorPath, BuildGeneratorSource(className, sceneName));
        registrySource = registrySource.Replace(CallMarker, $"        {className}.CreateScene();\n{CallMarker}");
        File.WriteAllText(RegistryPath, registrySource);
        AssetDatabase.Refresh();

        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(generatorPath);
        Selection.activeObject = script;
        EditorGUIUtility.PingObject(script);
        Debug.Log($"Created {className} at {generatorPath} and registered it in {RegistryPath}.");
        Close();
    }

    internal static string BuildGeneratorSource(string className, string sceneName)
    {
        var source = new StringBuilder();
        source.AppendLine("using UnityEngine;");
        source.AppendLine();
        source.AppendLine($"public static class {className}");
        source.AppendLine("{");
        source.AppendLine("    public static void CreateScene()");
        source.AppendLine("    {");
        source.AppendLine($"        const string sceneName = \"{sceneName}\";");
        source.AppendLine("        if (RayTracingSceneGenerator.ShouldSkipExistingScene(sceneName))");
        source.AppendLine("        {");
        source.AppendLine("            return;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        var context = RayTracingSceneGenerator.CreateBaseScene(new SceneSettings");
        source.AppendLine("        {");
        source.AppendLine("            SceneName = sceneName,");
        source.AppendLine("            CameraPosition = new Vector3(0.0f, 1.0f, -10.0f),");
        source.AppendLine("            FieldOfView = 60.0f,");
        source.AppendLine("            NumBounces = 6,");
        source.AppendLine("            DirectionalLightIntensity = 1.0f");
        source.AppendLine("        });");
        source.AppendLine();
        source.AppendLine("        // Add scene objects below context.Root, then save the generated scene.");
        source.AppendLine("        RayTracingSceneGenerator.Save(context.Scene, sceneName);");
        source.AppendLine("    }");
        source.AppendLine("}");
        return source.ToString();
    }

    private static string GetSceneName(string className)
    {
        const string generatorSuffix = "Generator";
        return className.EndsWith(generatorSuffix, System.StringComparison.Ordinal) && className.Length > generatorSuffix.Length
            ? className.Substring(0, className.Length - generatorSuffix.Length)
            : className;
    }

    private static bool IsValidIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || CSharpKeywords.Contains(value) || (!char.IsLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsLetterOrDigit(value[index]) && value[index] != '_')
            {
                return false;
            }
        }

        return true;
    }
}
