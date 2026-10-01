using System.Globalization;
using System.Text.Json;
using AssumptionZero.Simulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AssumptionZero.Tests;
[TestClass]
public class ComparisonTests
{
    [TestMethod]
    public void UnderlyingCommandsDefaultToWholeRecordingAndValidateLimits()
    {
        string[] Basic(params string[] rest) => new[] { "run", "--recording", "demo", "--out", "/tmp/unused-comparison-test" }.Concat(rest).ToArray();
        Assert.IsNull(BenchmarkRunner.Parse(Basic()).MaxMessages);
        Assert.IsNull(BenchmarkRunner.Parse(Basic("--all")).MaxMessages);
        Assert.AreEqual(1000, BenchmarkRunner.Parse(Basic("--max-messages", "1000")).MaxMessages);
        Assert.ThrowsException<ArgumentException>(() => BenchmarkRunner.Parse(Basic("--all", "--max-messages", "1000")));
        Assert.ThrowsException<ArgumentException>(() => BenchmarkRunner.Parse(Basic("--max-messages", "0")));
        Assert.ThrowsException<ArgumentException>(() => BenchmarkRunner.Parse(Basic("--speed", "NaN")));
        Assert.ThrowsException<ArgumentException>(() => BenchmarkRunner.Parse(Basic("--speed", "1", "--target-duration-seconds", "60")));
        Assert.ThrowsException<ArgumentException>(() => BenchmarkRunner.Parse(Basic("--unknown")));
        Assert.AreEqual(1000d, BenchmarkRunner.Parse(Basic("--smoke")).Speed);
        Assert.IsNull(BenchmarkRunner.Parse(Basic("--smoke", "--target-duration-seconds", "1")).Speed);
    }

    [TestMethod]
    public void DatasetHashIncludesPayloadTimingAndBoundaries()
    {
        var r = DemoRecording.Create();
        var first = new Recording(r.Id, r.Messages.Take(1));
        Assert.AreEqual(BenchmarkRunner.DatasetHash(first), BenchmarkRunner.DatasetHash(new Recording(Guid.NewGuid(), first.Messages)));
        Assert.AreNotEqual(BenchmarkRunner.DatasetHash(first), BenchmarkRunner.DatasetHash(r));
        Assert.AreNotEqual(BenchmarkRunner.DatasetHash(first), BenchmarkRunner.DatasetHash(new Recording(r.Id,
            [first.Messages[0] with { ElapsedNanoseconds = 1 }])));
    }

    [TestMethod]
    public void DecimalReferenceVectors()
    {
        var operands = new[] {
            ("1", "3"), ("2", "3"), ("-1.99", "1"), ("100.01", "0.01"),
            ("79228162514264337593543950335", "10"), ("0.0000000000000000000000000001", "1"),
            ("123456789.987654321", "0.00000001"), ("9223372036854775807", "1"),
            ("9223372036854775808", "1"), ("1", "10000000000000000000000000000"),
            ("0.1", "1"), ("0.001", "1"), ("12345678901234567890.123456789", "0.7") };
        var vectors = operands.Select(pair => {
            decimal a = decimal.Parse(pair.Item1, CultureInfo.InvariantCulture), b = decimal.Parse(pair.Item2, CultureInfo.InvariantCulture);
            decimal q = a / b;
            long? truncated; try { truncated = checked((long)q); } catch (OverflowException) { truncated = null; }
            return new { Dividend = pair.Item1, Divisor = pair.Item2, Quotient = q.ToString(CultureInfo.InvariantCulture),
                Truncated = truncated, DoubleBits = BitConverter.DoubleToInt64Bits((double)q) };
        }).ToArray();
        Assert.AreEqual("0.3333333333333333333333333333", vectors[0].Quotient);
        Assert.AreEqual(-1L, vectors[2].Truncated);
        Assert.IsNull(vectors[8].Truncated);
        // Opt-in export used to regenerate the checked-in Rust parity vectors.
        if (Environment.GetEnvironmentVariable("PARITY_VECTORS_OUTPUT") is { Length: > 0 } path)
            File.WriteAllText(path, JsonSerializer.Serialize(vectors, new JsonSerializerOptions { WriteIndented = true }));
    }
}
