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
            ["element-selector.schema.json"] = Schema(typeof(ElementSelector)),
            ["observation-node.schema.json"] = Schema(typeof(ObservationNode)),
            ["operation-descriptor.schema.json"] = Schema(typeof(OperationDescriptor[])),
            ["window.schema.json"] = Schema(typeof(PublicWindow)),
            ["capture.schema.json"] = Schema(typeof(CaptureInfo)),
            ["diagnostic.schema.json"] = Schema(typeof(DiagnosticItem[])),
            ["request.schema.json"] = Schema(typeof(GuiRequest)),
            ["response.schema.json"] = Schema(typeof(GuiResponse))
        };
    }
}
