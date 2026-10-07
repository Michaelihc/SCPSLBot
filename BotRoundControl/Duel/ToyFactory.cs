using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AdminToys;
using UnityEngine;

namespace DuelArena;

internal static class ToyFactory
{
    // Dedicated servers do not populate NetworkClient.prefabs. Instantiate the
    // registered server prefab and keep its native NetworkIdentity/assetId.
    internal static T Create<T>(Vector3 position, Quaternion rotation, Vector3 scale) where T : AdminToyBase
    {
        if (typeof(T) == typeof(AdminToys.PrimitiveObjectToy)) EnsureCubeMesh();
        T prefab = Mirror.LiteNetLib4Mirror.LiteNetLib4MirrorNetworkManager.singleton.spawnPrefabs
            .Select(p => p.GetComponent<T>()).FirstOrDefault(p => p != null);
        if (prefab == null) throw new InvalidOperationException($"Server toy prefab is unavailable: {typeof(T).Name}");
        T instance = UnityEngine.Object.Instantiate(prefab);
        instance.transform.SetPositionAndRotation(position, rotation);
        instance.transform.localScale = scale;
        return instance;
    }

    private static void EnsureCubeMesh()
    {
        // The native primitive cache is initialized by OnClientReady, which a
        // dedicated server never receives. Native Start needs the cube mesh to
        // construct its authoritative collider. Preserve an existing cache.
        var meshes = typeof(AdminToys.PrimitiveObjectToy)
            .GetField("PrimitiveTypeToMesh", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as Dictionary<PrimitiveType, Mesh>
            ?? throw new InvalidOperationException("Native primitive mesh cache is unavailable.");
        if (meshes.TryGetValue(PrimitiveType.Cube, out Mesh mesh) && mesh != null) return;
        GameObject source = GameObject.CreatePrimitive(PrimitiveType.Cube);
        source.SetActive(false);
        try { meshes[PrimitiveType.Cube] = source.GetComponent<MeshFilter>().sharedMesh; }
        finally { UnityEngine.Object.Destroy(source); }
    }
}
