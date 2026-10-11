using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public class RingBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Constructor_RejectsNonPositiveCapacity(int capacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(capacity));

    [Fact]
    public void Add_BelowCapacity_KeepsInsertionOrder()
    {
        var buffer = new RingBuffer<int>(4);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.Equal(3, buffer.Count);
        Assert.False(buffer.IsFull);
        Assert.Equal(new[] { 1, 2, 3 }, buffer.ToArray());
        Assert.Equal(1, buffer[0]);
        Assert.Equal(3, buffer[2]);
    }

    [Fact]
    public void Add_PastCapacity_OverwritesOldestAndKeepsOrder()
    {
        var buffer = new RingBuffer<int>(3);
        for (int i = 1; i <= 7; i++) buffer.Add(i);

        Assert.True(buffer.IsFull);
        Assert.Equal(3, buffer.Count);
        Assert.Equal(new[] { 5, 6, 7 }, buffer.ToArray());
        Assert.Equal(new[] { 5, 6, 7 }, buffer.ToList()); // enumerator agrees with ToArray
        Assert.Equal(5, buffer[0]);
        Assert.Equal(7, buffer[2]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Indexer_OutOfRange_Throws(int index)
    {
        var buffer = new RingBuffer<int>(5);
        buffer.Add(1);
        buffer.Add(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[index]);
    }

    [Fact]
    public void RemoveOldest_DropsFromTheFrontAcrossTheWrap()
    {
        var buffer = new RingBuffer<int>(4);
        for (int i = 1; i <= 6; i++) buffer.Add(i); // wrapped: 3,4,5,6

        buffer.RemoveOldest(2);
        Assert.Equal(new[] { 5, 6 }, buffer.ToArray());

        buffer.Add(7);
        buffer.Add(8);
        buffer.Add(9);
        Assert.Equal(new[] { 6, 7, 8, 9 }, buffer.ToArray());

        buffer.RemoveOldest(99); // clamps to Count
        Assert.Empty(buffer);
        buffer.RemoveOldest(-1); // no-op
        Assert.Empty(buffer);
    }

    [Fact]
    public void Clear_EmptiesAndBufferIsReusable()
    {
        var buffer = new RingBuffer<string>(2);
        buffer.Add("a");
        buffer.Add("b");
        buffer.Add("c");

        buffer.Clear();
        Assert.Empty(buffer);

        buffer.Add("d");
        Assert.Equal(new[] { "d" }, buffer.ToArray());
    }
}
