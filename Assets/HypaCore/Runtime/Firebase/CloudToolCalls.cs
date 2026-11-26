using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaCore.Hybrid
{
    public static class CloudToolCalls
    {
        public const int MaxRounds = 4;

        public static JArray Declarations(IReadOnlyList<CloudTool> tools)
        {
            var list = new JArray();
            foreach (var tool in tools ?? new CloudTool[0])
            {
                var parameters = tool.Parameters ?? new Dictionary<string, string>();
                list.Add(new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject(parameters.Select(p => new JProperty(p.Key, new JObject { ["type"] = "string", ["description"] = p.Value }))),
                        ["required"] = new JArray(parameters.Keys),
                    },
                });
            }
            return list;
        }

        public static async Task<JObject> RunAsync(IReadOnlyList<CloudTool> tools, JObject call, CancellationToken ct)
        {
            var name = (string)call?["name"];
            var tool = tools?.FirstOrDefault(t => t.Name == name);
            var args = new Dictionary<string, string>();
            if (call?["args"] is JObject given)
                foreach (var p in given.Properties())
                    args[p.Name] = p.Value.Type == JTokenType.String ? (string)p.Value : p.Value.ToString(Formatting.None);
            var result = tool == null ? $"There is no tool called {name}." : await tool.Run(args, ct);
            return new JObject { ["functionResponse"] = new JObject { ["name"] = name, ["response"] = new JObject { ["result"] = result } } };
        }
    }
}
