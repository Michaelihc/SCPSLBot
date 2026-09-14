using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace SCPSLBot.Collections.Generic
{
    internal readonly struct SelectingDictionary<TKey, TValue, TKeyIn, TValueIn> : IReadOnlyDictionary<TKey, TValue>
    {
        private readonly IDictionary<TKeyIn, TValueIn> dictionary;
        private readonly Func<TKey, TKeyIn> keyTransformerIn;
        private readonly Func<TKeyIn, TKey> keyTransformerOut;
        private readonly Func<TValueIn, TValue> valueTransformerOut;

        public SelectingDictionary(
            Dictionary<TKeyIn, TValueIn> dictionary,
            Func<TKey, TKeyIn> keyTransformerIn,
            Func<TKeyIn, TKey> keyTransformerOut,
            Func<TValueIn, TValue> valueTransformerOut)
        {
            this.dictionary = dictionary;
            this.keyTransformerIn = keyTransformerIn;
            this.keyTransformerOut = keyTransformerOut;
            this.valueTransformerOut = valueTransformerOut;
        }

        public TValue this[TKey key] => valueTransformerOut(dictionary[keyTransformerIn(key)]);

        public bool ContainsKey(TKey key) => dictionary.ContainsKey(keyTransformerIn(key));

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (dictionary.TryGetValue(keyTransformerIn(key), out var valueIn))
            {
                value = valueTransformerOut(valueIn);
                return true;
            }

            value = default;
            return false;
        }

        public IEnumerable<TKey> Keys => dictionary.Keys.Select(keyTransformerOut);
        public IEnumerable<TValue> Values => dictionary.Values.Select(valueTransformerOut);

        public int Count => dictionary.Count;

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            var enumratorIn = dictionary.GetEnumerator();
            while (enumratorIn.MoveNext())
            {
                yield return new(keyTransformerOut(enumratorIn.Current.Key), valueTransformerOut(enumratorIn.Current.Value));
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
