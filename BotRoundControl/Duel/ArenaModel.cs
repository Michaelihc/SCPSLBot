using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace DuelArena;

internal sealed class ArenaModel
{
    private readonly List<AdminToy> toys = new();
    public Vector3 Origin { get; private set; }
    public bool Ready => toys.Count != 0;
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
        var asset = (Asset)new DataContractJsonSerializer(typeof(Asset)).ReadObject(stream);
        try
        {
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
    [DataContract] private sealed class Asset
    {
        [DataMember] public int RootObjectId;
        [DataMember] public List<Block> Blocks = new();
    }
    [DataContract] private sealed class Block
    {
        [DataMember] public int ParentId;
        [DataMember] public int BlockType;
        [DataMember] public Coordinates Position = new();
        [DataMember] public Coordinates Rotation = new();
        [DataMember] public Coordinates Scale = new();
        [DataMember] public Properties Properties = new();
    }
    [DataContract] private sealed class Coordinates
    {
        [DataMember] public float x;
        [DataMember] public float y;
        [DataMember] public float z;
        public Vector3 Vector => new(x, y, z);
    }
    [DataContract] private sealed class Properties
    {
        [DataMember] public int PrimitiveType;
        [DataMember] public int PrimitiveFlags;
        [DataMember] public float Intensity;
        [DataMember] public float Range;
    }
}
