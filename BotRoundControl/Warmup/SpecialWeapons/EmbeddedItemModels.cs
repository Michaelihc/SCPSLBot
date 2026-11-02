#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CustomItems;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;
using PrimitiveFlags = AdminToys.PrimitiveFlags;

namespace SCPSLBot.Warmup.SpecialWeapons;

/// <summary>
/// Reads MER item models embedded in this assembly, in metres with the grip at the origin, +Y up and +Z
/// forward. Groups fold into their children; a primitive parent stays a parent so shear rigs survive.
/// Deployment ships DLLs only, so models travel inside the plugin. Never modify the returned lists.
/// </summary>
internal static class EmbeddedItemModels
{
    private const int GroupBlock = 0;
    private const int PrimitiveBlock = 1;

    private static readonly Dictionary<string, IReadOnlyList<MeshPrimitive>> Cache = new(StringComparer.Ordinal);

    /// <summary>The model's primitives, or an empty list when it is missing or invalid.</summary>
    public static IReadOnlyList<MeshPrimitive> Load(string modelId)
    {
        if (!Cache.TryGetValue(modelId, out IReadOnlyList<MeshPrimitive>? model))
        {
            model = Read(modelId);
            Cache[modelId] = model;
        }

        return model;
    }

    private static IReadOnlyList<MeshPrimitive> Read(string modelId)
    {
        string resource = $"SpecialWeapons.{modelId}.mer.json";
        try
        {
            using Stream? stream = typeof(EmbeddedItemModels).Assembly.GetManifestResourceStream(resource);
            if (stream == null)
            {
                Logger.Warn($"[BotRoundControl:SpecialWeapons] Model '{resource}' is not embedded.");
                return Array.Empty<MeshPrimitive>();
            }

            using var reader = new StreamReader(stream);
            using JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
            return Parse(document.RootElement.GetProperty("Blocks"));
        }
        catch (Exception exception)
        {
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Model '{resource}' failed to load: {exception.GetBaseException().Message}");
            return Array.Empty<MeshPrimitive>();
        }
    }

    private static List<MeshPrimitive> Parse(JsonElement blocks)
    {
        var blockById = new Dictionary<int, JsonElement>();
        foreach (JsonElement block in blocks.EnumerateArray())
        {
            blockById[block.GetProperty("ObjectId").GetInt32()] = block;
        }

        var primitives = new List<MeshPrimitive>(blockById.Count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nameById = new Dictionary<int, string>();
        foreach (JsonElement block in blocks.EnumerateArray())
        {
            if (block.GetProperty("BlockType").GetInt32() != PrimitiveBlock)
            {
                continue;
            }

            int objectId = block.GetProperty("ObjectId").GetInt32();
            string name = UniqueName(block, objectId, usedNames);
            nameById[objectId] = name;

            Vector3 position = ReadVector(block, "Position", Vector3.zero);
            Quaternion orientation = Quaternion.Euler(ReadVector(block, "Rotation", Vector3.zero));
            Vector3 scale = ReadVector(block, "Scale", Vector3.one);
            string? parentName = null;
            int cursor = ParentId(block);
            while (cursor >= 0 && blockById.TryGetValue(cursor, out JsonElement ancestor))
            {
                if (ancestor.GetProperty("BlockType").GetInt32() != GroupBlock)
                {
                    nameById.TryGetValue(cursor, out parentName);
                    break;
                }

                Vector3 ancestorScale = ReadVector(ancestor, "Scale", Vector3.one);
                Quaternion ancestorRotation = Quaternion.Euler(ReadVector(ancestor, "Rotation", Vector3.zero));
                position = ReadVector(ancestor, "Position", Vector3.zero) + (ancestorRotation * Vector3.Scale(position, ancestorScale));
                orientation = ancestorRotation * orientation;
                scale = Vector3.Scale(scale, ancestorScale);
                cursor = ParentId(ancestor);
            }

            ReadAppearance(block, out PrimitiveType type, out PrimitiveFlags flags, out Color color);
            primitives.Add(new MeshPrimitive(name, position, orientation.eulerAngles, scale, type, color, flags, parentName));
        }

        return primitives;
    }

    private static string UniqueName(JsonElement block, int objectId, HashSet<string> used)
    {
        string name = block.TryGetProperty("Name", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
        if (name.Length == 0)
        {
            name = $"block_{objectId}";
        }

        // Names are the parent key, so a duplicate would re-target a rig child.
        if (!used.Add(name))
        {
            name = $"{name}#{objectId}";
            used.Add(name);
        }

        return name;
    }

    private static void ReadAppearance(JsonElement block, out PrimitiveType type, out PrimitiveFlags flags, out Color color)
    {
        type = PrimitiveType.Cube;
        flags = PrimitiveFlags.Visible;
        color = Color.white;
        if (!block.TryGetProperty("Properties", out JsonElement properties))
        {
            return;
        }

        if (properties.TryGetProperty("PrimitiveType", out JsonElement primitiveType))
        {
            type = (PrimitiveType)primitiveType.GetInt32();
        }

        if (properties.TryGetProperty("PrimitiveFlags", out JsonElement primitiveFlags))
        {
            flags = (PrimitiveFlags)primitiveFlags.GetInt32();
        }

        if (properties.TryGetProperty("Color", out JsonElement hex)
            && hex.ValueKind == JsonValueKind.String
            && ColorUtility.TryParseHtmlString((hex.GetString() ?? string.Empty).Trim(), out Color parsed))
        {
            color = parsed;
        }

        // Hex clamps channels to 0..1; the raw override above 1 is what marks a part emissive.
        if (properties.TryGetProperty("ColorRgba", out JsonElement hdr))
        {
            color = new Color(
                ReadFloat(hdr, "r", color.r),
                ReadFloat(hdr, "g", color.g),
                ReadFloat(hdr, "b", color.b),
                ReadFloat(hdr, "a", color.a));
        }
    }

    private static int ParentId(JsonElement block) =>
        block.TryGetProperty("ParentId", out JsonElement parent) ? parent.GetInt32() : -1;

    private static Vector3 ReadVector(JsonElement block, string key, Vector3 fallback)
    {
        if (!block.TryGetProperty(key, out JsonElement vector))
        {
            return fallback;
        }

        return new Vector3(ReadFloat(vector, "x", 0f), ReadFloat(vector, "y", 0f), ReadFloat(vector, "z", 0f));
    }

    private static float ReadFloat(JsonElement element, string key, float fallback) =>
        element.TryGetProperty(key, out JsonElement value) ? value.GetSingle() : fallback;
}
