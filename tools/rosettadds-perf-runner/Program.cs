using ROSettaDDS.PerfRunner;
using System.Text.Json;

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

        string playerPath = PerfRunnerPaths.ResolvePlayerBuildPath(runDir, options);
        if (!options.SkipBuild)
        {
            await BuildPlayer(root, playerPath, options, runDir).ConfigureAwait(false);
        }
        string playerExecutable = PerfRunnerPaths.PlayerExecutablePath(playerPath, options.BuildTarget);
        if (options.SkipBuild)
        {
            PerfRunnerPaths.EnsurePlayerExecutableExists(playerExecutable);
        }

        IReadOnlyList<PerfScenario> scenarios = PerfScenario.Select(options.Scenario);
        var manifest = new ArtifactManifest(runId, options);
        int domainBase = 20;
        bool failed = false;
        for (int i = 0; i < scenarios.Count; i++)
        {
            ScenarioManifest scenarioManifest = await RunScenario(
                root,
                helper,
                playerExecutable,
                runDir,
                scenarios[i],
                options,
                domainBase + i).ConfigureAwait(false);
            manifest.Scenarios.Add(scenarioManifest);
            manifest.Save(Path.Combine(runDir, "manifest.json"));
            if (scenarioManifest.PlayerExitCode != 0 || scenarioManifest.HelperExitCode != 0)
            {
                failed = true;
            }
        }

        manifest.Save(Path.Combine(runDir, "manifest.json"));
        Console.WriteLine("Artifacts: " + runDir);
        return failed ? 1 : 0;
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

    string stdoutPath = Path.Combine(runDir, "uloop-build.stdout.log");
    string stderrPath = Path.Combine(runDir, "uloop-build.stderr.log");
    const int maxAttempts = 5;
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        using ProcessCapture uloop = ProcessCapture.Start(
            "uloop",
            new[]
            {
                "execute-dynamic-code",
                "--project-path", Path.Combine(root, "Ros2Unity"),
                "--code", code,
            },
            stdoutPath,
            stderrPath,
            SanitizeUnityEnvironment);

        int exitCode = await uloop.WaitForExitAsync(TimeSpan.FromMinutes(20)).ConfigureAwait(false);
        if (exitCode == 0)
        {
            EnsureUloopSuccess(stdoutPath);
            return;
        }

        string stderr = File.Exists(stderrPath) ? File.ReadAllText(stderrPath) : "";
        if (attempt < maxAttempts && IsTransientUloopState(stderr))
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            continue;
        }

        throw new InvalidOperationException(
            "uloop Player build failed with exit code " + exitCode +
            ". Open the Ros2Unity project in Unity Editor with uLoopMCP enabled, then rerun. " +
            stderr.Trim());
    }
}

static bool IsTransientUloopState(string stderr)
{
    return stderr.Contains("Unity server is starting", StringComparison.OrdinalIgnoreCase) ||
           stderr.Contains("Domain Reload in progress", StringComparison.OrdinalIgnoreCase) ||
           stderr.Contains("Please wait a moment and try again", StringComparison.OrdinalIgnoreCase);
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
        using ProcessCapture player = StartPlayer(playerExecutable, scenario, domainId, topic, options, readyFile, doneFile, releaseFile, metricsFile, profilerFile, playerLog, scenarioDir);
        await WaitForFile(readyFile, TimeSpan.FromSeconds(20), "Player ready sentinel", player).ConfigureAwait(false);
        using ProcessCapture helperProcess = StartHelper(helper, scenario, domainId, topic, "sub", helperStdout, helperStderr, measureStart: false);
        await helperProcess.WaitForEventAsync("ready", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        manifest.HelperExitCode = await helperProcess.WaitForExitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        File.WriteAllText(releaseFile, "release");
        manifest.PlayerExitCode = await player.WaitForExitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
    }
    else
    {
        using ProcessCapture player = StartPlayer(playerExecutable, scenario, domainId, topic, options, readyFile, doneFile, null, metricsFile, profilerFile, playerLog, scenarioDir);
        await WaitForFile(readyFile, TimeSpan.FromSeconds(20), "Player ready sentinel", player).ConfigureAwait(false);
        using ProcessCapture helperProcess = StartHelper(helper, scenario, domainId, topic, "pub", helperStdout, helperStderr, measureStart: true);
        await helperProcess.WaitForEventAsync("ready", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        await helperProcess.WaitForEventAsync("armed", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        helperProcess.StandardInput.WriteLine("go");
        helperProcess.StandardInput.Flush();
        manifest.HelperExitCode = await helperProcess.WaitForExitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        manifest.PlayerExitCode = await player.WaitForExitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
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
    IReadOnlyList<string> args = PerfRunnerProcessArgs.Helper(scenario, topic, mode, measureStart);

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
    args.Add("--rosettadds-profiler-mode");
    args.Add(options.ProfilerMode);

    return ProcessCapture.Start(
        playerExecutable,
        args,
        Path.Combine(scenarioDir, "player.stdout.log"),
        Path.Combine(scenarioDir, "player.stderr.log"),
        SanitizeUnityEnvironment);
}

static async Task WaitForFile(string path, TimeSpan timeout, string description, params ProcessCapture[] watchedProcesses)
{
    DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (File.Exists(path))
        {
            return;
        }
        foreach (ProcessCapture process in watchedProcesses)
        {
            int? exitCode = process.ExitCodeOrNull;
            if (exitCode.HasValue)
            {
                throw new InvalidOperationException(description + " was not written before process exit: " + exitCode.Value);
            }
        }
        await Task.Delay(20).ConfigureAwait(false);
    }
    throw new TimeoutException("timed out waiting for " + description + ": " + path);
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
    env["SDL_VIDEODRIVER"] = "dummy";
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

static string CSharpString(string value)
{
    return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

static void EnsureUloopSuccess(string stdoutPath)
{
    string text = File.ReadAllText(stdoutPath);
    using JsonDocument document = JsonDocument.Parse(text);
    if (!document.RootElement.TryGetProperty("Success", out JsonElement successElement) ||
        !successElement.GetBoolean())
    {
        string error = document.RootElement.TryGetProperty("Error", out JsonElement errorElement)
            ? errorElement.GetString() ?? "unknown uloop error"
            : "unknown uloop error";
        string diagnostics = document.RootElement.TryGetProperty("DiagnosticsSummary", out JsonElement diagElement)
            ? diagElement.GetString() ?? ""
            : "";
        throw new InvalidOperationException("uloop Player build failed: " + error + " " + diagnostics);
    }
}
