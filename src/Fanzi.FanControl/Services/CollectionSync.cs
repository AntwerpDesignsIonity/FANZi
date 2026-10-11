using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Updates a bound <see cref="ObservableCollection{T}"/> in place to match a new snapshot (v4).
/// Replacing the whole list with Clear()+Add() every poll resets the ItemsControl / DataGrid — rows
/// flash, scroll position and hover are lost and the UI visibly stutters. This keyed diff only moves,
/// inserts, removes or replaces the rows that actually changed.
/// </summary>
public static class CollectionSync
{
    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="source"/> (same order) using
    /// <paramref name="key"/> to recognise rows. Existing rows are kept; when
    /// <paramref name="update"/> is given it refreshes them in place (mutable row view models),
    /// otherwise a row is replaced only when it is no longer <c>Equals</c> (records).
    /// </summary>
    public static void Sync<T, TKey>(
        ObservableCollection<T> target,
        IReadOnlyList<T> source,
        Func<T, TKey> key,
        Action<T, T>? update = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(key);

        // 1. Drop rows that disappeared (and duplicates of a key — the first one wins).
        var wanted = new HashSet<TKey>();
        foreach (var item in source) wanted.Add(key(item));
        var seen = new HashSet<TKey>();
        for (int i = target.Count - 1; i >= 0; i--)
        {
            var k = key(target[i]);
            if (!wanted.Contains(k)) target.RemoveAt(i);
        }
        for (int i = 0; i < target.Count; i++)
        {
            if (!seen.Add(key(target[i]))) { target.RemoveAt(i); i--; }
        }

        // 2. Walk the new order: keep / move / insert, updating kept rows.
        var comparer = EqualityComparer<TKey>.Default;
        for (int i = 0; i < source.Count; i++)
        {
            var next = source[i];
            var k = key(next);

            if (i < target.Count && comparer.Equals(key(target[i]), k))
            {
                Refresh(target, i, next, update);
                continue;
            }

            int found = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (comparer.Equals(key(target[j]), k)) { found = j; break; }
            }

            if (found >= 0)
            {
                target.Move(found, i);
                Refresh(target, i, next, update);
            }
            else
            {
                target.Insert(i, next);
            }
        }

        // 3. Anything left after the new length (duplicate keys in the source) goes.
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }

    private static void Refresh<T>(ObservableCollection<T> target, int index, T next, Action<T, T>? update)
    {
        var current = target[index];
        if (update is not null)
        {
            update(current, next);
            return;
        }
        if (!EqualityComparer<T>.Default.Equals(current, next))
            target[index] = next;
    }
}
