using System;
using System.Linq;
using AdminToys;
using UnityEngine;

namespace DuelArena;

internal static class ToyFactory
{
    // Dedicated servers do not populate NetworkClient.prefabs. Instantiate the
    // registered server prefab and keep its native NetworkIdentity/assetId.
    internal static T Create<T>(Vector3 position, Quaternion rotation, Vector3 scale) where T : AdminToyBase
    {
        T prefab = Mirror.LiteNetLib4Mirror.LiteNetLib4MirrorNetworkManager.singleton.spawnPrefabs
            .Select(p => p.GetComponent<T>()).FirstOrDefault(p => p != null);
        if (prefab == null) throw new InvalidOperationException($"Server toy prefab is unavailable: {typeof(T).Name}");
        T instance = UnityEngine.Object.Instantiate(prefab);
        instance.transform.SetPositionAndRotation(position, rotation);
        instance.transform.localScale = scale;
        return instance;
    }
}
