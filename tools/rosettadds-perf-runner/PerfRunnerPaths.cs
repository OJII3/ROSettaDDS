namespace ROSettaDDS.PerfRunner;

internal static class PerfRunnerPaths
{
    internal static string ResolvePlayerBuildPath(string runDir, RunnerOptions options)
    {
        if (options.SkipBuild)
        {
            return Path.GetFullPath(options.PlayerBuild!);
        }

        string buildDir = Path.Combine(runDir, "build");
        Directory.CreateDirectory(buildDir);
        if (options.BuildTarget == "Android")
        {
            return Path.Combine(buildDir, "ROSettaDDSPerfPlayer.apk");
        }
        return options.BuildTarget == "StandaloneOSX"
            ? Path.Combine(buildDir, "ROSettaDDSPerfPlayer.app")
            : Path.Combine(buildDir, "ROSettaDDSPerfPlayer");
    }

    internal static string PlayerExecutablePath(string buildPath, string buildTarget)
    {
        if (buildTarget != "StandaloneOSX")
        {
            return buildPath;
        }
        return Path.Combine(buildPath, "Contents", "MacOS", Path.GetFileNameWithoutExtension(buildPath));
    }

    internal static void EnsurePlayerExecutableExists(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Player executable not found. Build a Player first or pass the correct --player-build path.", executablePath);
        }
    }
}
