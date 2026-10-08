using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace DuelArena;

internal sealed class ArenaModel
{
    private readonly List<AdminToy> toys = new();
    public Vector3 Origin { get; private set; }
    public bool Ready => toys.Count != 0;
    public int PrimitiveCount => toys.Count(toy => toy is PrimitiveObjectToy);
    public int LightCount => toys.Count(toy => toy is LightSourceToy);
    public bool Contains(Vector3 position)
    {
        Vector3 local = position - Origin;
        return Math.Abs(local.x) < 10f && Math.Abs(local.z) < 15f && local.y > -2f && local.y < 8f;
    }
    public void Spawn(Vector3 origin)
    {
        Destroy();
        Origin = origin;
        using var stream = typeof(ArenaModel).Assembly.GetManifestResourceStream("DuelArena.arena.json");
        if (stream == null) throw new InvalidOperationException("Bundled arena model is missing.");
        using var reader = new StreamReader(stream);
        // Use the game's shipped JSON parser without Mono machine.config or Unity type registration.
        using var asset = JsonDocument.Parse(reader.ReadToEnd());
        int rootObjectId = asset.RootElement.GetProperty("RootObjectId").GetInt32();
        JsonElement blocks = asset.RootElement.GetProperty("Blocks");
        if (blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() == 0)
            throw new InvalidOperationException("Bundled arena model has no blocks.");
        try
        {
            // FPC positions are compressed relative to a native waypoint within
            // +/-128 meters. Give the elevated arena its own bounded origin.
            var waypoint = WaypointToy.Get(ToyFactory.Create<AdminToys.WaypointToy>(origin, Quaternion.identity, Vector3.one));
            toys.Add(waypoint);
            waypoint.BoundsSize = new Vector3(40, 30, 50);
            waypoint.PriorityBias = 50;
            waypoint.VisualizeBounds = false;
            waypoint.Spawn();
            foreach (JsonElement block in blocks.EnumerateArray())
            {
                if (block.GetProperty("ParentId").GetInt32() != rootObjectId)
                    throw new InvalidOperationException("Arena asset must contain root-local blocks.");
                int blockType = block.GetProperty("BlockType").GetInt32();
                Vector3 position = Coordinates(block.GetProperty("Position"));
                JsonElement properties = block.GetProperty("Properties");
                if (blockType == 1)
                {
                    var toy = PrimitiveObjectToy.Get(ToyFactory.Create<AdminToys.PrimitiveObjectToy>(origin + position,
                        Quaternion.Euler(Coordinates(block.GetProperty("Rotation"))), Coordinates(block.GetProperty("Scale"))));
                    toys.Add(toy);
                    toy.Type = (PrimitiveType)properties.GetProperty("PrimitiveType").GetInt32();
                    toy.Flags = (AdminToys.PrimitiveFlags)properties.GetProperty("PrimitiveFlags").GetInt32();
                    toy.Color = Color.white;
                    toy.IsStatic = true;
                    toy.Spawn();
                }
                else if (blockType == 2)
                {
                    var light = LightSourceToy.Get(ToyFactory.Create<AdminToys.LightSourceToy>(origin + position, Quaternion.identity, Vector3.one));
                    toys.Add(light);
                    light.Color = Color.white;
                    light.Type = LightType.Point;
                    light.Intensity = properties.GetProperty("Intensity").GetSingle();
                    light.Range = properties.GetProperty("Range").GetSingle();
                    light.ShadowType = LightShadows.None;
                    light.IsStatic = true;
                    light.Spawn();
                }
                else throw new InvalidOperationException("Unsupported arena block.");
            }
        }
        catch { Destroy(); throw; }
    }
    public void Destroy()
    {
        foreach (AdminToy toy in toys)
            if (toy.Base != null) toy.Destroy();
        toys.Clear();
    }
    private static Vector3 Coordinates(JsonElement value) => new(
        value.GetProperty("x").GetSingle(), value.GetProperty("y").GetSingle(), value.GetProperty("z").GetSingle());
}
