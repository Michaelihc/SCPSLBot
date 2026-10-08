using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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
        // Unity's native JSON reader avoids Mono's machine.config serializer sections.
        var asset = JsonUtility.FromJson<Asset>(reader.ReadToEnd());
        if (asset?.Blocks == null || asset.Blocks.Count == 0)
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
            foreach (Block block in asset.Blocks)
            {
                if (block.ParentId != asset.RootObjectId)
                    throw new InvalidOperationException("Arena asset must contain root-local blocks.");
                if (block.BlockType == 1)
                {
                    var toy = PrimitiveObjectToy.Get(ToyFactory.Create<AdminToys.PrimitiveObjectToy>(origin + block.Position.Vector,
                        Quaternion.Euler(block.Rotation.Vector), block.Scale.Vector));
                    toys.Add(toy);
                    toy.Type = (PrimitiveType)block.Properties.PrimitiveType;
                    toy.Flags = (AdminToys.PrimitiveFlags)block.Properties.PrimitiveFlags;
                    toy.Color = Color.white;
                    toy.IsStatic = true;
                    toy.Spawn();
                }
                else if (block.BlockType == 2)
                {
                    var light = LightSourceToy.Get(ToyFactory.Create<AdminToys.LightSourceToy>(origin + block.Position.Vector, Quaternion.identity, Vector3.one));
                    toys.Add(light);
                    light.Color = Color.white;
                    light.Type = LightType.Point;
                    light.Intensity = block.Properties.Intensity;
                    light.Range = block.Properties.Range;
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
    [Serializable] private sealed class Asset
    {
        public int RootObjectId;
        public List<Block> Blocks = new();
    }
    [Serializable] private sealed class Block
    {
        public int ParentId;
        public int BlockType;
        public Coordinates Position = new();
        public Coordinates Rotation = new();
        public Coordinates Scale = new();
        public Properties Properties = new();
    }
    [Serializable] private sealed class Coordinates
    {
        public float x;
        public float y;
        public float z;
        public Vector3 Vector => new(x, y, z);
    }
    [Serializable] private sealed class Properties
    {
        public int PrimitiveType;
        public int PrimitiveFlags;
        public float Intensity;
        public float Range;
    }
}
