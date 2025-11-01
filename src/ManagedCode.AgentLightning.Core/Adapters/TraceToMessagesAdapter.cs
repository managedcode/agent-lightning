using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using ManagedCode.AgentLightning.Core.Tracing;

namespace ManagedCode.AgentLightning.Core.Adapters;

public sealed record OpenAiToolCall(string Id, string Name, string Arguments);

public sealed record OpenAiFunctionDefinition(string Name, string? Description, object? Parameters);

public sealed record OpenAiMessage(
    string Role,
    string? Content,
    string? ToolCallId = null,
    IReadOnlyList<OpenAiToolCall>? ToolCalls = null);

public sealed record OpenAiConversation(
    IReadOnlyList<OpenAiMessage> Messages,
    IReadOnlyList<OpenAiFunctionDefinition>? Tools);

public sealed class TraceToMessagesAdapter : TraceAdapter<IReadOnlyList<OpenAiConversation>>
{
    protected override IReadOnlyList<OpenAiConversation> ConvertCore(IReadOnlyList<SpanModel> source)
    {
        if (source.Count == 0)
        {
            return Array.Empty<OpenAiConversation>();
        }

        var rawEntries = new List<RawSpanInfo>();

        foreach (var span in source)
        {
            var prompt = ToDictionaryList(GroupGenAi(span.Attributes, "gen_ai.prompt"), "prompt");
            var completion = ToDictionaryList(GroupGenAi(span.Attributes, "gen_ai.completion"), "completion");
            var request = ToDictionary(GroupGenAi(span.Attributes, "gen_ai.request"), "request");
            var response = ToDictionary(GroupGenAi(span.Attributes, "gen_ai.response"), "response");

            if (prompt.Count == 0 && completion.Count == 0 && request.Count == 0 && response.Count == 0)
            {
                continue;
            }

            var tools = GetToolCalls(span, source);
            rawEntries.Add(new RawSpanInfo(prompt, completion, request, response, tools));
        }

        var conversations = new List<OpenAiConversation>(rawEntries.Count);

        foreach (var entry in rawEntries)
        {
            conversations.Add(ConvertRawEntry(entry));
        }

        return conversations;
    }

    private static List<Dictionary<string, object?>> GetToolCalls(SpanModel completion, IReadOnlyList<SpanModel> allSpans)
    {
        var result = new List<Dictionary<string, object?>>();

        foreach (var candidate in allSpans)
        {
            if (!string.Equals(candidate.ParentId, completion.SpanId, StringComparison.Ordinal))
            {
                continue;
            }

            var tool = GroupGenAi(candidate.Attributes, "tool");
            if (tool is Dictionary<string, object?> dict && dict.Count > 0)
            {
                result.Add(dict);
            }
        }

        return result;
    }

    private static OpenAiConversation ConvertRawEntry(RawSpanInfo entry)
    {
        var messages = new List<OpenAiMessage>();

        foreach (var prompt in entry.Prompt)
        {
            var role = GetRequiredString(prompt, "role", "prompt");
            var toolCalls = new List<OpenAiToolCall>();

            if (prompt.TryGetValue("tool_calls", out var callObj))
            {
                toolCalls.AddRange(ConvertToolCalls(callObj));
            }

            var content = prompt.TryGetValue("content", out var contentObj) ? contentObj?.ToString() : null;
            var toolCallId = prompt.TryGetValue("tool_call_id", out var toolCallIdObj) ? toolCallIdObj?.ToString() : null;

            messages.Add(
                toolCalls.Count > 0
                    ? new OpenAiMessage(role, content, toolCallId, toolCalls)
                    : new OpenAiMessage(role, content, toolCallId));
        }

        var completionToolCalls = entry.Tools.Select(ConvertToolCallFromSpan).Where(call => call is not null).Cast<OpenAiToolCall>()
            .ToList();

        foreach (var completion in entry.Completion)
        {
            var role = completion.TryGetValue("role", out var roleObj) ? roleObj?.ToString() ?? "assistant" : "assistant";
            var content = completion.TryGetValue("content", out var contentObj) ? contentObj?.ToString() : null;

            messages.Add(
                completionToolCalls.Count > 0
                    ? new OpenAiMessage(role, content, null, completionToolCalls)
                    : new OpenAiMessage(role, content));
        }

        IReadOnlyList<OpenAiFunctionDefinition>? tools = null;
        if (entry.Request.TryGetValue("functions", out var functionsObj))
        {
            var functionDefs = ToDictionaryList(functionsObj, "functions");
            if (functionDefs.Count > 0)
            {
                var list = new List<OpenAiFunctionDefinition>(functionDefs.Count);
                foreach (var fn in functionDefs)
                {
                    var name = GetRequiredString(fn, "name", "function");
                    fn.TryGetValue("description", out var descObj);
                    fn.TryGetValue("parameters", out var parametersObj);
                    list.Add(new OpenAiFunctionDefinition(name, descObj?.ToString(), parametersObj));
                }

                tools = list;
            }
        }

        return new OpenAiConversation(messages, tools);
    }

    private static IEnumerable<OpenAiToolCall> ConvertToolCalls(object? toolCallsObj)
    {
        foreach (var dict in ToDictionaryList(toolCallsObj, "tool_calls"))
        {
            var id = GetOptionalString(dict, "id") ?? Guid.NewGuid().ToString("N");
            var name = GetOptionalString(dict, "name") ?? "tool";
            var arguments = dict.TryGetValue("arguments", out var argsObj) ? SerializeArguments(argsObj) : "{}";
            yield return new OpenAiToolCall(id, name, arguments);
        }
    }

    private static OpenAiToolCall? ConvertToolCallFromSpan(Dictionary<string, object?> toolSpan)
    {
        if (!toolSpan.TryGetValue("call", out var callObj) || callObj is not Dictionary<string, object?> callDict)
        {
            return null;
        }

        var id = GetOptionalString(callDict, "id") ?? Guid.NewGuid().ToString("N");
        var arguments = "{}";
        if (toolSpan.TryGetValue("parameters", out var parameters))
        {
            arguments = SerializeArguments(parameters);
        }

        var name = GetOptionalString(toolSpan, "name") ?? "tool";
        return new OpenAiToolCall(id, name, arguments);
    }

    private static string SerializeArguments(object? arguments) =>
        arguments switch
        {
            null => "{}",
            string s => s,
            _ => JsonSerializer.Serialize(arguments),
        };

    private static string GetRequiredString(Dictionary<string, object?> dictionary, string key, string context)
    {
        if (!dictionary.TryGetValue(key, out var value) || value is null)
        {
            throw new InvalidOperationException($"Missing required '{key}' in {context} payload.");
        }

        return value switch
        {
            string s => s,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? throw new InvalidOperationException($"Unable to convert '{key}' to string"),
        };
    }

    private static string? GetOptionalString(Dictionary<string, object?> dictionary, string key)
    {
        if (!dictionary.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    private static List<Dictionary<string, object?>> ToDictionaryList(object? value, string context)
    {
        if (value is null)
        {
            return new List<Dictionary<string, object?>>();
        }

        if (value is List<object?> objectList)
        {
            var result = new List<Dictionary<string, object?>>();
            foreach (var item in objectList)
            {
                if (item is Dictionary<string, object?> dict)
                {
                    result.Add(dict);
                }
                else
                {
                    throw new InvalidOperationException($"Expected dictionary entries in {context} list.");
                }
            }

            return result;
        }

        if (value is List<Dictionary<string, object?>> dictionaryList)
        {
            return new List<Dictionary<string, object?>>(dictionaryList);
        }

        throw new InvalidOperationException($"Unsupported value encountered in {context} list conversion.");
    }

    private static Dictionary<string, object?> ToDictionary(object? value, string context)
    {
        if (value is null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        if (value is Dictionary<string, object?> dict)
        {
            return dict;
        }

        throw new InvalidOperationException($"Expected dictionary for {context} conversion.");
    }

    private static object? GroupGenAi(IReadOnlyDictionary<string, object?> attributes, string prefix)
    {
        var relevant = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            if (key.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                var trimmed = key[(prefix.Length + 1)..];
                relevant[trimmed] = value;
            }
        }

        if (relevant.Count == 0)
        {
            return null;
        }

        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in relevant)
        {
            var parts = key.Split('.');
            InsertNested(root, parts, 0, value);
        }

        return NormalizeNode(root);
    }

    private static void InsertNested(Dictionary<string, object?> root, IReadOnlyList<string> parts, int index, object? value)
    {
        if (index >= parts.Count)
        {
            return;
        }

        var part = parts[index];
        if (index == parts.Count - 1)
        {
            root[part] = value;
            return;
        }

        if (!root.TryGetValue(part, out var childObj) || childObj is not Dictionary<string, object?> childDict)
        {
            childDict = new Dictionary<string, object?>(StringComparer.Ordinal);
            root[part] = childDict;
        }

        InsertNested(childDict, parts, index + 1, value);
    }

    private static object NormalizeNode(object node)
    {
        if (node is Dictionary<string, object?> dict)
        {
            var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in dict)
            {
                if (value is Dictionary<string, object?> childDict)
                {
                    normalized[key] = NormalizeNode(childDict);
                }
                else if (value is List<object?> childList)
                {
                    normalized[key] = NormalizeNode(childList);
                }
                else
                {
                    normalized[key] = value;
                }
            }

            if (normalized.Count > 0 && AllKeysAreInts(normalized.Keys))
            {
                var maxIndex = normalized.Keys.Select(k => int.Parse(k, CultureInfo.InvariantCulture)).Max();
                var resultList = new List<object?>(new object?[maxIndex + 1]);
                foreach (var (key, value) in normalized)
                {
                    var index = int.Parse(key, CultureInfo.InvariantCulture);
                    resultList[index] = value is null ? null : NormalizeNode(value);
                }

                return resultList;
            }

            return normalized;
        }

        if (node is List<object?> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var value = list[i];
                list[i] = value is null ? null : NormalizeNode(value);
            }

            return list;
        }

        return node;
    }

    private static bool AllKeysAreInts(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record RawSpanInfo(
        IReadOnlyList<Dictionary<string, object?>> Prompt,
        IReadOnlyList<Dictionary<string, object?>> Completion,
        Dictionary<string, object?> Request,
        Dictionary<string, object?> Response,
        IReadOnlyList<Dictionary<string, object?>> Tools);
}
