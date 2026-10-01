using System.Net.WebSockets;
using Npgsql;

namespace AssumptionZero.Simulator;

public sealed record ReplayMessage(long SequenceNumber, long ElapsedNanoseconds,
    WebSocketMessageType MessageType, byte[] Payload);

public sealed record RecordingSummary(Guid Id, long MessageCount, long PayloadBytes, long DurationNanoseconds);

public sealed class Recording
{
    public Guid Id { get; }
    public IReadOnlyList<ReplayMessage> Messages { get; }
    public long PayloadBytes { get; }
    public long DurationNanoseconds => Messages[^1].ElapsedNanoseconds;

    public Recording(Guid id, IEnumerable<ReplayMessage> messages)
    {
        Id = id;
        var rows = messages.ToArray();
        if (rows.Length == 0)
            throw new InvalidDataException($"Recording {id} is empty or does not exist.");
        long previousTime = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row.SequenceNumber != (long)i + 1)
                throw new InvalidDataException($"Expected sequence {i + 1}, found {row.SequenceNumber}.");
            if (row.ElapsedNanoseconds < previousTime)
                throw new InvalidDataException($"Negative or decreasing elapsed_ns at sequence {row.SequenceNumber}.");
            if (row.MessageType is not (WebSocketMessageType.Binary or WebSocketMessageType.Text))
                throw new InvalidDataException($"Unsupported message type at sequence {row.SequenceNumber}.");
            if (row.Payload.Length > 1024 * 1024)
                throw new InvalidDataException($"Payload exceeds 1 MB at sequence {row.SequenceNumber}.");
            previousTime = row.ElapsedNanoseconds;
            PayloadBytes = checked(PayloadBytes + row.Payload.Length);
        }
        Messages = Array.AsReadOnly(rows);
    }
}

public sealed class RecordingRepository(string connectionString)
{
    public async Task<IReadOnlyList<RecordingSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT recording_id, count(*), sum(octet_length(payload))::bigint, max(elapsed_ns)
            FROM recorded_messages GROUP BY recording_id ORDER BY recording_id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RecordingSummary>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        return result;
    }

    public async Task<Recording> LoadAsync(Guid id, CancellationToken cancellationToken = default, int? maxMessages = null)
    {
        if (maxMessages is <= 0) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT sequence_number, elapsed_ns, message_type, payload
            FROM recorded_messages WHERE recording_id = $1 ORDER BY sequence_number LIMIT $2
            """, connection);
        command.Parameters.AddWithValue(id);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = (object?)maxMessages ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var messages = new List<ReplayMessage>();
        while (await reader.ReadAsync(cancellationToken))
            messages.Add(new(reader.GetInt64(0), reader.GetInt64(1),
                (WebSocketMessageType)reader.GetInt16(2), reader.GetFieldValue<byte[]>(3)));
        return new Recording(id, messages);
    }
}
