using System.Numerics;
using System.Runtime.CompilerServices;

namespace AssumptionZero.Infrastructure;

public sealed class SpscRingBuffer<T> where T : unmanaged
{
    private readonly T[] buffer;
    private readonly ulong capacity;
    private readonly ulong mask;

    private ulong producerTail;
    private ulong producerHeadCache;

    private ulong consumerHead;
    private ulong consumerTailCache;

    private readonly PaddedCursor publishedHead = new();

    private readonly PaddedCursor publishedTail = new();

    private sealed class PaddedCursor
    {
        // padding so head and tail are very unlikely to share a cache line.
#pragma warning disable CS0169
        private long p0, p1, p2, p3, p4, p5, p6, p7;

        public ulong Value;

        private long q0, q1, q2, q3, q4, q5, q6, q7;
#pragma warning restore CS0169
    }

    public SpscRingBuffer(ulong capacity)
    {
        if (capacity < 2 || capacity > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        if (!BitOperations.IsPow2(capacity))
            throw new ArgumentException("capacity must be a power of two", nameof(capacity));

        buffer = new T[capacity];
        this.capacity = (ulong)capacity;
        mask = (ulong)(capacity - 1);
    }

    /// <summary>
    /// TODO: measure whether this attribute would improve performance
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        ulong tail = producerTail;

        if (unchecked(tail - producerHeadCache) >= capacity)
        {
            producerHeadCache = Volatile.Read(ref publishedHead.Value);

            if (unchecked(tail - producerHeadCache) >= capacity)
                return false;
        }

        int index = (int)(tail & mask);
        buffer[index] = item;

        ulong next = unchecked(tail + 1);

        producerTail = next;

        Volatile.Write(ref publishedTail.Value, next);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        ulong head = consumerHead;

        if (head == consumerTailCache)
        {
            consumerTailCache = Volatile.Read(ref publishedTail.Value);

            if (head == consumerTailCache)
            {
                item = default;
                return false;
            }
        }

        int index = (int)(head & mask);

        item = buffer[index];

        ulong next = unchecked(head + 1);

        consumerHead = next;

        Volatile.Write(ref publishedHead.Value, next);

        return true;
    }
}