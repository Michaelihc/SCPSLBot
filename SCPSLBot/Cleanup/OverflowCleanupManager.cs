using CommandSystem;
using CommandSystem.Commands.RemoteAdmin.Cleanup;
using InventorySystem.Items.Pickups;
using LabApi.Events.Handlers;
using MEC;
using System;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace SCPSLBot.Cleanup
{
    internal sealed class OverflowCleanupManager
    {
        public static OverflowCleanupManager Instance { get; } = new OverflowCleanupManager();

        private readonly ICommandSender commandSender = new ServerConsoleSender();
        private BotPluginConfig config;
        private int generation;
        private int? baselineItemCount;
        private bool initialized;

        public void Init(BotPluginConfig pluginConfig)
        {
            if (initialized)
            {
                return;
            }

            config = pluginConfig ?? throw new ArgumentNullException(nameof(pluginConfig));
            initialized = true;
            generation++;
            baselineItemCount = null;
            ServerEvents.RoundStarted += OnRoundStarted;
            ServerEvents.RoundRestarted += OnRoundRestarted;
            ScheduleNext();
        }

        public void Terminate()
        {
            if (!initialized)
            {
                return;
            }

            initialized = false;
            generation++;
            ServerEvents.RoundRestarted -= OnRoundRestarted;
            ServerEvents.RoundStarted -= OnRoundStarted;
            baselineItemCount = null;
            config = null;
        }

        private void OnRoundStarted()
        {
            baselineItemCount = null;
        }

        private void OnRoundRestarted()
        {
            baselineItemCount = null;
        }

        private void ScheduleNext()
        {
            int scheduleGeneration = generation;
            float interval = Mathf.Max(1f, config?.CleanupCheckIntervalSeconds ?? 10f);
            Timing.CallDelayed(interval, () => Tick(scheduleGeneration));
        }

        private void Tick(int scheduleGeneration)
        {
            if (!initialized || scheduleGeneration != generation || config == null)
            {
                return;
            }

            try
            {
                if (config.EnableOverflowCleanup)
                {
                    CleanupIfNeeded();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SCPSLBot] Overflow cleanup failed: {ex}");
            }
            finally
            {
                if (initialized && scheduleGeneration == generation && config != null)
                {
                    ScheduleNext();
                }
            }
        }

        private void CleanupIfNeeded()
        {
            int threshold = Mathf.Max(1, config.CleanupItemThreshold);
            int itemCount = UnityEngine.Object.FindObjectsByType<ItemPickupBase>(FindObjectsSortMode.None).Length;
            if (!baselineItemCount.HasValue || itemCount < baselineItemCount.Value)
            {
                baselineItemCount = itemCount;
                Logger.Info($"[SCPSLBot] Overflow cleanup baseline captured at {itemCount} items.");
                return;
            }

            int excessItemCount = itemCount - baselineItemCount.Value;
            if (excessItemCount <= threshold)
            {
                return;
            }

            RunNativeCleanup(itemCount, baselineItemCount.Value, excessItemCount, threshold);
            baselineItemCount = null;
        }

        private void RunNativeCleanup(int itemCount, int baselineCount, int excessItemCount, int threshold)
        {
            ExecuteNativeCommand(new ItemsCommand(), "items");
            ExecuteNativeCommand(new CorpsesCommand(), "corpses");
            ExecuteNativeCommand(new BloodCommand(), "blood");
            ExecuteNativeCommand(new BulletHolesCommand(), "bullet holes");
            Logger.Info($"[SCPSLBot] Native overflow cleanup ran at {itemCount} items ({excessItemCount} above baseline {baselineCount}, threshold {threshold}).");
        }

        private void ExecuteNativeCommand(ICommand command, string label)
        {
            bool success = command.Execute(
                new ArraySegment<string>(Array.Empty<string>()),
                commandSender,
                out string response);
            if (!success)
            {
                Logger.Warn($"[SCPSLBot] Native cleanup command '{label}' failed: {response}");
            }
        }

        private OverflowCleanupManager()
        {
        }
    }
}
