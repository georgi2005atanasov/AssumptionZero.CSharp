using Npgsql;

namespace AssumptionZero.Application.Interfaces;

public interface IDbClient : IAsyncDisposable
{
    Task<int> ExecuteAsync(
        string sql,
        Action<NpgsqlCommand>? configure = null,
        CancellationToken cancellationToken = default);

    Task<T> ScalarAsync<T>(
        string sql,
        Action<NpgsqlCommand>? configure = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<NpgsqlDataReader, T> map,
        Action<NpgsqlCommand>? configure = null,
        CancellationToken cancellationToken = default);
}