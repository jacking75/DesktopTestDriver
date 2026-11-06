using System.Text.Json;

namespace DesktopTestDriver;
public static class ProfileValidator
{
    public static DiagnosticItem[] Validate(string json)
    {
        using var doc = JsonDocument.Parse(json); GuiProtocol.Unique(doc.RootElement);
        var profile = GuiProtocol.Decode<ApplicationProfile>(json);
        var result = new List<DiagnosticItem>();
        void Check(string name, bool ok, string reason) => result.Add(new(name, ok ? "supported" : "unsupported", reason));
        Check("version", profile.ProfileVersion == 1, "profileVersion=1");
        Check("profileId", GuiProtocol.Id(profile.ProfileId), "opaque-identifier");
        Check("backend", profile.Backend == "windows-uia", "windows-uia");
        Check("application", profile.Application != null && GuiProtocol.Id(profile.Application.ExecutableBinding) && GuiProtocol.Id(profile.Application.WorkingDirectoryBinding) && GuiProtocol.Id(profile.Application.FingerprintBinding), "machine-bindings-required");
        Check("targets", profile.Targets is { Count: > 0 and <= 128 }, "bounded-selectors-required");
        if (profile.Targets != null) foreach (var target in profile.Targets)
        {
            try
            {
                // Omit null DTO fields, as profiles use sparse selectors.
                var options = new JsonSerializerOptions(GuiProtocol.Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
                GuiProtocol.ValidateSelector(JsonSerializer.SerializeToElement(target.Value, options));
                Check("target:" + target.Key, GuiProtocol.Id(target.Key), "selector-validated");
            }
            catch { Check("target:" + target.Key, false, "invalid-selector"); }
        }
        Check("actions", profile.Actions is { Count: > 0 and <= 128 }, "bounded-actions-required");
        if (profile.Actions != null) foreach (var action in profile.Actions)
            Check("action:" + action.Key, action.Value != null && profile.Targets?.ContainsKey(action.Value.Target) == true && AutomationOperations.Contains(action.Value.Operation), "declared-target-and-operation");
        result.Add(new("backgroundQualification", "unknown", "static-validation-does-not-qualify-background-actions", "run-bounded-qualification"));
        return result.ToArray();
    }
}
