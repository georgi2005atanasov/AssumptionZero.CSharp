using Npgsql;
using NpgsqlTypes;

namespace AssumptionZero.Infrastructure.External;

public static class BinanceFeedReaderExtensions
{
    public static async Task PersistRecordedMessagesAsync(this BinanceFeedReader reader, string connectionString)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
        
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO recorded_messages
                        (recording_id, sequence_number, elapsed_ns, message_type, payload)
                    VALUES ($1, $2, $3, $4, $5)
                    """,
                    connection);
        
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea });
        
                await command.PrepareAsync();
        
                await foreach (BinanceFeedReader.RecordedMessage message in reader.recordedMessages.Reader.ReadAllAsync())
                {
                    command.Parameters[0].Value = message.RecordingId;
                    command.Parameters[1].Value = message.SequenceNumber;
                    command.Parameters[2].Value = message.ElapsedNanoseconds;
                    command.Parameters[3].Value = message.MessageType;
                    command.Parameters[4].Value = message.Payload;
        
                    await command.ExecuteNonQueryAsync();
                }
            }
}