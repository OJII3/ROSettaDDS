using ROSettaDDS.PerfRunner;

int exitCode = await MainAsync(args).ConfigureAwait(false);
return exitCode;

static async Task<int> MainAsync(string[] args)
{
    try
    {
        RunnerOptions options = RunnerOptions.Parse(args);
        if (options.Help)
        {
            RunnerOptions.PrintHelp(Console.Out);
            return 0;
        }

        string root = FindRepoRoot();
        string helper = ResolveHelper(root, options);
        string runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        string runDir = Path.GetFullPath(Path.Combine(root, options.Artifacts, runId));
        Directory.CreateDirectory(runDir);

        string playerPath = PlayerBuildPath(runDir, options.BuildTarget);
        if (!options.SkipBuild)
        {
            await BuildPlayer(root, playerPath, options, runDir).ConfigureAwait(false);
        }

        IReadOnlyList<PerfScenario> scenarios = PerfScenario.Select(options.Scenario);
        var manifest = new ArtifactManifest(runId, options);
        int domainBase = 20;
        for (int i = 0; i < scenarios.Count; i++)
        {
            ScenarioManifest scenarioManifest = await RunScenario(
                root,
                helper,
                PlayerExecutablePath(playerPath, options.BuildTarget),
                runDir,
                scenarios[i],
                options,
                domainBase + i).ConfigureAwait(false);
            manifest.Scenarios.Add(scenarioManifest);
            manifest.Save(Path.Combine(runDir, "manifest.json"));
        }

        manifest.Save(Path.Combine(runDir, "manifest.json"));
        Console.WriteLine("Artifacts: " + runDir);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 1;
    }
}

static async Task BuildPlayer(
    string root,
    string playerPath,
    RunnerOptions options,
    string runDir)
{
    string code =
        "ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer(" +
        CSharpString(playerPath) + ", " +
        CSharpString(options.BuildTarget) + ", " +
        CSharpString(options.Backend) + ");\n" +
        "return \"ok\";";

    using ProcessCapture uloop = ProcessCapture.Start(
        "uloop",
        new[]
        {
            "execute-dynamic-code",
            "--project-path", Path.Combine(root, "Ros2Unity"),
            "--code", code,
        },
        Path.Combine(runDir, "uloop-build.stdout.log"),
        Path.Combine(runDir, "uloop-build.stderr.log"),
        SanitizeUnityEnvironment);

    int exitCode = await uloop.WaitForExitAsync(TimeSpan.FromMinutes(20)).ConfigureAwait(false);
    if (exitCode != 0)
    {
        throw new InvalidOperationException(
            "uloop Player build failed with exit code " + exitCode +
            ". Open the Ros2Unity project in Unity Editor with uLoopMCP enabled, then rerun.");
    }
}

static async Task<ScenarioManifest> RunScenario(
    string root,
    string helper,
    string playerExecutable,
    string runDir,
    PerfScenario scenario,
    RunnerOptions options,
    int domainId)
{
    string scenarioDir = Path.Combine(runDir, scenario.Name);
    Directory.CreateDirectory(scenarioDir);
    string topic = "/rosettadds_perf_" + scenario.Name.Replace('-', '_');
    string readyFile = Path.Combine(scenarioDir, "player.ready");
    string doneFile = Path.Combine(scenarioDir, "player.done");
    string releaseFile = Path.Combine(scenarioDir, "player.release");
    string metricsFile = Path.Combine(scenarioDir, "metrics.ndjson");
    string profilerFile = Path.Combine(scenarioDir, "player.profiler.raw");
    string playerLog = Path.Combine(scenarioDir, "player.log");
    string helperStdout = Path.Combine(scenarioDir, "helper.stdout.ndjson");
    string helperStderr = Path.Combine(scenarioDir, "helper.stderr.log");

    var manifest = new ScenarioManifest
    {
        Name = scenario.Name,
        Direction = scenario.DirectionArgument,
        MetricsPath = metricsFile,
        ProfilerPath = profilerFile,
        PlayerLogPath = playerLog,
        HelperStdoutPath = helperStdout,
        HelperStderrPath = helperStderr,
    };

    if (scenario.Direction == PerfDirection.UnityToRos2)
    {
        using ProcessCapture helperProcess = StartHelper(helper, scenario, domainId, topic, "sub", helperStdout, helperStderr, measureStart: false);
        await helperProcess.WaitForEventAsync("ready", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        using ProcessCapture player = StartPlayer(playerExecutable, scenario, domainId, topic, options, readyFile, doneFile, releaseFile, metricsFile, profilerFile, playerLog, scenarioDir);
        await WaitForFile(readyFile, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        manifest.HelperExitCode = await helperProcess.WaitForExitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        File.WriteAllText(releaseFile, "release");
        manifest.PlayerExitCode = await player.WaitForExitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
    }
    else
    {
        using ProcessCapture player = StartPlayer(playerExecutable, scenario, domainId, topic, options, readyFile, doneFile, null, metricsFile, profilerFile, playerLog, scenarioDir);
        await WaitForFile(readyFile, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        using ProcessCapture helperProcess = StartHelper(helper, scenario, domainId, topic, "pub", helperStdout, helperStderr, measureStart: true);
        await helperProcess.WaitForEventAsync("ready", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        await helperProcess.WaitForEventAsync("armed", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        helperProcess.StandardInput.WriteLine("go");
        helperProcess.StandardInput.Flush();
        manifest.HelperExitCode = await helperProcess.WaitForExitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        manifest.PlayerExitCode = await player.WaitForExitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
    }

    if (manifest.PlayerExitCode != 0 || manifest.HelperExitCode != 0)
    {
        throw new InvalidOperationException(
            scenario.Name + " failed. player=" + manifest.PlayerExitCode + " helper=" + manifest.HelperExitCode);
    }

    return manifest;
}

static ProcessCapture StartHelper(
    string helper,
    PerfScenario scenario,
    int domainId,
    string topic,
    string mode,
    string stdoutPath,
    string stderrPath,
    bool measureStart)
{
    var args = new List<string>
    {
        "--mode", mode,
        "--topic", topic,
        "--messages", scenario.Messages.ToString(),
        "--payload-bytes", scenario.PayloadBytes.ToString(),
        "--rate-hz", "0",
        "--qos", scenario.Qos,
        "--ready-timeout-ms", "15000",
        "--idle-timeout-ms", "5000",
    };
    if (measureStart)
    {
        args.Add("--measure-start");
    }

    return ProcessCapture.Start(
        helper,
        args,
        stdoutPath,
        stderrPath,
        env =>
        {
            env["ROS_LOCALHOST_ONLY"] = "1";
            env["RMW_IMPLEMENTATION"] = "rmw_fastrtps_cpp";
            env["ROS_DOMAIN_ID"] = domainId.ToString();
        });
}

static ProcessCapture StartPlayer(
    string playerExecutable,
    PerfScenario scenario,
    int domainId,
    string topic,
    RunnerOptions options,
    string readyFile,
    string doneFile,
    string? releaseFile,
    string metricsFile,
    string profilerFile,
    string playerLog,
    string scenarioDir)
{
    var args = new List<string>
    {
        "-batchmode",
        "-nographics",
        "-logFile", playerLog,
        "-profiler-enable",
        "-profiler-log-file", profilerFile,
        "-profiler-capture-frame-count", options.CaptureFrames.ToString(),
        "-profiler-maxusedmemory", options.ProfilerMemory.ToString(),
        "--rosettadds-perf",
        "--rosettadds-scenario", scenario.Name,
        "--rosettadds-direction", scenario.DirectionArgument,
        "--rosettadds-domain-id", domainId.ToString(),
        "--rosettadds-topic", topic,
        "--rosettadds-qos", scenario.Qos,
        "--rosettadds-payload-bytes", scenario.PayloadBytes.ToString(),
        "--rosettadds-messages", scenario.Messages.ToString(),
        "--rosettadds-ready-file", readyFile,
        "--rosettadds-done-file", doneFile,
        "--rosettadds-metrics-file", metricsFile,
    };
    if (!string.IsNullOrEmpty(releaseFile))
    {
        args.Add("--rosettadds-release-file");
        args.Add(releaseFile);
    }

    return ProcessCapture.Start(
        playerExecutable,
        args,
        Path.Combine(scenarioDir, "player.stdout.log"),
        Path.Combine(scenarioDir, "player.stderr.log"),
        SanitizeUnityEnvironment);
}

static async Task WaitForFile(string path, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    while (!cts.IsCancellationRequested)
    {
        if (File.Exists(path))
        {
            return;
        }
        await Task.Delay(20, cts.Token).ConfigureAwait(false);
    }
    throw new TimeoutException("timed out waiting for file: " + path);
}

static void SanitizeUnityEnvironment(IDictionary<string, string?> env)
{
    string[] keys =
    {
        "AMENT_PREFIX_PATH",
        "COLCON_PREFIX_PATH",
        "CMAKE_PREFIX_PATH",
        "ROS_DISTRO",
        "ROS_DOMAIN_ID",
        "ROS_LOCALHOST_ONLY",
        "RMW_IMPLEMENTATION",
    };
    foreach (string key in keys)
    {
        env.Remove(key);
    }
}

static string FindRepoRoot()
{
    string directory = Directory.GetCurrentDirectory();
    while (!File.Exists(Path.Combine(directory, "rosettadds.sln")))
    {
        string? parent = Directory.GetParent(directory)?.FullName;
        if (parent == null)
        {
            throw new InvalidOperationException("could not find repository root");
        }
        directory = parent;
    }
    return directory;
}

static string ResolveHelper(string root, RunnerOptions options)
{
    string path = options.Helper ?? Path.Combine(
        root,
        "tools",
        "ros2-perf-helper",
        "install",
        "rosettadds_ros2_perf_helper",
        "lib",
        "rosettadds_ros2_perf_helper",
        "ros2_perf_helper");
    path = Path.GetFullPath(path);
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("ROS 2 perf helper not found. Run scripts/ros2/build_helper.sh first.", path);
    }
    return path;
}

static string PlayerBuildPath(string runDir, string buildTarget)
{
    string buildDir = Path.Combine(runDir, "build");
    Directory.CreateDirectory(buildDir);
    return buildTarget == "StandaloneOSX"
        ? Path.Combine(buildDir, "ROSettaDDSPerfPlayer.app")
        : Path.Combine(buildDir, "ROSettaDDSPerfPlayer");
}

static string PlayerExecutablePath(string buildPath, string buildTarget)
{
    if (buildTarget != "StandaloneOSX")
    {
        return buildPath;
    }
    return Path.Combine(buildPath, "Contents", "MacOS", Path.GetFileNameWithoutExtension(buildPath));
}

static string CSharpString(string value)
{
    return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
