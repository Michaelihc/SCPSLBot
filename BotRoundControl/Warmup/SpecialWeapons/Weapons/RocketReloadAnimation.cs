#nullable enable

using System;
using System.Collections.Generic;
using CustomItems;
using LabApi.Features.Wrappers;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;
using PrimitiveFlags = AdminToys.PrimitiveFlags;

namespace SCPSLBot.Warmup.SpecialWeapons.Weapons;

/// <summary>The launcher and its next round sharing one reload pose. Timing belongs to the weapon.</summary>
internal sealed class RocketReloadAnimation
{
    private const byte RawMovementSmoothing = 60;
    private const float HiddenScale = 0.001f;

    private readonly Player player;
    private readonly HeldMeshSpec spec;
    private readonly List<AdminToy> toys = new();
    private PlayerModelAttachment? attachment;
    private PrimitiveObjectToy? root;
    private PrimitiveObjectToy? rocketRoot;
    private Vector3 loadedPosition;
    private Vector3 breechPosition;
    private bool destroyed;

    public RocketReloadAnimation(Player player, HeldMeshSpec spec)
    {
        this.player = player;
        this.spec = spec;
    }

    public bool IsDestroyed => destroyed
        || attachment is { IsDestroyed: true }
        || root is null or { IsDestroyed: true };

    public bool Spawn()
    {
        IReadOnlyList<MeshPrimitive> rocket = EmbeddedItemModels.Load(GuidedRocketWeapon.ProjectileModel);
        if (player.IsDestroyed || spec.Primitives.Count == 0 || rocket.Count == 0)
        {
            return false;
        }

        loadedPosition = FindMarker("marker_bore", new Vector3(0f, 0.11f, 0.01f));
        breechPosition = FindMarker("marker_breech", new Vector3(0f, 0.11f, -0.475f)) - (Vector3.forward * 0.19f);
        try
        {
            // The carrier cancels native body scale, keeping the model at its physical size.
            attachment = new PlayerModelAttachment(player);
            if (!attachment.Spawn() || !TryComputeLocalPose(0f, out Vector3 position, out Quaternion rotation))
            {
                Destroy();
                return false;
            }

            root = CreateRoot(position, rotation, Vector3.one * Mathf.Max(0.02f, spec.Scale), attachment.Transform);
            foreach (MeshPrimitive primitive in spec.Primitives)
            {
                if (!IsMarker(primitive))
                {
                    AddPart(primitive, primitive.Position, primitive.Scale, root.Transform);
                }
            }

            rocketRoot = CreateRoot(Vector3.zero, Quaternion.identity, Vector3.one, root.Transform);
            foreach (MeshPrimitive primitive in rocket)
            {
                if (!IsMarker(primitive))
                {
                    // The flight model has deployed fins, folded here against the body while loading.
                    bool fin = primitive.Name?.StartsWith("fin_", StringComparison.OrdinalIgnoreCase) == true;
                    Vector3 folded = fin ? new Vector3(0.25f, 0.25f, 1f) : Vector3.one;
                    AddPart(primitive, Vector3.Scale(primitive.Position, folded), Vector3.Scale(primitive.Scale, folded), rocketRoot.Transform);
                }
            }

            Apply(0f);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warn($"[BotRoundControl:SpecialWeapons] Reload model failed: {exception.GetBaseException().Message}");
            Destroy();
            return false;
        }
    }

    public void Apply(float normalizedProgress)
    {
        if (IsDestroyed)
        {
            Destroy();
            return;
        }

        attachment!.UpdateScale();
        float progress = Mathf.Clamp01(normalizedProgress);
        float align = Smooth(Mathf.InverseLerp(0.17f, 0.42f, progress));
        float insert = Smooth(Mathf.InverseLerp(0.42f, 0.76f, progress));
        Vector3 hand = breechPosition + new Vector3(0.30f, -0.32f, -0.12f);
        if (TryComputeLocalPose(progress, out Vector3 position, out Quaternion rotation))
        {
            root!.Transform.SetLocalPositionAndRotation(position, rotation);
        }

        if (rocketRoot is { IsDestroyed: false })
        {
            rocketRoot.Transform.localPosition = progress < 0.42f
                ? Vector3.Lerp(hand, breechPosition, align)
                : Vector3.Lerp(breechPosition, loadedPosition, insert);
            rocketRoot.Transform.localRotation = Quaternion.Slerp(Quaternion.Euler(18f, -24f, 8f), Quaternion.identity, align);

            // Once seated, the round belongs inside the opaque launcher.
            rocketRoot.Scale = Vector3.one * (progress >= 0.78f ? HiddenScale : 1f);
        }
    }

    public void Destroy()
    {
        if (destroyed)
        {
            return;
        }

        destroyed = true;
        foreach (AdminToy toy in toys)
        {
            try
            {
                if (toy is { IsDestroyed: false })
                {
                    toy.Destroy();
                }
            }
            catch
            {
                // Round teardown can invalidate toys between the check and Destroy.
            }
        }

        toys.Clear();
        attachment?.Destroy();
        attachment = null;
    }

    private bool TryComputeLocalPose(float progress, out Vector3 localPosition, out Quaternion localRotation)
    {
        localPosition = Vector3.zero;
        localRotation = Quaternion.identity;
        if (player.IsDestroyed || attachment is null or { IsDestroyed: true })
        {
            return false;
        }

        Transform camera = player.Camera;
        float working = Mathf.Sin(Mathf.PI * Mathf.Clamp01(progress));
        float pose = working * (1f - (Smooth(Mathf.InverseLerp(0.80f, 1f, progress)) * 0.18f));

        // The launcher extends behind its grip, so the working pose brings the breech into view.
        var motionOffset = new Vector3(0.035f * pose, -0.20f * pose, 0.34f * pose);
        Quaternion motionRotation = Quaternion.Euler(12f * pose, -4f * pose, -17f * pose);
        Vector3 worldPosition = camera.position + (camera.rotation * (spec.CameraOffset + motionOffset));
        Quaternion worldRotation = camera.rotation * Quaternion.Euler(spec.RotationEuler) * motionRotation;
        Transform carrier = attachment.Transform;
        localPosition = carrier.InverseTransformPoint(worldPosition);
        localRotation = Quaternion.Inverse(carrier.rotation) * worldRotation;
        return true;
    }

    private PrimitiveObjectToy CreateRoot(Vector3 position, Quaternion rotation, Vector3 scale, Transform parent)
    {
        PrimitiveObjectToy toy = PrimitiveObjectToy.Create(position, rotation, scale, parent, networkSpawn: false);
        toy.Type = PrimitiveType.Cube;
        toy.Flags = PrimitiveFlags.None;
        toy.Color = Color.clear;
        toy.IsStatic = false;
        toy.Base.NetworkMovementSmoothing = RawMovementSmoothing;
        toy.SyncInterval = 0f;
        toys.Add(toy);
        toy.Spawn();
        return toy;
    }

    private void AddPart(MeshPrimitive primitive, Vector3 position, Vector3 scale, Transform parent)
    {
        PrimitiveObjectToy part = PrimitiveObjectToy.Create(position, Quaternion.Euler(primitive.Rotation), scale, parent, networkSpawn: false);
        part.Type = primitive.Type;
        part.Flags = primitive.Flags & ~PrimitiveFlags.Collidable;
        part.Color = primitive.Color;
        part.IsStatic = true;
        toys.Add(part);
        part.Spawn();
    }

    private Vector3 FindMarker(string name, Vector3 fallback)
    {
        foreach (MeshPrimitive primitive in spec.Primitives)
        {
            if (string.Equals(primitive.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return primitive.Position;
            }
        }

        return fallback;
    }

    private static bool IsMarker(MeshPrimitive primitive) =>
        primitive.Name?.StartsWith("marker_", StringComparison.OrdinalIgnoreCase) == true;

    private static float Smooth(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - (2f * t));
    }
}
