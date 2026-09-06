using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace UDRoute
{
    public class TemporyDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly int _maxsize;
        private readonly IEqualityComparer<TKey>? _comparer;
        private Dictionary<TKey, TValue> _dic1;
        private Dictionary<TKey, TValue>? _dic2;

        public TemporyDictionary(int maxsize, IEqualityComparer<TKey>? comparer = null)
        {
            _maxsize = Math.Max(1, maxsize);
            _comparer = comparer;
            _dic1 = new Dictionary<TKey, TValue>(comparer);
        }

        public int Count => _dic1.Count + (_dic2?.Count ?? 0);

        public TValue this[TKey key]
        {
            get
            {
                if (_dic1.TryGetValue(key, out var value1))
                {
                    return value1;
                }
                else if (_dic2 != null && _dic2.TryGetValue(key, out var value))
                {
                    return value;
                }
                else
                {
                    throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");
                }
            }
            set
            {
                if (_dic1.Count >= _maxsize && !_dic1.ContainsKey(key))
                {
                    _dic2 = _dic1;
                    _dic1 = new Dictionary<TKey, TValue>(_comparer);
                }
                _dic1[key] = value;
            }
        }

        public void Clear()
        {
            _dic1.Clear();
            _dic2 = null;
        }

        public bool ContainsKey(TKey key) => TryGetValue(key, out _);

        public bool Remove(TKey key)
        {
            bool r1 = _dic1.Remove(key);
            bool r2 = _dic2?.Remove(key) ?? false;
            return r1 || r2;
        }

        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            if (_dic1.TryGetValue(key, out var value1))
            {
                value = value1;
                return true;
            }
            else if (_dic2 != null && _dic2.TryGetValue(key, out var value2))
            {
                value = value2;
                return true;
            }
            else
            {
                value = default;
                return false;
            }
        }
    }
}
