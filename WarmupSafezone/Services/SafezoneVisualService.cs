using System;
using System.Collections.Generic;
using System.Linq;
using AdminToys;
using LabApi.Features.Enums;
using LabApi.Features.Wrappers;
using Mirror;
using ScpslPluginStarter.Core;
using UnityEngine;
using BasePrimitiveObjectToy = AdminToys.PrimitiveObjectToy;
using BaseTextToy = AdminToys.TextToy;
using PrimitiveObjectToy = LabApi.Features.Wrappers.PrimitiveObjectToy;
using TextToy = LabApi.Features.Wrappers.TextToy;

namespace ScpslPluginStarter.Services;

internal sealed class SafezoneVisualService
{
    internal const float Scp914PanelScaleMultiplier = 10f;
    internal const float Scp914PanelTextScale = 0.12f;

    private readonly WarmupSafezoneConfig _config;
    private readonly WarmupLocalization _localization;
    private readonly List<AdminToy> _toys = new();
    private string _renderedSurfaceSignature = string.Empty;

    public SafezoneVisualService(
        WarmupSafezoneConfig config,
        WarmupLocalization localization)
    {
        _config = config;
        _localization = localization;
    }

    public void Ensure()
    {
        if (!_config.Enabled || !_config.SafezoneVisualsEnabled || !CanSpawnVisualToys())
        {
            Destroy();
            return;
        }

        string surfaceSignature = SurfaceSignature();
        int expectedSurfaceToys = SurfaceSafezoneGeometry.NormalizeAxis(_config.SurfaceEscapeSafezoneAxis) == "z" ? 5 : 4;
        Door? scp914Gate = _config.Scp914SafezoneEnabled ? Door.Get(DoorName.Lcz914Gate) : null;
        int expected914Toys = scp914Gate != null && !scp914Gate.IsDestroyed ? 4 : 0;
        bool geometryChanged = !string.Equals(_renderedSurfaceSignature, surfaceSignature, StringComparison.Ordinal);
        bool toysMissing = _toys.Count != expectedSurfaceToys + expected914Toys
            || _toys.Any(toy => toy == null || toy.IsDestroyed);
        if (!geometryChanged && !toysMissing)
        {
            return;
        }

        Destroy();
        CreateConfiguredSurfaceBoundary();

        if (scp914Gate != null && !scp914Gate.IsDestroyed)
        {
            CreateScp914Panel(scp914Gate);
        }

        _renderedSurfaceSignature = surfaceSignature;
    }

    public void Destroy()
    {
        foreach (AdminToy toy in _toys.Where(toy => toy != null).ToArray())
        {
            if (!toy.IsDestroyed)
            {
                toy.Destroy();
            }
        }

        _toys.Clear();
        _renderedSurfaceSignature = string.Empty;
    }

    private void CreateConfiguredSurfaceBoundary()
    {
        const float thickness = 0.08f;
        Color color = new(0.25f, 0.85f, 1f, 0.35f);
        string label = _localization.Shared("SAFE ZONE", "安全区");
        switch (SurfaceSafezoneGeometry.NormalizeAxis(_config.SurfaceEscapeSafezoneAxis))
        {
            case "x":
                CreateWall(new Vector3(_config.SurfaceEscapeSafezoneMaxZ, 295f, 0f), new Vector3(thickness, 36f, 260f), color);
                CreateWall(new Vector3(_config.SurfaceEscapeSafezoneMaxZ + 0.1f, 295f, 0f), new Vector3(thickness, 36f, 260f), color);
                CreateSurfaceLabel(new Vector3(_config.SurfaceEscapeSafezoneMaxZ + 0.18f, 300f, 0f), Quaternion.Euler(0f, 90f, 0f), label);
                CreateSurfaceLabel(new Vector3(_config.SurfaceEscapeSafezoneMaxZ - 0.18f, 300f, 0f), Quaternion.Euler(0f, -90f, 0f), label);
                break;

            case "y":
                CreateWall(new Vector3(125f, _config.SurfaceEscapeSafezoneMaxZ, 0f), new Vector3(260f, thickness, 260f), color);
                CreateWall(new Vector3(125f, _config.SurfaceEscapeSafezoneMaxZ + 0.1f, 0f), new Vector3(260f, thickness, 260f), color);
                CreateSurfaceLabel(new Vector3(125f, _config.SurfaceEscapeSafezoneMaxZ + 0.18f, 0f), Quaternion.Euler(90f, 0f, 0f), label);
                CreateSurfaceLabel(new Vector3(125f, _config.SurfaceEscapeSafezoneMaxZ - 0.18f, 0f), Quaternion.Euler(-90f, 0f, 0f), label);
                break;

            default:
                float minX = _config.SurfaceEscapeSafezoneMinX;
                const float maxX = 260f;
                float width = Mathf.Max(1f, maxX - minX);
                float centerX = minX + (width * 0.5f);
                CreateWall(new Vector3(centerX, 295f, _config.SurfaceEscapeSafezoneMaxZ - 0.05f), new Vector3(width, 36f, thickness), color);
                CreateWall(new Vector3(centerX, 295f, _config.SurfaceEscapeSafezoneMaxZ + 0.05f), new Vector3(width, 36f, thickness), color);
                Vector3 labelCenter = new(136.45f, 295.8f, _config.SurfaceEscapeSafezoneMaxZ + 0.14f);
                CreateSurfaceLabel(labelCenter + Vector3.up * 0.6f, Quaternion.identity, label);
                CreateSurfaceLabel(labelCenter, Quaternion.identity, label);
                CreateSurfaceLabel(labelCenter - Vector3.up * 0.6f, Quaternion.identity, label);
                break;
        }
    }

    private void CreateSurfaceLabel(Vector3 position, Quaternion rotation, string text) =>
        CreateWorldLabel(position, rotation, text, null, new Vector3(0.32f, 0.32f, 0.32f), new Vector2(80f, 4f));

    private void CreateScp914Panel(Door door)
    {
        string english = NormalizeLegacyPanelText(_config.Scp914SafezonePanelTextEnglish, false);
        string chinese = NormalizeLegacyPanelText(_config.Scp914SafezonePanelTextChinese, true);
        string text = _localization.Shared(english, chinese);
        CreatePanelFace(door.Transform, 0.16f, Quaternion.identity, text);
        CreatePanelFace(door.Transform, -0.16f, Quaternion.Euler(0f, 180f, 0f), text);
    }

    private void CreatePanelFace(Transform parent, float localZ, Quaternion rotation, string text)
    {
        PrimitiveObjectToy backing = PrimitiveObjectToy.Create(
            new Vector3(0f, 1.85f, localZ),
            rotation,
            new Vector3(1.15f, 0.55f, 0.025f) * Scp914PanelScaleMultiplier,
            parent,
            false);
        backing.Type = PrimitiveType.Cube;
        backing.Flags = PrimitiveFlags.Visible;
        backing.Color = new Color(0.02f, 0.14f, 0.17f, 0.96f);
        backing.IsStatic = true;
        backing.SyncInterval = 0f;
        backing.Spawn();
        _toys.Add(backing);

        float textZ = localZ > 0f ? localZ + 0.02f : localZ - 0.02f;
        CreateWorldLabel(
            new Vector3(0f, 1.85f, textZ),
            rotation,
            text,
            parent,
            new Vector3(Scp914PanelTextScale, Scp914PanelTextScale, Scp914PanelTextScale),
            new Vector2(12f, 4f));
    }

    private void CreateWall(Vector3 position, Vector3 scale, Color color)
    {
        PrimitiveObjectToy wall = PrimitiveObjectToy.Create(position, Quaternion.identity, scale, null, false);
        wall.Type = PrimitiveType.Cube;
        wall.Flags = PrimitiveFlags.Visible;
        wall.Color = color;
        wall.IsStatic = true;
        wall.SyncInterval = 0f;
        wall.Spawn();
        _toys.Add(wall);
    }

    private void CreateWorldLabel(
        Vector3 position,
        Quaternion rotation,
        string text,
        Transform? parent,
        Vector3? scale = null,
        Vector2? displaySize = null)
    {
        TextToy label = TextToy.Create(position, rotation, scale ?? new Vector3(0.24f, 0.24f, 0.24f), parent, false);
        label.TextFormat = $"<alpha=#FF><align=center><b><color=#42F5E9>{text}</color></b></align>";
        label.DisplaySize = displaySize ?? new Vector2(40f, 4f);
        label.IsStatic = true;
        label.SyncInterval = 0f;
        label.Spawn();
        _toys.Add(label);
    }

    private static string NormalizeLegacyPanelText(string? configured, bool chinese)
    {
        string text = configured ?? string.Empty;
        if (text.IndexOf("godmode", StringComparison.OrdinalIgnoreCase) >= 0 || text.Contains("无敌"))
        {
            return chinese ? "安全区\n禁止造成或受到伤害" : "SAFE ZONE\nDAMAGE BLOCKED";
        }

        return string.IsNullOrWhiteSpace(text)
            ? (chinese ? "安全区\n禁止造成或受到伤害" : "SAFE ZONE\nDAMAGE BLOCKED")
            : text;
    }

    private string SurfaceSignature() => string.Join("|",
        SurfaceSafezoneGeometry.NormalizeAxis(_config.SurfaceEscapeSafezoneAxis),
        _config.SurfaceEscapeSafezoneMaxZ,
        _config.SurfaceEscapeSafezoneLessThan,
        _config.SurfaceEscapeSafezoneMinX);

    private static bool CanSpawnVisualToys()
    {
        if (NetworkClient.prefabs == null)
        {
            return false;
        }

        bool primitive = false;
        bool text = false;
        foreach (GameObject prefab in NetworkClient.prefabs.Values)
        {
            if (prefab == null)
            {
                continue;
            }

            primitive |= prefab.GetComponent<BasePrimitiveObjectToy>() != null;
            text |= prefab.GetComponent<BaseTextToy>() != null;
            if (primitive && text)
            {
                return true;
            }
        }

        return false;
    }
}
