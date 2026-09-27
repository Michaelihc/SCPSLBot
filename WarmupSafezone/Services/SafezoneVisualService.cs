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
    // Opaque plate sized to the two text lines (about 2.9 m x 0.85 m as rendered) plus a margin, so the gate stays
    // visible around it while the plate still hides the mirrored text of the opposite face.
    internal static readonly Vector3 Scp914PanelPlateSize = new(4f, 3f, 0.025f);

    // The text block renders about 1.65 m below its toy anchor; the plate is centred on the rendered text.
    internal const float Scp914PanelPlateDrop = 1f;
    internal const float Scp914PanelTextScale = 0.12f;

    // The closed gate leaves extend up to about 0.36 gate-local units either side of the origin; both faces sit
    // clear of them so the leaves cannot hide the panel text.
    internal const float Scp914PanelFaceOffset = 0.6f;

    // Wide enough that each configured panel line stays on one line at the normal text scale.
    internal static readonly Vector2 Scp914PanelTextDisplaySize = new(240f, 60f);

    // Clearance, in gate-local units, between the text and its backing; 0.02 let the backing hide the text.
    internal const float Scp914PanelTextGap = 0.15f;

    private readonly WarmupSafezoneConfig _config;
    private readonly WarmupLocalization _localization;
    private readonly List<AdminToy> _toys = new();
    private readonly List<(string Kind, Vector3 Position)> _panelParts = new();
    private string _renderedSurfaceSignature = string.Empty;

    public SafezoneVisualService(
        WarmupSafezoneConfig config,
        WarmupLocalization localization)
    {
        _config = config;
        _localization = localization;
    }

    public int LiveToyCount => _toys.Count(toy => toy != null && !toy.IsDestroyed);

    public string Describe914Gate()
    {
        Door? gate = Door.Get(DoorName.Lcz914Gate);
        if (gate == null || gate.IsDestroyed)
        {
            return "none";
        }

        Vector3 p = gate.Transform.position;
        Vector3 f = gate.Transform.forward;
        Vector3 s = gate.Transform.lossyScale;
        string faces = string.Join(";", _panelParts.Select(part => FormattableString.Invariant($"{part.Kind}=({part.Position.x:0.##},{part.Position.y:0.##},{part.Position.z:0.##})")));
        return FormattableString.Invariant($"({p.x:0.##},{p.y:0.##},{p.z:0.##}) forward=({f.x:0.##},{f.y:0.##},{f.z:0.##}) scale=({s.x:0.##},{s.y:0.##},{s.z:0.##}) panel=[{faces}]");
    }

    public void Ensure()
    {
        if (!_config.Enabled || !_config.SafezoneVisualsEnabled || !CanSpawnVisualToys())
        {
            Destroy();
            return;
        }

        Door? scp914Gate = _config.Scp914SafezoneEnabled ? Door.Get(DoorName.Lcz914Gate) : null;
        int expected914Toys = scp914Gate != null && !scp914Gate.IsDestroyed ? 4 : 0;
        bool hasCellsTile = _config.ClassDCellsSafezoneEnabled && ClassDCellsTile.TryGet(out _, out _);
        int expectedCellsToys = hasCellsTile ? 8 : 0;
        string surfaceSignature = SurfaceSignature() + "|" + CellsSignature(hasCellsTile);
        int expectedSurfaceToys = SurfaceSafezoneGeometry.NormalizeAxis(_config.SurfaceEscapeSafezoneAxis) == "z" ? 3 : 4;
        bool geometryChanged = !string.Equals(_renderedSurfaceSignature, surfaceSignature, StringComparison.Ordinal);
        bool toysMissing = _toys.Count != expectedSurfaceToys + expected914Toys + expectedCellsToys
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

        if (hasCellsTile)
        {
            CreateClassDCellsBoundary();
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
        _panelParts.Clear();
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
                CreateSurfaceLabel(new Vector3(136.45f, 295.8f, _config.SurfaceEscapeSafezoneMaxZ + 0.14f), Quaternion.identity, label);
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
        // Text reads correctly when the camera looks along its forward axis, so each face's text
        // sits on its outer side and points back toward the gate.
        CreatePanelFace(door.Transform, Scp914PanelFaceOffset, Quaternion.Euler(0f, 180f, 0f), text);
        CreatePanelFace(door.Transform, -Scp914PanelFaceOffset, Quaternion.identity, text);
    }

    // Faces are unparented world objects posed from the static gate. Only the backing's face size
    // is scaled; its depth stays thin so the text in front of it is not buried inside the box.
    private void CreatePanelFace(Transform door, float localZ, Quaternion localRotation, string text)
    {
        Quaternion rotation = door.rotation * localRotation;
        PrimitiveObjectToy backing = PrimitiveObjectToy.Create(
            door.TransformPoint(new Vector3(0f, 1.85f, localZ)) + Vector3.down * Scp914PanelPlateDrop,
            rotation,
            Scp914PanelPlateSize,
            null,
            false);
        backing.Type = PrimitiveType.Cube;
        backing.Flags = PrimitiveFlags.Visible;
        // Fully opaque: only a depth-writing backing hides the mirrored text of the opposite face. Translucent
        // backings (alpha 0.45 and 0.75 were tried) let that text render through at nearly full strength.
        backing.Color = new Color(0.02f, 0.14f, 0.17f, 1f);
        backing.IsStatic = true;
        backing.SyncInterval = 0f;
        backing.Spawn();
        _toys.Add(backing);
        _panelParts.Add(("plate", backing.Position));

        float textZ = localZ > 0f ? localZ + Scp914PanelTextGap : localZ - Scp914PanelTextGap;
        _panelParts.Add(("text", door.TransformPoint(new Vector3(0f, 1.85f, textZ))));
        CreateWorldLabel(
            door.TransformPoint(new Vector3(0f, 1.85f, textZ)),
            rotation,
            text,
            null,
            new Vector3(Scp914PanelTextScale, Scp914PanelTextScale, Scp914PanelTextScale),
            Scp914PanelTextDisplaySize);
    }

    private void CreateClassDCellsBoundary()
    {
        if (!ClassDCellsTile.TryGet(out Vector3Int coords, out Room? room) || room == null)
        {
            return;
        }

        // The safezone is the whole room, so the bound is drawn only across main-tile edges that hold an
        // exit door. One face just inside and one just outside, so it shows in front of a closed door that
        // sits exactly on the grid edge from either side.
        const float height = 5f;
        const float thickness = 0.04f;
        const float offset = 0.25f;
        Color color = new(0.25f, 0.85f, 1f, 0.35f);
        Bounds tile = ClassDCellsTile.FloorBounds(coords, room.Position.y);
        float y = room.Position.y + (height * 0.5f) - 0.2f;
        List<Vector3> exits = ExitDoorPositions(room, tile).ToList();
        foreach (float side in new[] { -offset, offset })
        {
            if (exits.Any(p => Mathf.Abs(p.z - tile.min.z) < 1f))
                CreateWall(new Vector3(tile.center.x, y, tile.min.z - side), new Vector3(tile.size.x, height, thickness), color);
            if (exits.Any(p => Mathf.Abs(p.z - tile.max.z) < 1f))
                CreateWall(new Vector3(tile.center.x, y, tile.max.z + side), new Vector3(tile.size.x, height, thickness), color);
            if (exits.Any(p => Mathf.Abs(p.x - tile.min.x) < 1f))
                CreateWall(new Vector3(tile.min.x - side, y, tile.center.z), new Vector3(thickness, height, tile.size.z), color);
            if (exits.Any(p => Mathf.Abs(p.x - tile.max.x) < 1f))
                CreateWall(new Vector3(tile.max.x + side, y, tile.center.z), new Vector3(thickness, height, tile.size.z), color);
        }
    }

    private static IEnumerable<Vector3> ExitDoorPositions(Room room, Bounds tile) => room.Doors
        .Where(door => door != null && !door.IsDestroyed)
        .Select(door => door.Position)
        .Where(p => Mathf.Min(Mathf.Abs(p.x - tile.min.x), Mathf.Abs(p.x - tile.max.x), Mathf.Abs(p.z - tile.min.z), Mathf.Abs(p.z - tile.max.z)) < 1f);

    public string DescribeClassDCells()
    {
        if (!ClassDCellsTile.TryGet(out Vector3Int coords, out Room? room) || room == null)
        {
            return "none";
        }

        Bounds tile = ClassDCellsTile.FloorBounds(coords, room.Position.y);
        Bounds whole = ClassDCellsTile.RoomBounds();
        IEnumerable<string> exits = ExitDoorPositions(room, tile)
            .Select(p => FormattableString.Invariant($"({p.x:0.##},{p.y:0.##},{p.z:0.##})"));
        return FormattableString.Invariant($"x={tile.min.x:0.##}..{tile.max.x:0.##} z={tile.min.z:0.##}..{tile.max.z:0.##} floor={room.Position.y:0.##} room_bounds=x={whole.min.x:0.##}..{whole.max.x:0.##},z={whole.min.z:0.##}..{whole.max.z:0.##} exits=[{string.Join(";", exits)}]");
    }

    private string CellsSignature(bool hasCellsTile) =>
        hasCellsTile && ClassDCellsTile.TryGet(out Vector3Int coords, out _) ? coords.ToString() : "none";

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
