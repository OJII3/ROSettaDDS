using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace ROSettaDDS.EditorTools
{
    public static class ROSettaDDSPerfPlayerBuilder
    {
        public static void PrintUsage()
        {
            Debug.Log(
                "ROSettaDDSPerfPlayerBuilder.BuildPlayer(path, target, backend)");
        }

        public static void Build()
        {
            try
            {
                Dictionary<string, string> args = ParseCommandLine(Environment.GetCommandLineArgs());
                string buildPath = Require(args, "--rosettadds-perf-build-path");
                string targetText = Require(args, "--rosettadds-perf-build-target");
                string backendText = Require(args, "--rosettadds-perf-backend");

                BuildPlayer(buildPath, targetText, backendText);
            }
            catch (Exception ex)
            {
                Debug.LogError(ex);
                EditorApplication.Exit(1);
            }
        }

        public static void BuildPlayer(string buildPath, string targetText, string backendText)
        {
            BuildTarget target = ParseBuildTarget(targetText);
            ScriptingImplementation backend = ParseBackend(backendText);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);

            string directory = Path.GetDirectoryName(buildPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new BuildPlayerOptions
            {
                scenes = EnabledScenes(),
                target = target,
                locationPathName = buildPath,
                options = BuildOptions.Development,
            };

            BuildReportOrThrow(options);
        }

        private static void BuildReportOrThrow(BuildPlayerOptions options)
        {
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "Perf Player build failed: " + report.summary.result +
                    " errors=" + report.summary.totalErrors);
            }
            Debug.Log("Perf Player build succeeded: " + options.locationPathName);
        }

        private static string[] EnabledScenes()
        {
            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length > 0)
            {
                return scenes;
            }
            return new[] { "Assets/Scenes/SampleScene.unity" };
        }

        private static Dictionary<string, string> ParseCommandLine(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (!arg.StartsWith("--rosettadds-perf-", StringComparison.Ordinal))
                {
                    continue;
                }
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("missing value for " + arg);
                }
                values[arg] = args[++i];
            }
            return values;
        }

        private static string Require(Dictionary<string, string> args, string key)
        {
            if (!args.TryGetValue(key, out string value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(key + " is required");
            }
            return value;
        }

        private static BuildTarget ParseBuildTarget(string value)
        {
            if (value == "StandaloneLinux64")
            {
                return BuildTarget.StandaloneLinux64;
            }
            if (value == "StandaloneOSX")
            {
                return BuildTarget.StandaloneOSX;
            }
            throw new ArgumentException("--rosettadds-perf-build-target must be StandaloneLinux64 or StandaloneOSX");
        }

        private static ScriptingImplementation ParseBackend(string value)
        {
            if (value == "il2cpp")
            {
                return ScriptingImplementation.IL2CPP;
            }
            if (value == "mono")
            {
                return ScriptingImplementation.Mono2x;
            }
            throw new ArgumentException("--rosettadds-perf-backend must be il2cpp or mono");
        }
    }
}
