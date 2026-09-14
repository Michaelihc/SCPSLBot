using System;
using System.Collections.Generic;

namespace ScpslPluginStarter.Core;

internal sealed class ResilientSchedule
{
    private sealed class WorkItem
    {
        public WorkItem(string name, int intervalMilliseconds, Action action, long nowMilliseconds)
        {
            Name = name;
            IntervalMilliseconds = Math.Max(1, intervalMilliseconds);
            Action = action;
            NextDueMilliseconds = nowMilliseconds;
        }

        public string Name { get; }
        public int IntervalMilliseconds { get; }
        public Action Action { get; }
        public long NextDueMilliseconds { get; set; }
    }

    private readonly List<WorkItem> _items = new();

    public void Add(string name, int intervalMilliseconds, Action action, long nowMilliseconds) =>
        _items.Add(new WorkItem(name, intervalMilliseconds, action, nowMilliseconds));

    public void RunDue(long nowMilliseconds, Action<string, Exception> onFault)
    {
        foreach (WorkItem item in _items)
        {
            if (nowMilliseconds < item.NextDueMilliseconds)
            {
                continue;
            }

            do
            {
                item.NextDueMilliseconds = MonotonicDeadline.After(item.NextDueMilliseconds, item.IntervalMilliseconds);
            }
            while (item.NextDueMilliseconds <= nowMilliseconds);

            try
            {
                item.Action();
            }
            catch (Exception exception)
            {
                onFault(item.Name, exception);
            }
        }
    }
}
