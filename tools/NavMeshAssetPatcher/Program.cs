using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NavMeshAssetPatcher;

/// <summary>
/// Makes every Mesh asset in the SCP:SL dedicated server's serialized asset files readable
/// (m_IsReadable = 1, vertex data inlined instead of streamed from .resS, keep flags set) so
/// Unity's runtime navmesh builder can see the collider geometry that a player build otherwise
/// drops silently. Writes atomically next to the originals with .bak
/// backups and a manifest that a launcher/deploy script verifies before starting the server.
/// Client files are never touched; the tool only ever opens SCPSL_Data of the given server root.
/// </summary>
internal static class Program
{
    private const string ToolVersion = "1.1.0";
    public const string ManifestFileName = "navmesh-asset-patch.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 64;
            }

            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(1));
            var serverRoot = ResolveServerRoot(options);
            var dataDir = Path.Combine(serverRoot, "SCPSL_Data");
            if (!Directory.Exists(dataDir))
            {
                Console.Error.WriteLine($"SCPSL_Data was not found under '{serverRoot}'.");
                return 1;
            }

            return command switch
            {
                "report" => Report(dataDir, options),
                "dump" => Dump(dataDir, options),
                "patch" => Patch(dataDir, options),
                "verify" => Verify(dataDir, options),
                "restore" => Restore(dataDir, options),
                _ => Unknown(command),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"NavMeshAssetPatcher failed: {exception}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 64;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("NavMeshAssetPatcher " + ToolVersion);
        Console.WriteLine("Usage:");
        Console.WriteLine("  NavMeshAssetPatcher report  --server <server root> [--classdata <classdata.tpk>]");
        Console.WriteLine("  NavMeshAssetPatcher patch   --server <server root> [--classdata <classdata.tpk>] [--dry-run]");
        Console.WriteLine("  NavMeshAssetPatcher verify  --server <server root> [--quiet]");
        Console.WriteLine("  NavMeshAssetPatcher restore --server <server root>");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 ok / patched, 2 unpatched or drifted (verify), 1 error, 64 usage.");
        Console.WriteLine("The server root is the folder containing SCPSL_Data (SCPSL_SERVER_ROOT is used when --server is omitted).");
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            var key = arg.Substring(2);
            if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = list[++i];
            }
            else
            {
                options[key] = "true";
            }
        }

        return options;
    }

    private static string ResolveServerRoot(Dictionary<string, string> options)
    {
        if (options.TryGetValue("server", out var root) && !string.IsNullOrWhiteSpace(root) && root != "true")
        {
            return Path.GetFullPath(root);
        }

        var env = Environment.GetEnvironmentVariable("SCPSL_SERVER_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }

        throw new ArgumentException("--server <root> (or SCPSL_SERVER_ROOT) is required.");
    }

    private static string ResolveClassData(Dictionary<string, string> options)
    {
        if (options.TryGetValue("classdata", out var explicitPath) && explicitPath != "true")
        {
            return Path.GetFullPath(explicitPath);
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "classdata.tpk");
        if (File.Exists(beside))
        {
            return beside;
        }

        throw new FileNotFoundException("classdata.tpk was not found beside the tool; pass --classdata.");
    }

    private static IEnumerable<string> CandidateFiles(string dataDir)
    {
        // Every serialized file that can carry Mesh assets in a player build.
        foreach (var path in Directory.EnumerateFiles(dataDir))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".assets", StringComparison.OrdinalIgnoreCase)
                || name.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("level", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains('.', StringComparison.Ordinal)))
            {
                yield return path;
            }
        }
    }

    private sealed class FileScan
    {
        public string Path = string.Empty;
        public string UnityVersion = string.Empty;
        public bool TypeTreeEnabled;
        public int MeshCount;
        public int UnreadableMeshCount;
        public int StreamedMeshCount;
        public List<string> UnreadableSample = new();
    }

    private static FileScan ScanFile(AssetsManager manager, string path)
    {
        var scan = new FileScan { Path = path };
        var instance = manager.LoadAssetsFile(path, false);
        try
        {
            var file = instance.file;
            scan.UnityVersion = file.Metadata.UnityVersion;
            scan.TypeTreeEnabled = file.Metadata.TypeTreeEnabled;
            if (!scan.TypeTreeEnabled)
            {
                manager.LoadClassDatabaseFromPackage(file.Metadata.UnityVersion);
            }

            foreach (var info in file.GetAssetsOfType(AssetClassID.Mesh))
            {
                scan.MeshCount++;
                var baseField = manager.GetBaseField(instance, info);
                var readable = baseField["m_IsReadable"].AsBool;
                if (baseField["m_StreamData.size"].AsUInt > 0)
                {
                    scan.StreamedMeshCount++;
                }

                if (!readable)
                {
                    scan.UnreadableMeshCount++;
                    if (scan.UnreadableSample.Count < 8)
                    {
                        scan.UnreadableSample.Add(baseField["m_Name"].AsString);
                    }
                }
            }
        }
        finally
        {
            manager.UnloadAssetsFile(instance);
        }

        return scan;
    }

    private static int Report(string dataDir, Dictionary<string, string> options)
    {
        var manager = CreateManager(options);
        var totalMeshes = 0;
        var totalUnreadable = 0;
        foreach (var path in CandidateFiles(dataDir))
        {
            var scan = ScanFile(manager, path);
            totalMeshes += scan.MeshCount;
            totalUnreadable += scan.UnreadableMeshCount;
            Console.WriteLine($"{Path.GetFileName(path),-28} unity={scan.UnityVersion} typetree={scan.TypeTreeEnabled} meshes={scan.MeshCount} unreadable={scan.UnreadableMeshCount} streamed={scan.StreamedMeshCount} sample=[{string.Join(", ", scan.UnreadableSample)}]");
        }

        Console.WriteLine($"TOTAL meshes={totalMeshes} unreadable={totalUnreadable}");
        return 0;
    }

    // Diagnostics: prints the serialized layout of the named meshes (index/vertex/baked sizes).
    private static int Dump(string dataDir, Dictionary<string, string> options)
    {
        var wanted = options.TryGetValue("name", out var name) ? name : throw new ArgumentException("--name <mesh name> is required.");
        var manager = CreateManager(options);
        foreach (var path in CandidateFiles(dataDir))
        {
            var instance = manager.LoadAssetsFile(path, false);
            var file = instance.file;
            if (!file.Metadata.TypeTreeEnabled)
            {
                manager.LoadClassDatabaseFromPackage(file.Metadata.UnityVersion);
            }

            foreach (var info in file.GetAssetsOfType(AssetClassID.Mesh))
            {
                var baseField = manager.GetBaseField(instance, info);
                if (!string.Equals(baseField["m_Name"].AsString, wanted, StringComparison.Ordinal))
                {
                    continue;
                }

                Console.WriteLine($"{Path.GetFileName(path)} pathId={info.PathId} name={wanted}");
                foreach (var child in baseField.Children)
                {
                    Console.WriteLine($"  {child.TypeName} {child.FieldName} = {Describe(child)}");
                }

                foreach (var subMesh in baseField["m_SubMeshes.Array"].Children)
                {
                    Console.WriteLine($"  submesh firstByte={subMesh["firstByte"].AsUInt} indexCount={subMesh["indexCount"].AsUInt} topology={subMesh["topology"].AsInt} baseVertex={subMesh["baseVertex"].AsUInt} firstVertex={subMesh["firstVertex"].AsUInt} vertexCount={subMesh["vertexCount"].AsUInt}");
                }

                var channels = baseField["m_VertexData.m_Channels.Array"].Children;
                Console.WriteLine($"  channels=[{string.Join(" ", channels.Select((c, i) => $"{i}:s{c["stream"].AsByte}/o{c["offset"].AsByte}/f{c["format"].AsByte}/d{c["dimension"].AsByte}"))}]");
            }

            manager.UnloadAssetsFile(instance);
        }

        return 0;
    }

    private static string Describe(AssetTypeValueField field, int depth = 0)
    {
        try
        {
            if (field.Value == null)
            {
                if (field.Children == null || field.Children.Count == 0)
                {
                    return "struct";
                }

                if (depth >= 1)
                {
                    return $"struct[{field.Children.Count}]";
                }

                return "{" + string.Join(", ", field.Children.Select(c => c.FieldName + "=" + Describe(c, depth + 1))) + "}";
            }

            return field.Value.ValueType switch
            {
                AssetValueType.ByteArray => $"bytes[{field.AsByteArray.Length}]",
                AssetValueType.Array => $"array[{field.Children.Count}]",
                _ => field.AsString,
            };
        }
        catch (Exception exception)
        {
            return $"<{exception.GetType().Name}>";
        }
    }

    private static AssetsManager CreateManager(Dictionary<string, string> options)
    {
        var manager = new AssetsManager();
        manager.LoadClassPackage(ResolveClassData(options));
        return manager;
    }

    private sealed class ManifestFile
    {
        public string Name { get; set; } = string.Empty;
        public long SizeBefore { get; set; }
        public long SizeAfter { get; set; }
        public string Sha256Before { get; set; } = string.Empty;
        public string Sha256After { get; set; } = string.Empty;
        public int MeshCount { get; set; }
        public int PatchedMeshCount { get; set; }
        public int InlinedMeshCount { get; set; }
        public long InlinedBytes { get; set; }
    }

    private sealed class Manifest
    {
        public string ToolVersion { get; set; } = string.Empty;
        public string PatchedAtUtc { get; set; } = string.Empty;
        public string UnityVersion { get; set; } = string.Empty;
        public string AssemblyCSharpSha256 { get; set; } = string.Empty;
        public int TotalMeshCount { get; set; }
        public int TotalPatchedMeshCount { get; set; }
        public List<ManifestFile> Files { get; set; } = new();
    }

    private static int Patch(string dataDir, Dictionary<string, string> options)
    {
        var dryRun = options.ContainsKey("dry-run");
        var manager = CreateManager(options);
        var manifest = new Manifest
        {
            ToolVersion = ToolVersion,
            PatchedAtUtc = DateTime.UtcNow.ToString("O"),
            AssemblyCSharpSha256 = HashFile(Path.Combine(dataDir, "Managed", "Assembly-CSharp.dll")),
        };

        var manifestPath = Path.Combine(dataDir, ManifestFileName);
        var previous = LoadManifest(manifestPath);

        foreach (var path in CandidateFiles(dataDir))
        {
            var name = Path.GetFileName(path);
            var instance = manager.LoadAssetsFile(path, false);
            var file = instance.file;
            manifest.UnityVersion = file.Metadata.UnityVersion;
            if (!file.Metadata.TypeTreeEnabled)
            {
                manager.LoadClassDatabaseFromPackage(file.Metadata.UnityVersion);
            }

            var meshes = file.GetAssetsOfType(AssetClassID.Mesh);
            if (meshes.Count == 0)
            {
                manager.UnloadAssetsFile(instance);
                continue;
            }

            var entry = new ManifestFile
            {
                Name = name,
                SizeBefore = new FileInfo(path).Length,
                Sha256Before = HashFile(path),
                MeshCount = meshes.Count,
            };

            foreach (var info in meshes)
            {
                var baseField = manager.GetBaseField(instance, info);
                var changed = false;
                if (!baseField["m_IsReadable"].AsBool)
                {
                    baseField["m_IsReadable"].AsBool = true;
                    changed = true;
                }

                // Unity keeps no CPU copy of vertex data that is streamed from the .resS side file
                // (it is meant to go straight to the GPU), so a readable flag alone still yields a
                // mesh with zero triangles at runtime. Inline the streamed bytes instead.
                var streamSize = baseField["m_StreamData.size"].AsUInt;
                if (streamSize > 0)
                {
                    var streamPath = baseField["m_StreamData.path"].AsString;
                    var streamOffset = baseField["m_StreamData.offset"].AsULong;
                    var bytes = ReadStream(dataDir, streamPath, streamOffset, streamSize);
                    baseField["m_VertexData.m_DataSize"].AsByteArray = bytes;
                    baseField["m_StreamData.offset"].AsULong = 0;
                    baseField["m_StreamData.size"].AsUInt = 0;
                    baseField["m_StreamData.path"].AsString = string.Empty;
                    entry.InlinedMeshCount++;
                    entry.InlinedBytes += bytes.Length;
                    changed = true;
                }

                if (!baseField["m_KeepVertices"].AsBool || !baseField["m_KeepIndices"].AsBool)
                {
                    baseField["m_KeepVertices"].AsBool = true;
                    baseField["m_KeepIndices"].AsBool = true;
                    changed = true;
                }

                if (!changed)
                {
                    continue;
                }

                info.SetNewData(baseField);
                entry.PatchedMeshCount++;
            }

            if (entry.PatchedMeshCount == 0)
            {
                // Already readable (a previous run). Keep the file as it is and record its hash.
                manager.UnloadAssetsFile(instance);
                entry.SizeAfter = entry.SizeBefore;
                entry.Sha256After = entry.Sha256Before;
                Console.WriteLine($"{name,-28} meshes={entry.MeshCount} patched=0 (already readable)");
                manifest.Files.Add(entry);
                manifest.TotalMeshCount += entry.MeshCount;
                continue;
            }

            if (dryRun)
            {
                manager.UnloadAssetsFile(instance);
                Console.WriteLine($"{name,-28} meshes={entry.MeshCount} would-patch={entry.PatchedMeshCount}");
                manifest.Files.Add(entry);
                manifest.TotalMeshCount += entry.MeshCount;
                manifest.TotalPatchedMeshCount += entry.PatchedMeshCount;
                continue;
            }

            var tempPath = path + ".navpatch-tmp";
            var backupPath = path + ".bak";
            try
            {
                using (var writer = new AssetsFileWriter(tempPath))
                {
                    file.Write(writer);
                }
            }
            finally
            {
                manager.UnloadAssetsFile(instance);
            }

            // Prove the rewrite before touching the original: every mesh readable, same count.
            var check = ScanFile(manager, tempPath);
            if (check.MeshCount != entry.MeshCount || check.UnreadableMeshCount != 0 || check.StreamedMeshCount != 0)
            {
                File.Delete(tempPath);
                throw new InvalidOperationException($"Verification of the rewritten '{name}' failed (meshes={check.MeshCount}/{entry.MeshCount}, unreadable={check.UnreadableMeshCount}, streamed={check.StreamedMeshCount}).");
            }

            // Keep the very first original as the backup; a re-run after a game update replaces
            // it because the original itself changed (hash mismatch with the previous manifest).
            var previousEntry = previous?.Files.FirstOrDefault(f => f.Name == name);
            if (!File.Exists(backupPath) || previousEntry == null || previousEntry.Sha256After != entry.Sha256Before)
            {
                File.Copy(path, backupPath, overwrite: true);
            }

            File.Move(tempPath, path, overwrite: true);
            entry.SizeAfter = new FileInfo(path).Length;
            entry.Sha256After = HashFile(path);
            Console.WriteLine($"{name,-28} meshes={entry.MeshCount} patched={entry.PatchedMeshCount} inlined={entry.InlinedMeshCount} ({entry.InlinedBytes} bytes) size={entry.SizeBefore}->{entry.SizeAfter} backup={Path.GetFileName(backupPath)}");
            manifest.Files.Add(entry);
            manifest.TotalMeshCount += entry.MeshCount;
            manifest.TotalPatchedMeshCount += entry.PatchedMeshCount;
        }

        if (!dryRun)
        {
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            Console.WriteLine($"Wrote {manifestPath}");
        }

        Console.WriteLine($"TOTAL meshes={manifest.TotalMeshCount} patched={manifest.TotalPatchedMeshCount} unity={manifest.UnityVersion} dryRun={dryRun}");
        return 0;
    }

    private static int Verify(string dataDir, Dictionary<string, string> options)
    {
        var quiet = options.ContainsKey("quiet");
        var manifestPath = Path.Combine(dataDir, ManifestFileName);
        var manifest = LoadManifest(manifestPath);
        if (manifest == null)
        {
            Console.WriteLine($"UNPATCHED: no manifest at {manifestPath}. Run: NavMeshAssetPatcher patch --server \"{Path.GetDirectoryName(dataDir)}\"");
            return 2;
        }

        var assemblyHash = HashFile(Path.Combine(dataDir, "Managed", "Assembly-CSharp.dll"));
        var drifted = new List<string>();
        if (!string.Equals(assemblyHash, manifest.AssemblyCSharpSha256, StringComparison.OrdinalIgnoreCase))
        {
            drifted.Add("Assembly-CSharp.dll (game build changed)");
        }

        foreach (var entry in manifest.Files)
        {
            var path = Path.Combine(dataDir, entry.Name);
            if (!File.Exists(path))
            {
                drifted.Add($"{entry.Name} (missing)");
                continue;
            }

            if (new FileInfo(path).Length != entry.SizeAfter)
            {
                drifted.Add($"{entry.Name} (size {new FileInfo(path).Length} != {entry.SizeAfter})");
                continue;
            }

            var hash = HashFile(path);
            if (!string.Equals(hash, entry.Sha256After, StringComparison.OrdinalIgnoreCase))
            {
                drifted.Add($"{entry.Name} (hash changed; Steam validation or a game update restored it)");
            }
        }

        if (drifted.Count > 0)
        {
            Console.WriteLine($"UNPATCHED: {string.Join("; ", drifted)}. Re-apply with: NavMeshAssetPatcher patch --server \"{Path.GetDirectoryName(dataDir)}\"");
            return 2;
        }

        if (!quiet)
        {
            Console.WriteLine($"PATCHED: {manifest.Files.Count} file(s), {manifest.TotalPatchedMeshCount} of {manifest.TotalMeshCount} meshes made readable on {manifest.PatchedAtUtc} (unity {manifest.UnityVersion}, tool {manifest.ToolVersion}).");
        }

        return 0;
    }

    private static int Restore(string dataDir, Dictionary<string, string> options)
    {
        var manifestPath = Path.Combine(dataDir, ManifestFileName);
        var manifest = LoadManifest(manifestPath);
        if (manifest == null)
        {
            Console.WriteLine("Nothing to restore: no manifest.");
            return 0;
        }

        foreach (var entry in manifest.Files)
        {
            var path = Path.Combine(dataDir, entry.Name);
            var backupPath = path + ".bak";
            if (!File.Exists(backupPath))
            {
                Console.WriteLine($"{entry.Name,-28} no backup present");
                continue;
            }

            File.Copy(backupPath, path, overwrite: true);
            File.Delete(backupPath);
            Console.WriteLine($"{entry.Name,-28} restored from backup");
        }

        File.Delete(manifestPath);
        Console.WriteLine("Restored originals and removed the manifest.");
        return 0;
    }

    private static byte[] ReadStream(string dataDir, string streamPath, ulong offset, uint size)
    {
        var fileName = streamPath;
        const string archivePrefix = "archive:/";
        if (fileName.StartsWith(archivePrefix, StringComparison.Ordinal))
        {
            fileName = fileName.Substring(archivePrefix.Length);
        }

        var fullPath = Path.Combine(dataDir, Path.GetFileName(fileName));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Streamed mesh data file '{streamPath}' was not found under SCPSL_Data.");
        }

        using var stream = File.OpenRead(fullPath);
        if ((ulong)stream.Length < offset + size)
        {
            throw new InvalidDataException($"Streamed mesh data at {streamPath}+{offset} ({size} bytes) exceeds the file length {stream.Length}.");
        }

        stream.Seek((long)offset, SeekOrigin.Begin);
        var bytes = new byte[size];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count <= 0)
            {
                throw new EndOfStreamException($"Streamed mesh data at {streamPath}+{offset} ended early.");
            }

            read += count;
        }

        return bytes;
    }

    private static Manifest? LoadManifest(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
