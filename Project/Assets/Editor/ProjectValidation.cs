using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ClientMcp.Development
{
    // Development project only. This file is not distributed inside the UPM package.
    public static class ProjectValidation
    {
        public static void Bootstrap()
        {
            PlayerSettings.companyName = "Counull";
            PlayerSettings.productName = "ClientMCP";
            EditorSettings.serializationMode = SerializationMode.ForceText;

            const string scenePath = "Assets/Scenes/DiagnosticsSandbox.unity";
            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                if (!EditorSceneManager.SaveScene(scene, scenePath))
                    throw new InvalidOperationException("Failed to save the diagnostic sandbox scene.");
            }

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
            AssetDatabase.SaveAssets();
            ValidatePackage();
        }

        [MenuItem("ClientMCP/Validate Package Registration")]
        public static void ValidatePackage()
        {
            var package = PackageInfo.GetAllRegisteredPackages()
                .SingleOrDefault(item => item.name == "com.counull.clientmcp");
            if (package == null)
                throw new InvalidOperationException("ClientMCP package is not registered.");

            if (!CompilationPipeline.GetAssemblies(AssembliesType.Player)
                .Any(assembly => assembly.name == "ClientMcp.Runtime"))
                throw new InvalidOperationException("ClientMcp.Runtime is missing from player assemblies.");

            Debug.Log($"CLIENTMCP_VALIDATION_OK package={package.name} version={package.version} source={package.source} unity={Application.unityVersion}");
        }
    }
}
