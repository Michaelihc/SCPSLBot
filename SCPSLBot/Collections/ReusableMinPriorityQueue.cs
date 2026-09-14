using System.Collections.Generic;

namespace SCPSLBot.Collections
{
    /// <summary>
    /// Stable binary min-heap. Clear retains the backing array so repeated path searches reuse memory.
    /// </summary>
    internal sealed class ReusableMinPriorityQueue<T>
    {
        private readonly List<Entry> heap = new();
        private long nextSequence;

        public int Count => heap.Count;
        public int Capacity => heap.Capacity;

        public void Clear()
        {
            heap.Clear();
            nextSequence = 0;
        }

        public void Enqueue(T item, float priority)
        {
            var entry = new Entry(item, priority, nextSequence++);
            var index = heap.Count;
            heap.Add(entry);

            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (!ComesBefore(entry, heap[parent]))
                {
                    break;
                }

                heap[index] = heap[parent];
                index = parent;
            }

            heap[index] = entry;
        }

        public bool TryDequeue(out T item, out float priority)
        {
            if (heap.Count == 0)
            {
                item = default!;
                priority = default;
                return false;
            }

            var root = heap[0];
            var lastIndex = heap.Count - 1;
            var tail = heap[lastIndex];
            heap.RemoveAt(lastIndex);

            if (lastIndex > 0)
            {
                var index = 0;
                while (true)
                {
                    var left = index * 2 + 1;
                    if (left >= lastIndex)
                    {
                        break;
                    }

                    var right = left + 1;
                    var child = right < lastIndex && ComesBefore(heap[right], heap[left]) ? right : left;
                    if (!ComesBefore(heap[child], tail))
                    {
                        break;
                    }

                    heap[index] = heap[child];
                    index = child;
                }

                heap[index] = tail;
            }

            item = root.Item;
            priority = root.Priority;
            return true;
        }

        private static bool ComesBefore(Entry left, Entry right)
        {
            var priorityComparison = left.Priority.CompareTo(right.Priority);
            return priorityComparison < 0 || priorityComparison == 0 && left.Sequence < right.Sequence;
        }

        private readonly struct Entry
        {
            public Entry(T item, float priority, long sequence)
            {
                Item = item;
                Priority = priority;
                Sequence = sequence;
            }

            public T Item { get; }
            public float Priority { get; }
            public long Sequence { get; }
        }
    }
}
