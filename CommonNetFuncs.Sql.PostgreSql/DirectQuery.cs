using System.Data;
using System.Runtime.CompilerServices;
using CommonNetFuncs.Sql.Common;
using Npgsql;
using static CommonNetFuncs.Sql.Common.DirectQuery;

namespace CommonNetFuncs.Sql.PostgreSql;

/// <summary>
/// Interact with databases by using direct queries
/// </summary>
public class DirectQuery(Func<string, NpgsqlConnection>? connectionFactory = null) : IDirectQuery
{
	private readonly Func<string, NpgsqlConnection> connectionFactory = connectionFactory ?? (connStr => new NpgsqlConnection(connStr));

	/// <summary>
	/// Returns a DataTable using the SQL and data connection passed to the function
	/// </summary>
	/// <param name="sql">Select query to retrieve populate datatable.</param>
	/// <param name="connStr">Connection string to run the query on</param>
	/// <param name="commandTimeoutSeconds">Query execution timeout length in seconds</param>
	/// <param name="maxRetry">Number of times to re-try executing the command on failure</param>
	/// <returns><see cref="DataTable"/> containing the results of the SQL query</returns>
	public async Task<DataTable> GetDataTableAsync(string sql, string connStr, int commandTimeoutSeconds = 30, int maxRetry = 3, CancellationToken cancellationToken = default)
	{
		await using NpgsqlConnection sqlConn = connectionFactory(connStr);
		await using NpgsqlCommand sqlCmd = new(sql, sqlConn);
		return await GetDataTableInternalAsync(sqlConn, sqlCmd, commandTimeoutSeconds, maxRetry, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Returns a DataTable using the SQL and data connection passed to the function
	/// </summary>
	/// <param name="sql">Select query to retrieve populate datatable.</param>
	/// <param name="connStr">Connection string to run the query on</param>
	/// <param name="commandTimeoutSeconds">Query execution timeout length in seconds</param>
	/// <param name="maxRetry">Number of times to re-try executing the command on failure</param>
	/// <returns><see cref="DataTable"/> containing the results of the SQL query</returns>
	public DataTable GetDataTable(string sql, string connStr, int commandTimeoutSeconds = 30, int maxRetry = 3)
	{
		using NpgsqlConnection sqlConn = connectionFactory(connStr);
		using NpgsqlCommand sqlCmd = new(sql, sqlConn);
		return GetDataTableInternal(sqlConn, sqlCmd, commandTimeoutSeconds, maxRetry);
	}

	/// <summary>
	/// Execute an update query asynchronously
	/// </summary>
	/// <param name="sql">Update query to retrieve run against database</param>
	/// <param name="connStr">Connection string to run the query on</param>
	/// <param name="commandTimeoutSeconds">Query execution timeout length in seconds</param>
	/// <param name="maxRetry">Number of times to re-try executing the command on failure</param>
	/// <returns>UpdateResult containing the number of records altered and whether the query executed successfully</returns>
	public async Task<UpdateResult> RunUpdateQueryAsync(string sql, string connStr, int commandTimeoutSeconds = 30, int maxRetry = 3, CancellationToken cancellationToken = default)
	{
		await using NpgsqlConnection sqlConn = connectionFactory(connStr);
		await using NpgsqlCommand sqlCmd = new(sql, sqlConn);
		return await RunUpdateQueryInternalAsync(sqlConn, sqlCmd, commandTimeoutSeconds, maxRetry, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Execute an update query synchronously
	/// </summary>
	/// <param name="sql">Update query to retrieve run against database</param>
	/// <param name="connStr">Connection string to run the query on</param>
	/// <param name="commandTimeoutSeconds">Query execution timeout length in seconds</param>
	/// <param name="maxRetry">Number of times to re-try executing the command on failure</param>
	/// <returns>UpdateResult containing the number of records altered and whether the query executed successfully</returns>
	public UpdateResult RunUpdateQuery(string sql, string connStr, int commandTimeoutSeconds = 30, int maxRetry = 3)
	{
		using NpgsqlConnection sqlConn = connectionFactory(connStr);
		using NpgsqlCommand sqlCmd = new(sql, sqlConn);
		return RunUpdateQueryInternal(sqlConn, sqlCmd, commandTimeoutSeconds, maxRetry);
	}

	/// <summary>
	/// Returns a IAsyncEnumerable using the SQL and data connection passed to the function
	/// </summary>
	/// <param name="sql">Select query to retrieve populate datatable.</param>
	/// <param name="connStr">Connection string to run the query on</param>
	/// <param name="commandTimeoutSeconds">Query execution timeout length in seconds</param>
	/// <param name="maxRetry">Number of times to re-try executing the command on failure</param>
	/// <returns><see cref="DataTable"/> containing the results of the SQL query</returns>
	public async IAsyncEnumerable<T> GetDataStreamAsync<T>(string sql, string connStr, int commandTimeoutSeconds = 30, int maxRetry = 3, bool useCache = true, [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class, new()
	{
		await using NpgsqlConnection sqlConn = connectionFactory(connStr);
		await using NpgsqlCommand sqlCmd = new(sql, sqlConn);

		await foreach (T item in GetDataStreamWithRetryAsync<T>(sqlConn, sqlCmd, commandTimeoutSeconds, maxRetry, useCache, cancellationToken).ConfigureAwait(false))
		{
			yield return item;
		}
	}

	/// <summary>
	/// Returns a IAsyncEnumerable using the SQL and data connection passed to the function
	/// </summary>
	/// <param name="sql">Select query to retrieve populate datatable.</param>
	/// <param name="connStr">Connection string to run the query on</param>
	/// <param name="commandTimeoutSeconds">Query execution timeout length in seconds</param>
	/// <param name="maxRetry">Number of times to re-try executing the command on failure</param>
	/// <returns><see cref="DataTable"/> containing the results of the SQL query</returns>
	public IEnumerable<T> GetDataStream<T>(string sql, string connStr, int commandTimeoutSeconds = 30, int maxRetry = 3, bool useCache = true, CancellationToken cancellationToken = default) where T : class, new()
	{
		using NpgsqlConnection sqlConn = connectionFactory(connStr);
		using NpgsqlCommand sqlCmd = new(sql, sqlConn);

		return GetDataStreamWithRetry<T>(sqlConn, sqlCmd, commandTimeoutSeconds, maxRetry, useCache, cancellationToken);
	}

	/// <summary>
	/// Returns an IEnumerable of TObj resulting from the SQL query
	/// </summary>
	/// <param name="sql">Select query to retrieve populate datatable.</param>
	/// <param name="connStr">Connection string to run the query on</param>
	/// <param name="commandTimeoutSeconds">Query execution timeout length in seconds</param>
	/// <param name="maxRetry">Number of times to re-try executing the command on failure</param>
	/// <returns><see cref="DataTable"/> containing the results of the SQL query</returns>
	public async Task<IEnumerable<T>> GetDataDirectAsync<T>(string sql, string connStr, int commandTimeoutSeconds = 30, int maxRetry = 3, bool useCache = true, CancellationToken cancellationToken = default) where T : class, new()
	{
		await using NpgsqlConnection sqlConn = connectionFactory(connStr);
		await using NpgsqlCommand sqlCmd = new(sql, sqlConn);
		return await Common.DirectQuery.GetDataDirectAsync<T>(sqlConn, sqlCmd, commandTimeoutSeconds, maxRetry, useCache, cancellationToken).ConfigureAwait(false);
	}
}
