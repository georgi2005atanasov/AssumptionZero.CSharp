namespace AssumptionZero.Infrastructure.Benchmarking;

public sealed record LatencySummary(int Count, long P50, long P90, long P95, long P99, long P999, long Maximum)
{
    public static LatencySummary From(long[] values, int count)
    {
        if (count == 0) return new(0, 0, 0, 0, 0, 0, 0);
        Array.Sort(values, 0, count);
        long At(double fraction) => values[(int)Math.Ceiling(count * fraction) - 1];
        return new(count, At(.5), At(.9), At(.95), At(.99), At(.999), values[count - 1]);
    }
}