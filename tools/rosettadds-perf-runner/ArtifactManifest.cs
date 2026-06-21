using System.Text.Json;

namespace ROSettaDDS.PerfRunner;

internal sealed class ArtifactManifest
{
    internal ArtifactManifest(string runId, RunnerOptions options)
    {
        RunId = runId;
        Backend = options.Backend;
        BuildTarget = options.BuildTarget;
        CaptureFrames = options.CaptureFrames;
        Scenarios = new List<ScenarioManifest>();
    }

    public string RunId { get; }
    public string Backend { get; }
    public string BuildTarget { get; }
    public int CaptureFrames { get; }
    public List<ScenarioManifest> Scenarios { get; }

    internal void Save(string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }
}

internal sealed class ScenarioManifest
{
    public string Name { get; set; } = "";
    public string Direction { get; set; } = "";
    public string MetricsPath { get; set; } = "";
    public string ProfilerPath { get; set; } = "";
    public string PlayerLogPath { get; set; } = "";
    public string HelperStdoutPath { get; set; } = "";
    public string HelperStderrPath { get; set; } = "";
    public int PlayerExitCode { get; set; }
    public int HelperExitCode { get; set; }
}
