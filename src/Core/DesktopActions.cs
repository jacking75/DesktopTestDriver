using System.Text.Json;

namespace DesktopTestDriver;

public sealed record DesktopAction(string Name, long? Hwnd = null, string? Title = null, string? Text = null,
    string? AutomationId = null, int? X = null, int? Y = null);

// Composite requests are implemented in C#, not in a Python automation layer.
public static class DesktopActions
{
    public static bool Handles(string? name) => name is "type-window" or "click-control" or "move-window" or "click-window";
    public static async Task<Result> Execute(JsonElement arguments, Func<JsonElement, Task<Result>> send)
    {
        var action = Wire.Decode<DesktopAction>(arguments.GetRawText());
        if (!Handles(action.Name) || action.Hwnd == null && action.Title == null) throw new DriverException(Error.InvalidRequest);
        static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Wire.Json);
        var inspected = await send(Json(new { name = "inspect" }));
        if (inspected.ErrorCode != Error.None) return inspected;
        var frames = Wire.Decode<InspectResult>(Wire.Encode(inspected.Evidence)).Frames;
        var matches = frames.Where(f => (action.Hwnd == null || f.Window.Hwnd == action.Hwnd) && (action.Title == null || f.Window.Title == action.Title)).ToArray();
        if (matches.Length != 1) throw new DriverException(Error.WindowChanged);
        var frame = matches[0];
        if (frame.Observation.Foreground != frame.Window.Hwnd)
        {
            var focused = await send(Json(new { name = "focus", expectedWindow = frame.Window, frameId = frame.Id }));
            if (focused.ErrorCode != Error.None) return focused;
            frame = Wire.Decode<Frame>(Wire.Encode(focused.Evidence));
        }
        if (action.Name == "type-window")
        {
            if (action.Text == null) throw new DriverException(Error.InvalidRequest);
            return await send(Json(new { name = "text", text = action.Text, expectedWindow = frame.Window, frameId = frame.Id }));
        }
        int? x = action.X, y = action.Y;
        if (action.Name == "click-control")
        {
            if (string.IsNullOrWhiteSpace(action.AutomationId)) throw new DriverException(Error.InvalidRequest);
            var controls = await send(Json(new { name = "controls", expectedWindow = frame.Window }));
            if (controls.ErrorCode != Error.None) return controls;
            frame = Wire.Decode<Frame>(Wire.Encode(controls.Evidence));
            var matchesControl = frame.Controls.Where(c => c.AutomationId == action.AutomationId && c.Enabled && c.Bounds.Width > 0 && c.Bounds.Height > 0).ToArray();
            if (matchesControl.Length != 1) throw new DriverException(Error.ObservationUnavailable);
            var control = matchesControl[0].Bounds;
            x = checked(control.X + control.Width / 2 - frame.Window.Bounds.X);
            y = checked(control.Y + control.Height / 2 - frame.Window.Bounds.Y);
        }
        if (x == null || y == null) throw new DriverException(Error.InvalidRequest);
        return await send(Json(new { name = action.Name == "move-window" ? "move" : "click", x, y, expectedWindow = frame.Window, frameId = frame.Id }));
    }
}
