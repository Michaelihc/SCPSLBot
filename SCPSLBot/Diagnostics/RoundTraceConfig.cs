using System.Collections.Generic;
using System.ComponentModel;

namespace SCPSLBot.Diagnostics
{
    public sealed class RoundTraceConfig
    {
        [Description("Record every round to a JSONL trace under <plugin folder>/traces (render it with tools/round-map/render_round_map.py). The bot_trace command works regardless. / 将每回合记录为 JSONL 轨迹文件（位于插件目录 traces 下，用 tools/round-map/render_round_map.py 渲染）；bot_trace 指令不受此项影响。")]
        public bool Enabled { get; set; }

        [Description("Seconds between player position samples. / 玩家位置采样间隔（秒）。")]
        public float SampleIntervalSeconds { get; set; } = 1f;

        internal void Normalize(ICollection<string> changes)
        {
            var original = SampleIntervalSeconds;
            SampleIntervalSeconds = float.IsNaN(original) ? 1f : System.Math.Max(0.25f, System.Math.Min(30f, original));
            if (System.Math.Abs(SampleIntervalSeconds - original) > 0.0001f || float.IsNaN(original))
            {
                changes.Add($"RoundTrace.SampleIntervalSeconds {original} -> {SampleIntervalSeconds}");
            }
        }
    }
}
