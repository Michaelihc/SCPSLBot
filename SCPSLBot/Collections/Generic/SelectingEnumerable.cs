using System;
using System.Collections;
using System.Collections.Generic;

namespace SCPSLBot.Collections.Generic
{
    internal readonly struct SelectingEnumerable<TEnumerableSource, TResult, TSource> : IEnumerable<TResult>
        where TEnumerableSource : IEnumerable<TSource>
    {
        private readonly TEnumerableSource _source;
        private readonly Func<TSource, TResult> _selector;

        public SelectingEnumerable(TEnumerableSource source, Func<TSource, TResult> selector)
        {
            _source = source;
            _selector = selector;
        }

        public IEnumerator<TResult> GetEnumerator()
        {
            return new SelectingEnumerator<IEnumerator<TSource>>(_source.GetEnumerator(), _selector);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        internal readonly struct SelectingEnumerator<TEnumeratorSource> : IEnumerator<TResult>
            where TEnumeratorSource : IEnumerator<TSource>
        {
            private readonly TEnumeratorSource _source;
            private readonly Func<TSource, TResult> _selector;

            public SelectingEnumerator(TEnumeratorSource source, Func<TSource, TResult> selector)
            {
                _source = source;
                _selector = selector;
            }

            public TResult Current => _selector.Invoke(_source.Current);

            public void Dispose()
            {
                _source.Dispose();
            }

            public bool MoveNext()
            {
                return _source.MoveNext();
            }

            public void Reset()
            {
                _source.Reset();
            }

            object IEnumerator.Current => Current;
        }
    }
}
