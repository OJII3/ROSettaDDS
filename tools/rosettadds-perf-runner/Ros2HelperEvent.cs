using System.Text.Json;

namespace ROSettaDDS.PerfRunner;

internal sealed class Ros2HelperEvent
{
    private Ros2HelperEvent(string eventName, string? message)
    {
        Event = eventName;
        Message = message;
    }

    internal string Event { get; }
    internal string? Message { get; }

    internal static bool TryParse(string line, out Ros2HelperEvent parsed)
    {
        parsed = null!;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("event", out JsonElement eventElement))
            {
                return false;
            }
            string? eventName = eventElement.GetString();
            if (string.IsNullOrEmpty(eventName))
            {
                return false;
            }
            string? message = null;
            if (document.RootElement.TryGetProperty("message", out JsonElement messageElement))
            {
                message = messageElement.GetString();
            }
            parsed = new Ros2HelperEvent(eventName, message);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
