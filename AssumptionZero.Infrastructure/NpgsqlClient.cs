using AssumptionZero.Application.Interfaces;

namespace AssumptionZero.Infrastructure;

using Npgsql;

public sealed class NpgsqlClient(string connectionString) : IDbClient
{
    private readonly NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task<int> ExecuteAsync(
        string sql,
        Action<NpgsqlCommand>? configure = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        configure?.Invoke(command);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<T> ScalarAsync<T>(
        string sql,
        Action<NpgsqlCommand>? configure = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        configure?.Invoke(command);

        var result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is T value)
            return value;

        throw new InvalidOperationException(
            $"The query returned no value of type {typeof(T).Name}.");
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<NpgsqlDataReader, T> map,
        Action<NpgsqlCommand>? configure = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        configure?.Invoke(command);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<T>();

        while (await reader.ReadAsync(cancellationToken))
            results.Add(map(reader));

        return results;
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}