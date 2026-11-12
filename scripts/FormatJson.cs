// Re-indents a JSON document: dotnet run scripts/FormatJson.cs -- <in> <out>
#:property PublishAot=false
#:property ImplicitUsings=enable
#:property TreatWarningsAsErrors=false
using System.Text.Json;
using System.Text.Json.Nodes;
var node = JsonNode.Parse(File.ReadAllText(args[0]));
File.WriteAllText(args[1], node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
