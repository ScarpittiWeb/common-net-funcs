using CommonNetFuncs.Sql.Common;
using System.Data;
using static Xunit.TestContext;
using OdbcDirectQuery = CommonNetFuncs.Sql.Odbc.DirectQuery;
using PostgreSqlDirectQuery = CommonNetFuncs.Sql.PostgreSql.DirectQuery;
using SqlServerDirectQuery = CommonNetFuncs.Sql.SqlServer.DirectQuery;

namespace Sql.Common.Tests;

public sealed class ProviderDirectQueryTests
{
	private const string ConnectionString = "Driver={Unit Test};Server=provider-tests;Database=test;";
	private const string QueryText = "SELECT 1";

	[Theory]
	[InlineData(SqlProvider.SqlServer, ProviderOperation.GetDataTableAsync)]
	[InlineData(SqlProvider.SqlServer, ProviderOperation.GetDataTable)]
	[InlineData(SqlProvider.SqlServer, ProviderOperation.RunUpdateQueryAsync)]
	[InlineData(SqlProvider.SqlServer, ProviderOperation.RunUpdateQuery)]
	[InlineData(SqlProvider.SqlServer, ProviderOperation.GetDataStreamAsync)]
	[InlineData(SqlProvider.SqlServer, ProviderOperation.GetDataStream)]
	[InlineData(SqlProvider.SqlServer, ProviderOperation.GetDataDirectAsync)]
	[InlineData(SqlProvider.Odbc, ProviderOperation.GetDataTableAsync)]
	[InlineData(SqlProvider.Odbc, ProviderOperation.GetDataTable)]
	[InlineData(SqlProvider.Odbc, ProviderOperation.RunUpdateQueryAsync)]
	[InlineData(SqlProvider.Odbc, ProviderOperation.RunUpdateQuery)]
	[InlineData(SqlProvider.Odbc, ProviderOperation.GetDataStreamAsync)]
	[InlineData(SqlProvider.Odbc, ProviderOperation.GetDataStream)]
	[InlineData(SqlProvider.Odbc, ProviderOperation.GetDataDirectAsync)]
	public async Task ProviderOperations_ShouldUseInjectedConnectionFactory_WithoutOpeningDatabase(SqlProvider provider, ProviderOperation operation)
	{
		int factoryCalls = 0;
		string? observedConnectionString = null;
		ProviderFactoryException expectedException = new();
		IDirectQuery query = CreateQuery(provider, expectedException, connectionString =>
		{
			factoryCalls++;
			observedConnectionString = connectionString;
		});

		ProviderFactoryException actualException = await Assert.ThrowsAsync<ProviderFactoryException>(() => InvokeAsync(query, operation));

		actualException.ShouldBeSameAs(expectedException);
		factoryCalls.ShouldBe(1);
		observedConnectionString.ShouldBe(ConnectionString);
	}

	private static IDirectQuery CreateQuery(SqlProvider provider, ProviderFactoryException expectedException, Action<string> observeConnectionString)
	{
		return provider switch
		{
			SqlProvider.SqlServer => new SqlServerDirectQuery(connectionString =>
			{
				observeConnectionString(connectionString);
				throw expectedException;
			}),
			SqlProvider.Odbc => new OdbcDirectQuery(connectionString =>
			{
				observeConnectionString(connectionString);
				throw expectedException;
			}),
			_ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
		};
	}

	private static async Task InvokeAsync(IDirectQuery query, ProviderOperation operation)
	{
		switch (operation)
		{
			case ProviderOperation.GetDataTableAsync:
				await query.GetDataTableAsync(QueryText, ConnectionString);
				return;
			case ProviderOperation.GetDataTable:
				query.GetDataTable(QueryText, ConnectionString);
				return;
			case ProviderOperation.RunUpdateQueryAsync:
				await query.RunUpdateQueryAsync(QueryText, ConnectionString);
				return;
			case ProviderOperation.RunUpdateQuery:
				query.RunUpdateQuery(QueryText, ConnectionString);
				return;
			case ProviderOperation.GetDataStreamAsync:
				await foreach (ProviderRow _ in query.GetDataStreamAsync<ProviderRow>(QueryText, ConnectionString))
				{
				}
				return;
			case ProviderOperation.GetDataStream:
				_ = query.GetDataStream<ProviderRow>(QueryText, ConnectionString).ToArray();
				return;
			case ProviderOperation.GetDataDirectAsync:
				await query.GetDataDirectAsync<ProviderRow>(QueryText, ConnectionString);
				return;
			default:
				throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
		}
	}

	public enum SqlProvider
	{
		SqlServer,
		Odbc
	}

	public enum ProviderOperation
	{
		GetDataTableAsync,
		GetDataTable,
		RunUpdateQueryAsync,
		RunUpdateQuery,
		GetDataStreamAsync,
		GetDataStream,
		GetDataDirectAsync
	}

	private sealed class ProviderFactoryException : Exception;

	private sealed class ProviderRow;
}

public sealed class ProviderDirectQueryZeroRetryTests
{
	private const string SqlText = "SELECT 1";
	private const string SqlServerConnectionString = "Server=localhost;Database=unit-tests;User Id=unit-tests;Password=unit-tests;TrustServerCertificate=True";
	private const string OdbcConnectionString = "Driver={Unit Test};Server=provider-tests;Database=test;";
#if CORE_NATIVE_BUILD
	private const string PostgreSqlConnectionString = "Host=localhost;Database=unit-tests;Username=unit-tests;Password=unit-tests";
#endif


	public enum Provider
	{
		SqlServer,
		Odbc,
#if CORE_NATIVE_BUILD
		PostgreSql
#endif
	}

	public enum Operation
	{
		GetDataTableAsync,
		GetDataTable,
		RunUpdateQueryAsync,
		RunUpdateQuery,
		GetDataStreamAsync,
		GetDataStream,
		GetDataDirectAsync
	}

	private sealed class ProviderRow;

	[Theory]
	[InlineData(Provider.SqlServer, Operation.GetDataTableAsync)]
	[InlineData(Provider.SqlServer, Operation.GetDataTable)]
	[InlineData(Provider.SqlServer, Operation.RunUpdateQueryAsync)]
	[InlineData(Provider.SqlServer, Operation.RunUpdateQuery)]
	[InlineData(Provider.SqlServer, Operation.GetDataStreamAsync)]
	[InlineData(Provider.SqlServer, Operation.GetDataStream)]
	[InlineData(Provider.SqlServer, Operation.GetDataDirectAsync)]
	[InlineData(Provider.Odbc, Operation.GetDataTableAsync)]
	[InlineData(Provider.Odbc, Operation.GetDataTable)]
	[InlineData(Provider.Odbc, Operation.RunUpdateQueryAsync)]
	[InlineData(Provider.Odbc, Operation.RunUpdateQuery)]
	[InlineData(Provider.Odbc, Operation.GetDataStreamAsync)]
	[InlineData(Provider.Odbc, Operation.GetDataStream)]
	[InlineData(Provider.Odbc, Operation.GetDataDirectAsync)]
#if CORE_NATIVE_BUILD
	[InlineData(Provider.PostgreSql, Operation.GetDataTableAsync)]
	[InlineData(Provider.PostgreSql, Operation.GetDataTable)]
	[InlineData(Provider.PostgreSql, Operation.RunUpdateQueryAsync)]
	[InlineData(Provider.PostgreSql, Operation.RunUpdateQuery)]
	[InlineData(Provider.PostgreSql, Operation.GetDataStreamAsync)]
	[InlineData(Provider.PostgreSql, Operation.GetDataStream)]
	[InlineData(Provider.PostgreSql, Operation.GetDataDirectAsync)]
#endif
	public async Task DefaultFactory_WithZeroRetries_ShouldBuildWrapperAndReturnEmptyResult(Provider provider, Operation operation)
	{
		IDirectQuery query = provider switch
		{
			Provider.SqlServer => new SqlServerDirectQuery(),
			Provider.Odbc => new OdbcDirectQuery(),
#if CORE_NATIVE_BUILD
			Provider.PostgreSql => new PostgreSqlDirectQuery(),
#endif
			_ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
		};
		string connectionString = provider switch
		{
			Provider.SqlServer => SqlServerConnectionString,
			Provider.Odbc => OdbcConnectionString,
#if CORE_NATIVE_BUILD
			Provider.PostgreSql => PostgreSqlConnectionString,
#endif
			_ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
		};

		switch (operation)
		{
			case Operation.GetDataTableAsync:
				using (DataTable table = await query.GetDataTableAsync(SqlText, connectionString, maxRetry: 0, cancellationToken: Current.CancellationToken))
				{
					table.Rows.Count.ShouldBe(0);
					table.Columns.Count.ShouldBe(0);
				}
				break;
			case Operation.GetDataTable:
				using (DataTable table = query.GetDataTable(SqlText, connectionString, maxRetry: 0))
				{
					table.Rows.Count.ShouldBe(0);
					table.Columns.Count.ShouldBe(0);
				}
				break;
			case Operation.RunUpdateQueryAsync:
				(await query.RunUpdateQueryAsync(SqlText, connectionString, maxRetry: 0, cancellationToken: Current.CancellationToken)).ShouldSatisfyAllConditions(
					result => result.Success.ShouldBeFalse(),
					result => result.RecordsChanged.ShouldBe(0));
				break;
			case Operation.RunUpdateQuery:
				query.RunUpdateQuery(SqlText, connectionString, maxRetry: 0).ShouldSatisfyAllConditions(
					result => result.Success.ShouldBeFalse(),
					result => result.RecordsChanged.ShouldBe(0));
				break;
			case Operation.GetDataStreamAsync:
				{
					int count = 0;
					await foreach (ProviderRow _ in query.GetDataStreamAsync<ProviderRow>(SqlText, connectionString, maxRetry: 0, cancellationToken: Current.CancellationToken))
					{
						count++;
					}
					count.ShouldBe(0);
					break;
				}
			case Operation.GetDataStream:
				query.GetDataStream<ProviderRow>(SqlText, connectionString, maxRetry: 0, cancellationToken: Current.CancellationToken).ShouldBeEmpty();
				break;
			case Operation.GetDataDirectAsync:
				(await query.GetDataDirectAsync<ProviderRow>(SqlText, connectionString, maxRetry: 0, cancellationToken: Current.CancellationToken)).ShouldBeEmpty();
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
		}
	}

}
