// OpenAPI pre-processor for the Remnawave API document.
//
// Usage:  dotnet run scripts/Preprocess.cs -- <input.json> <output.json> [schema-names.json]
//
// The upstream document is produced by nestjs-zod / zod-to-openapi and contains many
// constructs that make generated C# clients awkward to use. This script normalises it so
// that Kiota produces a clean, deduplicated, strongly-typed model:
//
//   1. Strips non OpenAPI 3.0 keywords (VS Code JSON-schema extensions, zod `errorMessage`, ...),
//      regex `pattern`s and `default`s (defaults would otherwise be assigned in model constructors
//      and silently sent in PATCH bodies).
//   2. Replaces the recursive "any JSON value" helper schemas (`*__schemaN`) with free-form JSON.
//   3. Collapses unions of primitives (e.g. anyOf[string(ipv4), string(ipv6)] -> string).
//   4. Merges the four error DTOs into a single `RemnawaveError` mapped to 4XX / 5XX, so every
//      failed call throws one strongly-typed exception.
//   5. Hoists every inline object / enum into components, deduplicating structurally identical
//      shapes (the "user" object appears 7 times inline) and giving them readable names
//      (User, Node, UserStatus, ...).
//   6. Adds `x-ms-enum` metadata so enum members get idiomatic PascalCase names.

#:property PublishAot=false
#:property ImplicitUsings=enable
#:property TreatWarningsAsErrors=false
#:property NoWarn=IL2026;IL3050

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run scripts/Preprocess.cs -- <input.json> <output.json>");
    return 1;
}

var root = JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
var schemas = root["components"]!["schemas"]!.AsObject();
const string RefPrefix = "#/components/schemas/";

// Keywords that are not part of the OpenAPI 3.0 Schema Object or carry no value for code generation.
var stripKeys = new HashSet<string>
{
    "markdownDescription", "markdownEnumDescriptions", "defaultSnippets", "errorMessage",
    "propertyNames", "examples", "example", "pattern", "default", "$schema",
};

// ---------------------------------------------------------------------------------------------
// 1. Free-form JSON helper schemas
// ---------------------------------------------------------------------------------------------
var jsonValueSchemas = schemas
    .Where(kv => Regex.IsMatch(kv.Key, @"__schema\d+$"))
    .Select(kv => kv.Key)
    .ToHashSet();
foreach (var name in jsonValueSchemas) schemas.Remove(name);

// ---------------------------------------------------------------------------------------------
// 1b. Colliding path templates
// ---------------------------------------------------------------------------------------------
// Upstream declares e.g. `POST /api/connections/by-user/{userId}` and `GET /api/connections/by-user/{jobId}`.
// Templates that differ only by parameter names are invalid OpenAPI and Kiota merges them unpredictably.
// Merge them into one path item with a neutral `{id}` string parameter.
string[] httpMethods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];
var paths = root["paths"]!.AsObject();
foreach (var group in paths.Select(p => p.Key).GroupBy(p => Regex.Replace(p, @"\{[^}]+\}", "{}")).Where(g => g.Count() > 1).ToList())
{
    var members = group.ToList();
    var segments = members.Select(p => p.Split('/')).ToList();
    var merged = new JsonObject();
    var mergedSegments = (string[])segments[0].Clone();
    var renames = new Dictionary<string, Dictionary<string, string>>(); // path -> old param name -> new name
    var differing = Enumerable.Range(0, mergedSegments.Length)
        .Where(i => segments.Select(s => s[i]).Distinct().Count() > 1).ToList();
    foreach (var i in differing)
    {
        var newName = differing.Count == 1 ? "id" : $"id{differing.IndexOf(i) + 1}";
        mergedSegments[i] = "{" + newName + "}";
        for (var m = 0; m < members.Count; m++)
        {
            if (!renames.TryGetValue(members[m], out var map)) renames[members[m]] = map = [];
            map[segments[m][i].Trim('{', '}')] = newName;
        }
    }

    foreach (var path in members)
    {
        var item = paths[path]!.AsObject();
        foreach (var (key, value) in item.ToList())
        {
            if (httpMethods.Contains(key) && merged.ContainsKey(key))
                throw new InvalidOperationException($"Cannot merge colliding paths {string.Join(", ", members)}: both define {key.ToUpperInvariant()}.");
            if (!httpMethods.Contains(key) && merged.ContainsKey(key)) continue;
            var clone = value!.DeepClone();
            if (httpMethods.Contains(key) && clone["parameters"] is JsonArray ps)
            {
                foreach (var p in ps.OfType<JsonObject>())
                {
                    if (p["in"]?.GetValue<string>() != "path" || !renames[path].TryGetValue(p["name"]!.GetValue<string>(), out var newName)) continue;
                    var original = p["name"]!.GetValue<string>();
                    p["name"] = newName;
                    p["description"] = $"{original} ({clone["operationId"]?.GetValue<string>()?.Split('_').Last()})";
                    p["schema"] = new JsonObject { ["type"] = "string" }; // the merged parameter must accept every variant
                }
            }
            merged[key] = clone;
        }
        paths.Remove(path);
    }
    paths[string.Join('/', mergedSegments)] = merged;
}

// Parameter objects may carry JSON-schema keywords at the wrong level.
foreach (var (_, pathItem) in paths)
foreach (var (_, opNode) in pathItem!.AsObject())
    if (opNode is JsonObject { } o && o["parameters"] is JsonArray ps)
        foreach (var p in ps.OfType<JsonObject>()) p.Remove("propertyNames");

// ---------------------------------------------------------------------------------------------
// 2. Errors
// ---------------------------------------------------------------------------------------------
string[] errorDtos = ["RemnawaveBadRequestErrorDto", "RemnawaveValidationErrorDto", "RemnawaveNotFoundErrorDto", "RemnawaveInternalServerErrorDto"];
var errorProps = new JsonObject();
foreach (var dto in errorDtos)
{
    if (schemas[dto]?["properties"] is not JsonObject props) continue;
    foreach (var (key, value) in props)
    {
        if (errorProps.ContainsKey(key)) continue;
        var clone = value!.DeepClone().AsObject();
        clone.Remove("enum"); // known messages are informative only; never constrain deserialisation
        errorProps[key] = clone;
    }
    schemas.Remove(dto);
}
if (errorProps["errors"] is JsonObject errs && errs["items"] is JsonObject issue)
{
    schemas["RemnawaveValidationIssue"] = issue.DeepClone();
    errs["items"] = new JsonObject { ["$ref"] = RefPrefix + "RemnawaveValidationIssue" };
}
schemas["RemnawaveError"] = new JsonObject
{
    ["type"] = "object",
    ["description"] = "Error payload returned by the Remnawave panel for any non-success status code.",
    ["properties"] = errorProps,
};

foreach (var (_, pathItem) in root["paths"]!.AsObject())
foreach (var (method, opNode) in pathItem!.AsObject())
{
    if (opNode is not JsonObject op || op["responses"] is not JsonObject responses) continue;
    foreach (var code in responses.Select(r => r.Key).Where(c => !c.StartsWith('2')).ToList())
        responses.Remove(code);
    foreach (var range in new[] { "4XX", "5XX" })
    {
        responses[range] = new JsonObject
        {
            ["description"] = range == "4XX" ? "Client error" : "Server error",
            ["content"] = new JsonObject
            {
                ["application/json"] = new JsonObject
                {
                    ["schema"] = new JsonObject { ["$ref"] = RefPrefix + "RemnawaveError" },
                },
            },
        };
    }
}

// ---------------------------------------------------------------------------------------------
// 3. Normalise every schema (strip keys, json-value refs, primitive unions)
// ---------------------------------------------------------------------------------------------
JsonNode NormaliseSchema(JsonNode node, string? propertyName = null)
{
    if (node is not JsonObject obj) return node;

    // Body DTOs only carry zod's ISO regex, not `format: date-time`; restore it so they become DateTimeOffset.
    if (obj["type"]?.GetValue<string>() == "string" && obj["format"] is null
        && obj["pattern"]?.GetValue<string>() is { } pattern && pattern.Contains(@"T(?:(?:[01]\d|2[0-3])"))
        obj["format"] = "date-time";

    // zod emits `number` for everything; map values that are integral by nature to long/int.
    if (obj["type"]?.GetValue<string>() == "number" && propertyName is not null)
    {
        if (Words.Int64Properties.IsMatch(propertyName)) { obj["type"] = "integer"; obj["format"] = "int64"; }
        else if (Words.Int32Properties.IsMatch(propertyName)) { obj["type"] = "integer"; obj["format"] = "int32"; }
    }

    if (obj["$ref"] is JsonValue r && r.GetValue<string>() is var refStr && refStr.StartsWith(RefPrefix)
        && jsonValueSchemas.Contains(refStr[RefPrefix.Length..]))
    {
        return new JsonObject(); // free-form JSON
    }

    foreach (var key in stripKeys) obj.Remove(key);

    // `enum: [..., null]` would become a sentinel enum member in Kiota; nullability is expressed by `nullable`.
    if (obj["enum"] is JsonArray enumValues && enumValues.Any(v => v is null))
    {
        var nonNull = enumValues.Where(v => v is not null).Select(v => v!.DeepClone()).ToArray();
        obj["enum"] = new JsonArray(nonNull);
        obj["nullable"] = true;
    }

    if (obj["properties"] is JsonObject props)
        foreach (var key in props.Select(p => p.Key).ToList())
            props[key] = NormaliseSchema(Detach(props[key]!), key);

    if (obj["items"] is JsonObject items) obj["items"] = NormaliseSchema(Detach(items), propertyName);
    if (obj["additionalProperties"] is JsonObject ap) obj["additionalProperties"] = NormaliseSchema(Detach(ap));
    if (obj["not"] is JsonObject not) obj["not"] = NormaliseSchema(Detach(not));

    foreach (var kw in new[] { "oneOf", "anyOf", "allOf" })
    {
        if (obj[kw] is not JsonArray arr) continue;
        var normalised = arr.Select(m => NormaliseSchema(Detach(m!), propertyName)).ToList();
        arr.Clear();
        foreach (var m in normalised) arr.Add(m);

        if (kw == "allOf") continue;
        // Collapse unions that consist only of primitives.
        var members = normalised.OfType<JsonObject>().ToList();
        bool IsPrimitive(JsonObject m) =>
            m["type"]?.GetValue<string>() is "string" or "number" or "integer" or "boolean"
            && m["enum"] is null && m["properties"] is null;
        bool IsFreeForm(JsonObject m) => m.Count == 0;
        if (members.Count == normalised.Count && members.All(m => IsPrimitive(m) || IsFreeForm(m)))
        {
            obj.Remove(kw);
            var types = members.Select(m => m["type"]?.GetValue<string>()).Distinct().ToList();
            if (types.Count == 1 && types[0] is not null)
            {
                obj["type"] = types[0];
                var formats = members.Select(m => m["format"]?.GetValue<string>()).Distinct().ToList();
                if (formats.Count == 1 && formats[0] is not null) obj["format"] = formats[0];
            }
            // otherwise: leave as free-form JSON (no type)
        }
    }
    return obj;
}

static JsonNode Detach(JsonNode n) => n.Parent is null ? n : n.DeepClone();

foreach (var key in schemas.Select(s => s.Key).ToList())
    schemas[key] = NormaliseSchema(Detach(schemas[key]!));

foreach (var (_, pathItem) in root["paths"]!.AsObject())
foreach (var (_, opNode) in pathItem!.AsObject())
{
    if (opNode is not JsonObject op) continue;
    if (op["parameters"] is JsonArray ps)
        foreach (var p in ps.OfType<JsonObject>())
        {
            if (p["schema"] is not JsonObject s) continue;
            var schema = (JsonObject)NormaliseSchema(Detach(s), p["name"]?.GetValue<string>());
            // Numeric path/query parameters (ids, paging) are integers: avoid `this[double position]`.
            if (schema["type"]?.GetValue<string>() == "number")
            {
                schema["type"] = "integer";
                schema["format"] = p["in"]?.GetValue<string>() == "path" ? "int64" : "int32";
            }
            p["schema"] = schema;
        }
    foreach (var container in new[] { op["requestBody"], op["responses"] })
    {
        if (container is null) continue;
        IEnumerable<JsonObject> mediaHolders = container == op["requestBody"]
            ? [container.AsObject()]
            : container.AsObject().Select(r => r.Value).OfType<JsonObject>();
        foreach (var holder in mediaHolders)
            if (holder["content"] is JsonObject content)
                foreach (var (_, media) in content)
                    if (media?["schema"] is JsonObject s) media["schema"] = NormaliseSchema(Detach(s));
    }
}

// ---------------------------------------------------------------------------------------------
// 4. Hoist + deduplicate inline objects and enums
// ---------------------------------------------------------------------------------------------
var hashExclude = new HashSet<string> { "description", "title", "nullable", "deprecated", "readOnly", "writeOnly" };
var defs = new Dictionary<string, JsonObject>();
var occurrences = new Dictionary<string, List<NameCandidate>>();
var reserved = new HashSet<string>(schemas.Select(s => s.Key), StringComparer.OrdinalIgnoreCase);

string Canonical(JsonNode? n)
{
    switch (n)
    {
        case JsonObject o:
            var sb = new StringBuilder("{");
            foreach (var (k, v) in o.Where(kv => !hashExclude.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(JsonSerializer.Serialize(k)).Append(':').Append(Canonical(v)).Append(',');
            return sb.Append('}').ToString();
        case JsonArray a:
            return "[" + string.Join(",", a.Select(Canonical)) + "]";
        default:
            return n?.ToJsonString() ?? "null";
    }
}

bool IsHoistable(JsonObject o) =>
    (o["properties"] is JsonObject p && p.Count > 0) || o["enum"] is JsonArray;

// Walks a schema post-order; `segments` describes the path for naming purposes.
JsonNode Hoist(JsonNode node, List<string> segments, bool isRoot)
{
    if (node is not JsonObject obj) return node;

    if (obj["properties"] is JsonObject props)
        foreach (var key in props.Select(p => p.Key).ToList())
            props[key] = Hoist(Detach(props[key]!), [.. segments, Pascal(key)], false);

    if (obj["items"] is JsonObject items)
        obj["items"] = Hoist(Detach(items), Singularise(segments), false);

    if (obj["additionalProperties"] is JsonObject ap)
        obj["additionalProperties"] = Hoist(Detach(ap), [.. segments, "Value"], false);

    foreach (var kw in new[] { "oneOf", "anyOf", "allOf" })
    {
        if (obj[kw] is not JsonArray arr) continue;
        var list = arr.Select(m => m!).ToList();
        arr.Clear();
        for (var i = 0; i < list.Count; i++)
        {
            var label = DiscriminatorLabel(list[i]) ?? (kw == "allOf" ? $"Part{i + 1}" : $"Variant{i + 1}");
            arr.Add(Hoist(Detach(list[i]), [.. segments, label], false));
        }
    }

    if (isRoot || !IsHoistable(obj)) return obj;

    var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Canonical(obj))))[..12];
    defs.TryAdd(hash, obj);
    if (!occurrences.TryGetValue(hash, out var occ)) occurrences[hash] = occ = [];
    occ.Add(new NameCandidate(segments));
    return new JsonObject { ["$ref"] = "#hoisted/" + hash };
}

foreach (var key in schemas.Select(s => s.Key).ToList())
    schemas[key] = Hoist(Detach(schemas[key]!), [StripDto(key)], true);

foreach (var (_, pathItem) in root["paths"]!.AsObject())
foreach (var (_, opNode) in pathItem!.AsObject())
{
    if (opNode is not JsonObject op || op["parameters"] is not JsonArray ps) continue;
    var opName = Pascal(op["operationId"]?.GetValue<string>()?.Split('_').Last() ?? "Operation");
    foreach (var p in ps.OfType<JsonObject>())
        if (p["schema"] is JsonObject s)
            p["schema"] = Hoist(Detach(s), [opName, Pascal(p["name"]!.GetValue<string>())], false);
}

// Assign names: most frequently used shapes first so they win the short names.
var assigned = new Dictionary<string, string>();
foreach (var (hash, occ) in occurrences.OrderByDescending(o => o.Value.Count).ThenBy(o => o.Value.Min(c => c.Short.Length)))
{
    var preferred = occ.Select(c => c.Short).Distinct().OrderBy(s => s.Length).ThenBy(s => s, StringComparer.Ordinal)
        .Concat(occ.Select(c => c.Full).Distinct().OrderBy(s => s.Length).ThenBy(s => s, StringComparer.Ordinal));
    var name = preferred.FirstOrDefault(n => !reserved.Contains(n));
    if (name is null)
    {
        var baseName = occ.Select(c => c.Short).OrderBy(s => s.Length).First();
        for (var i = 2; ; i++) if (!reserved.Contains(name = baseName + i)) break;
    }
    reserved.Add(name);
    assigned[hash] = name;
}

// Curated renames (spec/schema-names.json): { "GeneratedName": "PreferredName" }.
var overridesPath = args.Length > 2 ? args[2] : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!, "schema-names.json");
if (File.Exists(overridesPath))
{
    var overrides = JsonNode.Parse(File.ReadAllText(overridesPath))!.AsObject();
    foreach (var (hash, name) in assigned.ToList())
    {
        if (overrides[name]?.GetValue<string>() is not { } renamed) continue;
        if (assigned.ContainsValue(renamed) || schemas.ContainsKey(renamed))
            throw new InvalidOperationException($"schema-names.json: '{renamed}' is already in use.");
        assigned[hash] = renamed;
    }
}

// A C# member cannot share its type's name (Kiota would emit `InternalSquadsProp`), so list payloads
// such as { internalSquads: [...], total } become `InternalSquadsResult.InternalSquads`.
foreach (var (hash, name) in assigned.ToList())
{
    if (defs[hash]["properties"] is not JsonObject props || !props.Any(p => Pascal(p.Key) == name)) continue;
    var renamed = name + "Result";
    for (var i = 2; assigned.ContainsValue(renamed) || schemas.ContainsKey(renamed); i++) renamed = name + "Result" + i;
    assigned[hash] = renamed;
}

// Emit definitions and rewrite placeholder refs everywhere.
foreach (var (hash, def) in defs)
{
    var name = assigned[hash];
    if (def["enum"] is JsonArray values) AddEnumNames(def, name, values);
    schemas[name] = def;
}

void RewriteRefs(JsonNode? n)
{
    switch (n)
    {
        case JsonObject o:
            if (o["$ref"] is JsonValue v && v.GetValue<string>() is var s && s.StartsWith("#hoisted/"))
                o["$ref"] = RefPrefix + assigned[s["#hoisted/".Length..]];
            foreach (var (_, child) in o.ToList()) RewriteRefs(child);
            break;
        case JsonArray a:
            foreach (var child in a) RewriteRefs(child);
            break;
    }
}
RewriteRefs(root);

// Top-level enums that were already named components also get idiomatic member names.
foreach (var (name, node) in schemas)
    if (node is JsonObject o && o["enum"] is JsonArray vals && o["x-ms-enum"] is null)
        AddEnumNames(o, name, vals);

// Sort components for stable diffs.
var sorted = new JsonObject();
foreach (var (k, v) in schemas.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())
{
    schemas.Remove(k);
    sorted[k] = v;
}
root["components"]!["schemas"] = sorted;

File.WriteAllText(args[1], root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Preprocessed: {sorted.Count} schemas ({defs.Count} hoisted from {occurrences.Sum(o => o.Value.Count)} inline occurrences).");
return 0;

// ---------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------
static void AddEnumNames(JsonObject def, string enumName, JsonArray values)
{
    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var mapped = new JsonArray();
    foreach (var v in values)
    {
        var raw = v?.ToString() ?? "";
        var member = Pascal(raw);
        if (member.Length == 0) member = "Empty";
        if (char.IsDigit(member[0])) member = "Value" + member;
        var unique = member;
        for (var i = 2; !used.Add(unique); i++) unique = member + i;
        mapped.Add(new JsonObject { ["value"] = v?.DeepClone(), ["name"] = unique });
    }
    def["x-ms-enum"] = new JsonObject { ["name"] = enumName, ["modelAsString"] = false, ["values"] = mapped };
}

static string? DiscriminatorLabel(JsonNode node)
{
    // A oneOf branch with a single-valued enum property (e.g. { op: enum["replace"] }) is named after that value.
    if (node is not JsonObject o || o["properties"] is not JsonObject props) return null;
    foreach (var (_, p) in props)
        if (p?["enum"] is JsonArray { Count: 1 } e) return Pascal(e[0]!.ToString());
    return null;
}

static string StripDto(string name) => Regex.Replace(name, "(Response|Body|Query|Request)?Dto$", "");

static string Pascal(string s)
{
    var parts = Regex.Split(s, "[^A-Za-z0-9]+").Where(p => p.Length > 0);
    var sb = new StringBuilder();
    foreach (var p in parts)
    {
        // Keep camelCase boundaries; normalise SHOUTING_CASE.
        var word = p.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? p[0] + p[1..].ToLowerInvariant() : char.ToUpperInvariant(p[0]) + p[1..];
        sb.Append(word);
    }
    return sb.ToString();
}

static List<string> Singularise(List<string> segments)
{
    var copy = new List<string>(segments);
    // Array envelopes ({ response: T[] }) are named after the owning schema: GetHosts.Response[] -> GetHost.
    if (copy.Count == 2 && copy[1] == "Response") copy.RemoveAt(1);
    var last = copy[^1];
    var singular = Words.Singular(last);
    copy[^1] = singular == last ? last + "Item" : singular;
    return copy;
}



record NameCandidate
{
    static readonly Regex Verbs = new("^(Get|Create|Update|Delete|Set|Clone|Reorder|Find)(?=[A-Z])");
    static readonly Regex ByClause = new("By(Uuid|ShortUuid|Username|Id|Tag|Email|TelegramId|Name)$");

    public string Full { get; }
    public string Short { get; }

    public NameCandidate(List<string> segments)
    {
        var tail = segments.Skip(1).Where(s => s != "Response").ToList();
        Full = Join(segments[0], tail);
        var head = ByClause.Replace(Verbs.Replace(segments[0], ""), "");
        Short = Join(head.Length > 0 ? head : segments[0], tail);
    }

    static string Join(string head, List<string> tail)
    {
        var acc = head;
        foreach (var seg in tail)
        {
            var words = Regex.Matches(acc, "[A-Z][a-z0-9]*").Select(m => m.Value).ToList();
            // Avoid stutter: "User" + "UserTraffic" -> "UserTraffic", "Passkeys" + "Passkey" -> "Passkey".
            var merged = false;
            for (var take = Math.Min(words.Count, 3); take > 0 && !merged; take--)
            {
                var suffix = string.Concat(words.TakeLast(take));
                foreach (var form in new[] { suffix, Words.Singular(suffix) })
                {
                    if (form.Length == 0 || !seg.StartsWith(form, StringComparison.Ordinal)) continue;
                    if (seg.Length > form.Length && !char.IsUpper(seg[form.Length])) continue; // word boundary
                    acc = acc[..^suffix.Length] + seg;
                    merged = true;
                    break;
                }
            }
            if (!merged) acc += seg;
        }
        return acc;
    }
}

static class Words
{
    public static readonly Regex Int64Properties = new(
        @"^(id|userId|userIds|nodeId|telegramId|total|count|\w*Count|\w*Bytes|memory(Total|Free|Used)|(rx|tx)Total|total(Users|Bills|BillingNodes|AvailableBillingNodes|Reports|UniqueDevices|HwidDevices|Online))$",
        RegexOptions.Compiled);
    public static readonly Regex Int32Properties = new(@"^(port)$", RegexOptions.Compiled);

    public static string Singular(string word) => word switch
{
        _ when word.EndsWith("Series") || word.EndsWith("Status") || word.EndsWith("Stats") || word.EndsWith("Settings") => word,
        _ when word.EndsWith("ies") => word[..^3] + "y",
        _ when word.EndsWith("sses") || word.EndsWith("xes") => word[..^2],
        _ when word.EndsWith("ss") || word.EndsWith("us") || word.EndsWith("is") => word,
        _ when word.EndsWith('s') => word[..^1],
        _ => word,
    };
}
