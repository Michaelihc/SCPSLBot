using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using Logger = LabApi.Features.Console.Logger;
using MapGeneration;
using MEC;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using SCPSLBot.AI;
using SCPSLBot.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using LabRound = LabApi.Features.Wrappers.Round;
using LabServer = LabApi.Features.Wrappers.Server;

namespace SCPSLBot.Diagnostics
{
    /// <summary>
    /// Writes one JSONL file per round (header, navmesh floor, position samples, role changes, deaths) for
    /// tools/round-map/render_round_map.py. Sampling runs on a single MEC coroutine at the configured interval,
    /// lines are formatted into reused buffers, and nothing here throws into game callbacks.
    /// </summary>
    internal sealed class RoundTraceRecorder
    {
        private const int FlushPeriodSeconds = 5;
        private const int FileBufferBytes = 64 * 1024;
        private const float NavTileMinArea = 0.02f;
        private const int NavChunkChars = 16 * 1024;

        public static RoundTraceRecorder Instance { get; } = new RoundTraceRecorder();

        private RoundTraceConfig config;
        private bool initialized;
        private bool manualArmed;

        private StreamWriter writer;
        private string currentPath;
        private CoroutineHandle sampler;
        private int session;
        private float roundZero;
        private float lastFlush;
        private int sampleCount;
        private int eventCount;
        private bool navWritten;

        private readonly StringBuilder line = new StringBuilder(4096);
        private char[] chars = new char[8192];
        private readonly HashSet<int> knownPlayers = new HashSet<int>();

        public bool IsRecording => writer != null;
        public string CurrentPath => currentPath;

        public void Init(RoundTraceConfig traceConfig)
        {
            if (initialized)
                return;

            config = traceConfig ?? new RoundTraceConfig();
            initialized = true;
            ServerEvents.RoundStarted += OnRoundStarted;
            ServerEvents.RoundEnded += OnRoundEnded;
            ServerEvents.RoundRestarted += OnRoundRestarted;
            PlayerEvents.Death += OnDeath;
            PlayerEvents.ChangedRole += OnChangedRole;
            ReferenceHub.OnPlayerRemoved += OnPlayerRemoved;

            // Enabling mid-round (plugin reload) traces from the current moment onward.
            if (config.Enabled && IsRoundLive())
                TryBegin("plugin enabled mid-round");
        }

        public void Terminate()
        {
            if (!initialized)
                return;

            initialized = false;
            ServerEvents.RoundStarted -= OnRoundStarted;
            ServerEvents.RoundEnded -= OnRoundEnded;
            ServerEvents.RoundRestarted -= OnRoundRestarted;
            PlayerEvents.Death -= OnDeath;
            PlayerEvents.ChangedRole -= OnChangedRole;
            ReferenceHub.OnPlayerRemoved -= OnPlayerRemoved;
            manualArmed = false;
            Close("plugin_disabled");
            config = null;
        }

        // ---- Command surface ----

        public string Start()
        {
            if (!initialized)
                return "Round trace is not initialized.";
            if (IsRecording)
                return $"Already recording: {currentPath}";

            manualArmed = true;
            if (IsRoundLive())
            {
                return TryBegin("manual start") ? $"Recording started: {currentPath}" : "Recording failed to start; see the server log.";
            }

            return "Armed: recording starts when the round begins.";
        }

        public string Stop()
        {
            manualArmed = false;
            if (!IsRecording)
                return "Not recording.";

            string path = currentPath;
            Close("stopped");
            return $"Recording stopped: {path}";
        }

        public string Status()
        {
            string state = IsRecording
                ? $"recording path={currentPath} samples={sampleCount} events={eventCount} t={Now():0.0}s"
                : (manualArmed ? "armed (waiting for round start)" : "idle");
            return $"round_trace={state}; auto={(config?.Enabled ?? false)}; interval={(config?.SampleIntervalSeconds ?? 0f):0.##}s; dir={TraceDirectory() ?? "unset"}";
        }

        // ---- Round lifecycle ----

        private static bool IsRoundLive() => LabRound.IsRoundInProgress && SeedSynchronizer.MapGenerated;

        private void OnRoundStarted()
        {
            try
            {
                Close("new_round");
                if ((config?.Enabled ?? false) || manualArmed)
                    TryBegin("round started");
            }
            catch (Exception exception)
            {
                Logger.Error($"[SCPSLBot] ROUND_TRACE_FAILED start: {exception}");
            }
        }

        private void OnRoundEnded(RoundEndedEventArgs args)
        {
            try
            {
                if (!IsRecording)
                    return;

                BeginLine("round_end", Now());
                line.Append(",\"leadingTeam\":");
                AppendString(args.LeadingTeam.ToString());
                EndLine();
                manualArmed = false;
                Close("round_end");
            }
            catch (Exception exception)
            {
                Logger.Error($"[SCPSLBot] ROUND_TRACE_FAILED round_end: {exception}");
            }
        }

        private void OnRoundRestarted()
        {
            manualArmed = false;
            Close("round_restarted");
        }

        // ---- Player events ----

        private void OnDeath(PlayerDeathEventArgs args)
        {
            if (!IsRecording)
                return;

            try
            {
                var hub = args.Player?.ReferenceHub;
                if (args.Player == null || hub == null || hub.IsHost)
                    return;

                EnsureKnown(hub);
                BeginLine("death", Now());
                line.Append(",\"id\":"); AppendInt(args.Player.PlayerId);
                line.Append(",\"attackerId\":"); AppendInt(args.Attacker != null ? args.Attacker.PlayerId : -1);
                line.Append(",\"pos\":"); AppendVector(args.OldPosition);
                line.Append(",\"role\":"); AppendString(args.OldRole.ToString());
                if (args.DamageHandler != null)
                {
                    line.Append(",\"cause\":"); AppendString(args.DamageHandler.GetType().Name);
                }
                EndLine();
                eventCount++;
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void OnChangedRole(PlayerChangedRoleEventArgs args)
        {
            if (!IsRecording)
                return;

            try
            {
                var hub = args.Player?.ReferenceHub;
                if (args.Player == null || hub == null || hub.IsHost || args.NewRole == null)
                    return;

                EnsureKnown(hub);
                BeginLine("role", Now());
                line.Append(",\"id\":"); AppendInt(args.Player.PlayerId);
                line.Append(",\"role\":"); AppendString(args.NewRole.RoleTypeId.ToString());
                line.Append(",\"roleId\":"); AppendInt((int)args.NewRole.RoleTypeId);
                line.Append(",\"team\":"); AppendString(args.NewRole.Team.ToString());
                line.Append(",\"reason\":"); AppendString(args.ChangeReason.ToString());
                if (args.NewRole is IFpcRole fpcRole && fpcRole.FpcModule != null && fpcRole.FpcModule.Position != Vector3.zero)
                {
                    line.Append(",\"pos\":"); AppendVector(fpcRole.FpcModule.Position);
                }
                EndLine();
                eventCount++;
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void OnPlayerRemoved(ReferenceHub hub)
        {
            if (!IsRecording)
                return;

            try
            {
                // Native hook on purpose: teardown must not recreate LabAPI player wrappers.
                int id = hub.PlayerId;
                if (!knownPlayers.Remove(id))
                    return;

                BeginLine("left", Now());
                line.Append(",\"id\":"); AppendInt(id);
                EndLine();
                eventCount++;
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        // ---- Session ----

        private bool TryBegin(string reason)
        {
            string directory = TraceDirectory();
            if (directory == null)
            {
                Logger.Warn("[SCPSLBot] ROUND_TRACE_SKIPPED plugin directory is not resolved.");
                return false;
            }

            try
            {
                Close("restart");
                Directory.CreateDirectory(directory);
                currentPath = Path.Combine(directory, $"round-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
                var stream = new FileStream(currentPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, FileBufferBytes);
                writer = new StreamWriter(stream, new UTF8Encoding(false), FileBufferBytes) { AutoFlush = false, NewLine = "\n" };

                // Round time zero is the native round clock, so a mid-round start still lines up with it.
                roundZero = Time.realtimeSinceStartup - (float)LabRound.Duration.TotalSeconds;
                lastFlush = Time.realtimeSinceStartup;
                sampleCount = 0;
                eventCount = 0;
                navWritten = false;
                knownPlayers.Clear();
                int mySession = ++session;

                WriteHeader(reason);
                writer.Flush();
                sampler = Timing.RunCoroutine(SampleLoop(mySession));
                Logger.Info($"[SCPSLBot] ROUND_TRACE_STARTED path={currentPath} reason={reason}");
                return true;
            }
            catch (Exception exception)
            {
                Logger.Error($"[SCPSLBot] ROUND_TRACE_FAILED begin: {exception}");
                Close("begin_failed");
                return false;
            }
        }

        private void Close(string reason)
        {
            session++;
            Timing.KillCoroutines(sampler);
            var closing = writer;
            if (closing == null)
                return;

            string path = currentPath;
            writer = null;
            currentPath = null;
            knownPlayers.Clear();
            try
            {
                closing.Flush();
                closing.Dispose();
                Logger.Info($"[SCPSLBot] ROUND_TRACE_CLOSED path={path} reason={reason} samples={sampleCount} events={eventCount}");
            }
            catch (Exception exception)
            {
                Logger.Error($"[SCPSLBot] ROUND_TRACE_FAILED close: {exception}");
            }
        }

        private void Fail(Exception exception)
        {
            Logger.Error($"[SCPSLBot] ROUND_TRACE_FAILED stopping recorder: {exception}");
            manualArmed = false;
            Close("error");
        }

        private static string TraceDirectory()
        {
            string baseDir = NavigationSystem.Instance.BaseDir;
            return string.IsNullOrWhiteSpace(baseDir) ? null : Path.Combine(baseDir, "traces");
        }

        private float Now() => Time.realtimeSinceStartup - roundZero;

        // ---- Sampling ----

        private IEnumerator<float> SampleLoop(int mySession)
        {
            while (session == mySession && writer != null)
            {
                try
                {
                    if (!navWritten)
                        TryWriteNavMesh();
                    Sample();
                    if (Time.realtimeSinceStartup - lastFlush >= FlushPeriodSeconds)
                    {
                        lastFlush = Time.realtimeSinceStartup;
                        writer.Flush();
                    }
                }
                catch (Exception exception)
                {
                    if (session == mySession)
                        Fail(exception);
                    yield break;
                }

                float interval = config != null ? config.SampleIntervalSeconds : 1f;
                yield return Timing.WaitForSeconds(interval);
            }
        }

        private void Sample()
        {
            BeginLine("s", Now());
            line.Append(",\"p\":[");
            bool first = true;
            foreach (var hub in ReferenceHub.AllHubs)
            {
                if (hub == null || hub.IsHost)
                    continue;

                var role = hub.roleManager != null ? hub.roleManager.CurrentRole : null;
                if (role == null)
                    continue;

                // New players get their identity line before the sample that first mentions them. It goes
                // straight to the writer; the sample line is still being built and is written afterwards.
                EnsureKnown(hub);

                if (role is not IFpcRole fpcRole || fpcRole.FpcModule == null || !hub.IsAlive())
                    continue;

                var position = fpcRole.FpcModule.Position;
                if (!first)
                    line.Append(',');
                first = false;
                line.Append('[');
                AppendInt(hub.PlayerId); line.Append(',');
                AppendScaled(position.x, 10); line.Append(',');
                AppendScaled(position.y, 10); line.Append(',');
                AppendScaled(position.z, 10); line.Append(',');
                AppendInt((int)role.RoleTypeId);
                line.Append(']');
            }

            line.Append("]");
            EndLine();
            sampleCount++;
        }

        // Writes the "player" identity line the first time an id is seen (rare, so strings may allocate).
        private void EnsureKnown(ReferenceHub hub)
        {
            int id = hub.PlayerId;
            if (!knownPlayers.Add(id))
                return;

            var pending = new StringBuilder(160);
            var role = hub.roleManager?.CurrentRole;
            pending.Append("{\"t\":\"player\",\"time\":");
            AppendScaled(pending, Now(), 100);
            pending.Append(",\"id\":").Append(id);
            pending.Append(",\"nick\":");
            AppendString(pending, hub.nicknameSync != null ? hub.nicknameSync.MyNick : string.Empty);
            bool managed = BotManager.Instance.BotPlayers.ContainsKey(hub);
            pending.Append(",\"isBot\":").Append(managed || hub.IsDummy ? "true" : "false");
            pending.Append(",\"managedBot\":").Append(managed ? "true" : "false");
            if (role != null)
            {
                pending.Append(",\"role\":");
                AppendString(pending, role.RoleTypeId.ToString());
                pending.Append(",\"roleId\":").Append((int)role.RoleTypeId);
                pending.Append(",\"team\":");
                AppendString(pending, role.Team.ToString());
            }
            pending.Append("}\n");
            writer.Write(pending.ToString());
        }

        // ---- Header and navmesh ----

        private void WriteHeader(string reason)
        {
            BeginRaw("header");
            line.Append(",\"version\":1");
            line.Append(",\"utc\":"); AppendString(DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            line.Append(",\"reason\":"); AppendString(reason);
            line.Append(",\"port\":"); AppendInt(LabServer.Port);
            line.Append(",\"seed\":"); AppendInt(SeedSynchronizer.Seed);
            line.Append(",\"mapAge\":"); AppendScaled(SeedSynchronizer.TimeSinceMapGeneration, 10);
            line.Append(",\"roundTime\":"); AppendScaled(Now(), 10);
            line.Append(",\"interval\":"); AppendScaled(config != null ? config.SampleIntervalSeconds : 1f, 100);

            line.Append(",\"roles\":{");
            bool firstRole = true;
            foreach (RoleTypeId roleId in Enum.GetValues(typeof(RoleTypeId)))
            {
                if (roleId == RoleTypeId.None || !PlayerRoleLoader.TryGetRoleTemplate(roleId, out PlayerRoleBase template))
                    continue;

                if (!firstRole)
                    line.Append(',');
                firstRole = false;
                line.Append('"'); AppendInt((int)roleId); line.Append("\":[");
                AppendString(roleId.ToString()); line.Append(',');
                AppendString(template.Team.ToString());
                line.Append(']');
            }
            line.Append('}');

            line.Append(",\"rooms\":[");
            bool firstRoom = true;
            foreach (var room in RoomIdentifier.AllRoomIdentifiers)
            {
                if (room == null)
                    continue;

                if (!firstRoom)
                    line.Append(',');
                firstRoom = false;
                var bounds = room.WorldspaceBounds;
                var transform = room.transform;
                line.Append("{\"name\":"); AppendString(room.Name.ToString());
                line.Append(",\"zone\":"); AppendString(room.Zone.ToString());
                line.Append(",\"shape\":"); AppendString(room.Shape.ToString());
                line.Append(",\"pos\":"); AppendVector(transform.position);
                line.Append(",\"rotY\":"); AppendScaled(transform.eulerAngles.y, 10);
                line.Append(",\"bounds\":{\"center\":"); AppendVector(bounds.center);
                line.Append(",\"size\":"); AppendVector(bounds.size);
                line.Append("}}");
                DrainIfLarge();
            }
            line.Append("]");
            EndLine();
        }

        private void TryWriteNavMesh()
        {
            var navigation = NavigationSystem.Instance;
            if (!navigation.IsReadyForCurrentMap || !navigation.Runtime.IsBuilt)
                return;

            navWritten = true;
            var triangulation = NavMesh.CalculateTriangulation();
            var vertices = triangulation.vertices;
            var indices = triangulation.indices;
            if (vertices == null || indices == null || indices.Length < 3)
                return;

            // Quantize to 0.1 m, merge coincident vertices and drop slivers; only a one-time pass per round.
            var remap = new Dictionary<long, int>(vertices.Length);
            var outVertices = new List<int>(vertices.Length * 3);
            var outIndices = new List<int>(indices.Length);
            var corner = new int[3];
            int kept = 0;
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                var a = vertices[indices[t]];
                var b = vertices[indices[t + 1]];
                var c = vertices[indices[t + 2]];
                if (Vector3.Cross(b - a, c - a).sqrMagnitude < 4f * NavTileMinArea * NavTileMinArea)
                    continue;

                for (int k = 0; k < 3; k++)
                {
                    var v = vertices[indices[t + k]];
                    int qx = Mathf.RoundToInt(v.x * 10f), qy = Mathf.RoundToInt(v.y * 10f), qz = Mathf.RoundToInt(v.z * 10f);
                    long key = ((long)(qx + 1048576) << 42) | ((long)(qy + 1048576) << 21) | (long)(qz + 1048576);
                    if (!remap.TryGetValue(key, out int index))
                    {
                        index = outVertices.Count / 3;
                        remap.Add(key, index);
                        outVertices.Add(qx);
                        outVertices.Add(qy);
                        outVertices.Add(qz);
                    }
                    corner[k] = index;
                }

                if (corner[0] == corner[1] || corner[1] == corner[2] || corner[0] == corner[2])
                    continue;

                outIndices.Add(corner[0]);
                outIndices.Add(corner[1]);
                outIndices.Add(corner[2]);
                kept++;
            }

            BeginRaw("nav");
            line.Append(",\"time\":"); AppendScaled(Now(), 10);
            line.Append(",\"scale\":10,\"v\":[");
            for (int i = 0; i < outVertices.Count; i++)
            {
                if (i > 0)
                    line.Append(',');
                AppendInt(outVertices[i]);
                DrainIfLarge();
            }
            line.Append("],\"i\":[");
            for (int i = 0; i < outIndices.Count; i++)
            {
                if (i > 0)
                    line.Append(',');
                AppendInt(outIndices[i]);
                DrainIfLarge();
            }
            line.Append("]");
            EndLine();
            Logger.Info($"[SCPSLBot] ROUND_TRACE_NAV triangles={kept}/{indices.Length / 3} vertices={outVertices.Count / 3}");
        }

        // ---- Line building (reused buffers; no per-value allocation) ----

        private void BeginLine(string type, float time)
        {
            BeginRaw(type);
            line.Append(",\"time\":");
            AppendScaled(time, 100);
        }

        private void BeginRaw(string type)
        {
            line.Clear();
            line.Append("{\"t\":\"").Append(type).Append('"');
        }

        private void EndLine()
        {
            line.Append("}\n");
            Drain();
        }

        private void DrainIfLarge()
        {
            if (line.Length >= NavChunkChars)
                Drain();
        }

        private void Drain()
        {
            int length = line.Length;
            if (length == 0)
                return;

            if (chars.Length < length)
                chars = new char[Math.Max(length, chars.Length * 2)];
            line.CopyTo(0, chars, 0, length);
            writer.Write(chars, 0, length);
            line.Clear();
        }

        private void AppendInt(int value) => AppendLong(line, value);

        private void AppendScaled(float value, int scale) => AppendScaled(line, value, scale);

        private void AppendVector(Vector3 value)
        {
            line.Append('[');
            AppendScaled(value.x, 10); line.Append(',');
            AppendScaled(value.y, 10); line.Append(',');
            AppendScaled(value.z, 10);
            line.Append(']');
        }

        private void AppendString(string value) => AppendString(line, value);

        private static void AppendLong(StringBuilder target, long value)
        {
            if (value < 0)
            {
                target.Append('-');
                value = -value;
            }
            if (value >= 10)
                AppendLong(target, value / 10);
            target.Append((char)('0' + (int)(value % 10)));
        }

        // Fixed-point decimal (scale 10 -> one decimal, 100 -> two) without culture-dependent formatting.
        private static void AppendScaled(StringBuilder target, float value, int scale)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                value = 0f;

            long scaled = (long)Math.Round(value * (double)scale);
            if (scaled < 0)
            {
                target.Append('-');
                scaled = -scaled;
            }
            AppendLong(target, scaled / scale);
            target.Append('.');
            int digits = scale == 100 ? 2 : 1;
            long fraction = scaled % scale;
            if (digits == 2)
                target.Append((char)('0' + (int)(fraction / 10)));
            target.Append((char)('0' + (int)(fraction % 10)));
        }

        private static void AppendString(StringBuilder target, string value)
        {
            target.Append('"');
            if (value != null)
            {
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '"': target.Append("\\\""); break;
                        case '\\': target.Append("\\\\"); break;
                        case '\n': target.Append("\\n"); break;
                        case '\r': target.Append("\\r"); break;
                        case '\t': target.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                target.Append("\\u").Append(((int)c).ToString("x4"));
                            else
                                target.Append(c);
                            break;
                    }
                }
            }
            target.Append('"');
        }
    }
}
