namespace AssumptionZero.Infrastructure;

public static class NanosecondTime
{
    public static long FromTicks(long ticks, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        return checked((long)((Int128)ticks * 1_000_000_000 / frequency));
    }

    public static long ToTicksCeiling(long nanoseconds, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nanoseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        return checked((long)(((Int128)nanoseconds * frequency + 999_999_999) / 1_000_000_000));
    }
}
