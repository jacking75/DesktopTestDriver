using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace DesktopTestDriver;
public static class ContractSchemas
{
    public static Dictionary<string, JsonNode> Export()
    {
        var options = new JsonSerializerOptions(GuiProtocol.Json) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        JsonNode Schema(Type type)
        {
            var schema = options.GetJsonSchemaAsNode(type); schema["$schema"] = "https://json-schema.org/draft/2020-12/schema"; return schema;
        }
        return new()
        {
            ["application-profile.schema.json"] = Schema(typeof(ApplicationProfile)),
            ["machine-bindings.schema.json"] = Schema(typeof(MachineBindings)),
            ["element-selector.schema.json"] = Schema(typeof(ElementSelector)),
            ["observation.schema.json"] = Schema(typeof(Observation)),
            ["operation-descriptor.schema.json"] = Schema(typeof(OperationDescriptor)),
            ["diagnostic.schema.json"] = Schema(typeof(DiagnosticItem[])),
            ["request.schema.json"] = Schema(typeof(GuiRequest)),
            ["response.schema.json"] = Schema(typeof(GuiResponse))
        };
    }
}
