using System.Net.WebSockets;
using AssumptionZero.Infrastructure.External;
using AssumptionZero.Simulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace AssumptionZero.Tests;

[TestClass]
public class RecordingRepositoryTests
{
    [TestMethod]
    [TestCategory("PostgreSQL")]
    public async Task ListsSelectsAndOrdersExistingFormatAndReadsRecorderOutput()
    {
        string? connectionString = Environment.GetEnvironmentVariable("PG_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Inconclusive("Set PG_TEST_CONNECTION to run the isolated PostgreSQL integration test.");
        string schema = "replay_test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Pooling = false };
            await using var setup = new NpgsqlConnection(scoped.ConnectionString);
            await setup.OpenAsync();
            await using (var init = new NpgsqlCommand(
                             await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "init.sql")), setup))
                await init.ExecuteNonQueryAsync();
            Guid first = Guid.NewGuid(), second = Guid.NewGuid();
            foreach (var row in new[] { (first, 2L, 200L), (second, 1L, 500L), (first, 1L, 100L) })
            {
                await using var insert = new NpgsqlCommand("""
                                                           INSERT INTO recorded_messages (recording_id, sequence_number, elapsed_ns, message_type, payload)
                                                           VALUES ($1, $2, $3, $4, $5)
                                                           """, setup);
                insert.Parameters.AddWithValue(row.Item1);
                insert.Parameters.AddWithValue(row.Item2);
                insert.Parameters.AddWithValue(row.Item3);
                insert.Parameters.AddWithValue((short)WebSocketMessageType.Binary);
                insert.Parameters.AddWithValue(new byte[] { (byte)row.Item2 });
                await insert.ExecuteNonQueryAsync();
            }

            var repository = new RecordingRepository(scoped.ConnectionString);
            var summaries = await repository.ListAsync();
            Assert.AreEqual(2, summaries.Count);
            Assert.AreEqual(new RecordingSummary(first, 2, 2, 200), summaries.Single(row => row.Id == first));
            var loaded = await repository.LoadAsync(first);
            CollectionAssert.AreEqual(new long[] { 1, 2 }, loaded.Messages.Select(row => row.SequenceNumber).ToArray());
            CollectionAssert.AreEqual(new long[] { 100, 200 },
                loaded.Messages.Select(row => row.ElapsedNanoseconds).ToArray());
            CollectionAssert.AreEqual(new byte[] { 2 }, loaded.Messages[1].Payload);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => repository.LoadAsync(Guid.NewGuid()));

            // Exercise the existing recorder's persistence code against the unchanged table definition.
            Guid captured = Guid.NewGuid();
            var recorder = new BinanceFeedReader("test", [], "ws://127.0.0.1/stream");
            byte[] payload = WebSocketReplayTests.QuotePayload();
            recorder.recordedMessages.Writer.TryWrite(
                new(captured, 1, 123, (short)WebSocketMessageType.Binary, payload));
            recorder.recordedMessages.Writer.TryComplete();
            await recorder.PersistRecordedMessages(scoped.ConnectionString);
            var persisted = await repository.LoadAsync(captured);
            Assert.AreEqual(123L, persisted.Messages[0].ElapsedNanoseconds);
            CollectionAssert.AreEqual(payload, persisted.Messages[0].Payload);

            await using var corrupt =
                new NpgsqlCommand(
                    "UPDATE recorded_messages SET sequence_number = 3 WHERE recording_id = $1 AND sequence_number = 2",
                    setup);
            corrupt.Parameters.AddWithValue(first);
            await corrupt.ExecuteNonQueryAsync();
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => repository.LoadAsync(first));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}