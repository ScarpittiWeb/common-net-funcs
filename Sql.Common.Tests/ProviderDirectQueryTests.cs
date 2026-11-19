using CommonNetFuncs.Sql.Common;
using OdbcDirectQuery = CommonNetFuncs.Sql.Odbc.DirectQuery;
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
