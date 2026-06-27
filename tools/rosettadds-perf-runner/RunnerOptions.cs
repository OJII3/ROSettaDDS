using System.Runtime.InteropServices;

namespace ROSettaDDS.PerfRunner;

internal sealed class RunnerOptions
{
    internal string Backend { get; private set; } = "il2cpp";
    internal string BuildTarget { get; private set; } = DefaultBuildTarget();
    internal string Scenario { get; private set; } = "all";
    internal string? Helper { get; private set; }
    internal string? PlayerBuild { get; private set; }
    internal string Artifacts { get; private set; } = Path.Combine("artifacts", "perf");
    internal int CaptureFrames { get; private set; } = 1200;
    internal long ProfilerMemory { get; private set; } = 256L * 1024L * 1024L;
    internal string ProfilerMode { get; private set; } = "lean";
    internal bool SkipBuild { get; private set; }
    internal bool Help { get; private set; }
    internal string Adb { get; private set; } = "adb";
    internal string? AndroidDevice { get; private set; }
    internal string AndroidPackage { get; private set; } = "com.ojii3.rosettadds.perf";
    internal string AndroidActivity { get; private set; } = "com.unity3d.player.UnityPlayerGameActivity";

    internal static RunnerOptions Parse(string[] args)
    {
        var options = new RunnerOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    options.Help = true;
                    break;
                case "--backend":
                    options.Backend = RequireValue(args, ref i, arg);
                    break;
                case "--build-target":
                    options.BuildTarget = RequireValue(args, ref i, arg);
                    break;
                case "--scenario":
                    options.Scenario = RequireValue(args, ref i, arg);
                    break;
                case "--helper":
                    options.Helper = RequireValue(args, ref i, arg);
                    break;
                case "--player-build":
                    options.PlayerBuild = RequireValue(args, ref i, arg);
                    break;
                case "--artifacts":
                    options.Artifacts = RequireValue(args, ref i, arg);
                    break;
                case "--capture-frames":
                    options.CaptureFrames = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                    break;
                case "--profiler-memory":
                    options.ProfilerMemory = ParsePositiveLong(RequireValue(args, ref i, arg), arg);
                    break;
                case "--profiler-mode":
                    options.ProfilerMode = RequireValue(args, ref i, arg);
                    if (options.ProfilerMode != "lean" && options.ProfilerMode != "full")
                    {
                        throw new ArgumentException("--profiler-mode must be lean or full");
                    }
                    break;
                case "--skip-build":
                    options.SkipBuild = true;
                    break;
                case "--adb":
                    options.Adb = RequireValue(args, ref i, arg);
                    break;
                case "--android-device":
                    options.AndroidDevice = RequireValue(args, ref i, arg);
                    break;
                case "--android-package":
                    options.AndroidPackage = RequireValue(args, ref i, arg);
                    break;
                case "--android-activity":
                    options.AndroidActivity = RequireValue(args, ref i, arg);
                    break;
                default:
                    throw new ArgumentException("unknown argument: " + arg);
            }
        }

        if (options.Backend != "il2cpp" && options.Backend != "mono")
        {
            throw new ArgumentException("--backend must be il2cpp or mono");
        }
        if (options.BuildTarget != "StandaloneLinux64"
            && options.BuildTarget != "StandaloneOSX"
            && options.BuildTarget != "Android")
        {
            throw new ArgumentException(
                "--build-target must be StandaloneLinux64, StandaloneOSX, or Android");
        }
        if (options.SkipBuild && string.IsNullOrWhiteSpace(options.PlayerBuild))
        {
            throw new ArgumentException("--player-build is required with --skip-build");
        }
        _ = PerfScenario.Select(options.Scenario);
        return options;
    }

    internal static void PrintHelp(TextWriter output)
    {
        output.WriteLine("Usage: dotnet run --project tools/rosettadds-perf-runner -- [options]");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --backend <il2cpp|mono>                  Default: il2cpp");
        output.WriteLine("  --build-target <StandaloneLinux64|StandaloneOSX|Android>");
        output.WriteLine("  --scenario <name|all>                    Default: all");
        output.WriteLine("  --helper <path>                          Default: tools/ros2-perf-helper install output");
        output.WriteLine("  --player-build <path>                    Existing Player build path for --skip-build");
        output.WriteLine("  --artifacts <path>                       Default: artifacts/perf");
        output.WriteLine("  --capture-frames <count>                 Default: 1200");
        output.WriteLine("  --profiler-memory <bytes>                Default: 268435456");
        output.WriteLine("  --profiler-mode <lean|full>              Default: lean");
        output.WriteLine("  --skip-build                             Reuse --player-build instead of building");
        output.WriteLine("  --adb <path>                               Default: adb (PATH 解決)");
        output.WriteLine("  --android-device <serial>                  Required for --build-target Android (auto-detect 未実装)。");
        output.WriteLine("  --android-package <id>                     Default: com.ojii3.rosettadds.perf");
        output.WriteLine("  --android-activity <component>             Default: com.unity3d.player.UnityPlayerGameActivity");
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException("missing value for " + name);
        }
        index++;
        return args[index];
    }

    private static int ParsePositiveInt(string value, string name)
    {
        if (!int.TryParse(value, out int parsed) || parsed <= 0)
        {
            throw new ArgumentException(name + " must be a positive integer");
        }
        return parsed;
    }

    private static long ParsePositiveLong(string value, string name)
    {
        if (!long.TryParse(value, out long parsed) || parsed <= 0)
        {
            throw new ArgumentException(name + " must be a positive integer");
        }
        return parsed;
    }

    private static string DefaultBuildTarget()
        => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "StandaloneOSX" : "StandaloneLinux64";
}
