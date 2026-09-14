using SCPSLBot.AI.FirstPersonControl.Combat;
using SCPSLBot.Collections;
using SCPSLBot.Performance;
using System;
using System.Collections.Generic;

var tests = new (string Name, Action Run)[]
{
    ("difficulty profiles are cached and immutable", DifficultyProfilesAreCached),
    ("snapshot cadence stays at 8 Hz", SnapshotCadenceIsFixed),
    ("heap returns stable minimum priority order", HeapIsStable),
    ("heap storage is retained across deterministic load", HeapStorageIsReused),
    ("heap supports decrease-key style duplicate entries", HeapSupportsDuplicateEntries),
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS | {test.Name}");
}

Console.WriteLine($"RESULT | {tests.Length}/{tests.Length} passed");

static void DifficultyProfilesAreCached()
{
    foreach (var difficulty in Enum.GetValues<BotCombatDifficulty>())
    {
        var first = BotCombatDifficultySettings.For(difficulty);
        var second = BotCombatDifficultySettings.For(difficulty);
        Require(ReferenceEquals(first, second), $"{difficulty} allocated a new profile");
    }

    var normal = BotCombatDifficultySettings.For(BotCombatDifficulty.Normal);
    var unknown = BotCombatDifficultySettings.For((BotCombatDifficulty)int.MaxValue);
    Require(ReferenceEquals(normal, unknown), "unknown difficulty did not use cached Normal profile");
    Require(normal.ShotCooldownSeconds == 0.14f, "Normal shot cooldown changed");
    Require(BotCombatDifficultySettings.For(BotCombatDifficulty.Hardest).Scp096RageDurationSeconds == 60f,
        "Hardest rage duration changed");
}

static void SnapshotCadenceIsFixed()
{
    var gate = new FixedRateRefreshGate(8f);
    Require(gate.IntervalSeconds == 0.125f, "8 Hz interval is not 125 ms");
    Require(gate.TryAcquire(10f), "first capture was not immediate");
    Require(!gate.TryAcquire(10.124f), "capture ran before the interval");
    Require(gate.TryAcquire(10.125f), "capture did not run at the interval");
    Require(!gate.TryAcquire(10.249f), "second capture ran early");
    Require(gate.TryAcquire(10.250f), "second capture did not run on schedule");
    gate.Reset();
    Require(gate.TryAcquire(0f), "reset did not permit an immediate capture");
}

static void HeapIsStable()
{
    var queue = new ReusableMinPriorityQueue<string>();
    queue.Enqueue("late", 5f);
    queue.Enqueue("tie-first", 1f);
    queue.Enqueue("middle", 3f);
    queue.Enqueue("tie-second", 1f);

    Require(Dequeue(queue) == "tie-first", "first equal-priority item lost insertion order");
    Require(Dequeue(queue) == "tie-second", "second equal-priority item lost insertion order");
    Require(Dequeue(queue) == "middle", "middle priority was incorrect");
    Require(Dequeue(queue) == "late", "largest priority was incorrect");
    Require(!queue.TryDequeue(out _, out _), "empty heap returned an item");
}

static void HeapStorageIsReused()
{
    const int itemCount = 4096;
    var queue = new ReusableMinPriorityQueue<int>();
    FillAndDrain(queue, itemCount);
    var warmedCapacity = queue.Capacity;
    Require(warmedCapacity >= itemCount, "heap did not grow to the workload");

    for (var iteration = 0; iteration < 100; iteration++)
    {
        FillAndDrain(queue, itemCount);
        Require(queue.Capacity == warmedCapacity, "heap backing storage grew after warm-up");
    }
}

static void HeapSupportsDuplicateEntries()
{
    var queue = new ReusableMinPriorityQueue<int>();
    var accepted = new HashSet<int>();
    queue.Enqueue(42, 10f);
    queue.Enqueue(42, 2f);
    queue.Enqueue(7, 3f);

    Require(queue.TryDequeue(out var item, out var priority) && item == 42 && priority == 2f,
        "improved duplicate priority was not returned first");
    Require(accepted.Add(item), "first item was unexpectedly stale");
    Require(queue.TryDequeue(out item, out priority) && item == 7 && priority == 3f,
        "unrelated queue item was reordered");
    Require(accepted.Add(item), "second item was unexpectedly stale");
    Require(queue.TryDequeue(out item, out priority) && item == 42 && priority == 10f,
        "older duplicate entry disappeared");
    Require(!accepted.Add(item), "older duplicate was not identifiable as stale");
}

static void FillAndDrain(ReusableMinPriorityQueue<int> queue, int count)
{
    queue.Clear();
    for (var i = count - 1; i >= 0; i--)
    {
        queue.Enqueue(i, i);
    }

    for (var expected = 0; expected < count; expected++)
    {
        Require(queue.TryDequeue(out var actual, out var priority), "heap emptied early");
        Require(actual == expected && priority == expected, "heap returned a non-minimum item");
    }

    Require(queue.Count == 0, "heap count was not zero after drain");
}

static string Dequeue(ReusableMinPriorityQueue<string> queue)
{
    Require(queue.TryDequeue(out var item, out _), "heap unexpectedly empty");
    return item;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
