using System;
using System.Collections;
using System.Collections.Generic;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Fixed-capacity FIFO: adding to a full buffer overwrites the oldest item. Index 0 is the oldest,
/// <c>Count - 1</c> the newest. Not thread-safe (the view model only touches it on the UI thread).
/// </summary>
public sealed class RingBuffer<T> : IReadOnlyList<T>
{
    private readonly T[] _items;
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count => _count;
    public bool IsFull => _count == _items.Length;

    /// <summary>Item at <paramref name="index"/> counted from the oldest (0).</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_count}).");
            return _items[(_start + index) % _items.Length];
        }
    }

    /// <summary>Appends <paramref name="item"/>; when full, the oldest item is dropped.</summary>
    public void Add(T item)
    {
        int end = (_start + _count) % _items.Length;
        _items[end] = item;
        if (_count == _items.Length)
            _start = (_start + 1) % _items.Length;
        else
            _count++;
    }

    /// <summary>Drops up to <paramref name="count"/> of the oldest items.</summary>
    public void RemoveOldest(int count)
    {
        count = Math.Clamp(count, 0, _count);
        for (int i = 0; i < count; i++)
        {
            _items[_start] = default!;
            _start = (_start + 1) % _items.Length;
        }
        _count -= count;
        if (_count == 0) _start = 0;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        _count = 0;
    }

    /// <summary>Copy of the contents, oldest first.</summary>
    public T[] ToArray()
    {
        var result = new T[_count];
        for (int i = 0; i < _count; i++)
            result[i] = _items[(_start + i) % _items.Length];
        return result;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return _items[(_start + i) % _items.Length];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
