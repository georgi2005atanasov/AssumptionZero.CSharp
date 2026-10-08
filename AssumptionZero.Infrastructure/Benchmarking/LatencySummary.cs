namespace AssumptionZero.Infrastructure.Benchmarking;

public sealed record LatencySummary(int Count, long P50, long P90, long P95, long P99, long P999, long Maximum)
{

    public static LatencySummary From(long[] values, int count)
    {
        if (count == 0)
        {
            return new(0, 0, 0, 0, 0, 0, 0);
        }

        Array.Sort(values, 0, count);
        return new(
            count,
            Percentile(values, count, .5),
            Percentile(values, count, .9),
            Percentile(values, count, .95),
            Percentile(values, count, .99),
            Percentile(values, count, .999),
            values[count - 1]);
    }

    private static long Percentile(long[] sortedValues, int count,
        double fraction) => sortedValues[(int)Math.Ceiling(count * fraction) - 1];
}
