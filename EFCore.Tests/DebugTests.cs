using CommonNetFuncs.EFCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Collections.Concurrent;
using System.Reflection;

namespace EFCore.Tests;

public sealed class DebugTests
{
	private const string SqlServerConnectionString = "Server=localhost;Database=test;Trusted_Connection=true;TrustServerCertificate=true";

	private const string PostgreSqlConnectionString = "Host=localhost;Database=test";

	[Fact]
	public void RenderQueryAsScript_WithSqlServerContext_ReturnsRunnableDeclareScript()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseSqlServer(SqlServerConnectionString));
		string name = "Pink Floyd";

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => e.Name == name);

		string result = query.RenderQueryAsScript(context);

		result.ShouldContain("DECLARE @name nvarchar(4000) = N'Pink Floyd';");
		result.ShouldContain("@name");
		result.ShouldNotContain("-- ====");
	}

	[Fact]
	public void RenderQueryAsScript_WithSqlDialectSqlServer_ReturnsRunnableDeclareScript()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseSqlServer(SqlServerConnectionString));
		int value = 5;

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => e.Value == value);

		string result = query.RenderQueryAsScript(SqlDialect.SqlServer);

		result.ShouldContain("DECLARE @value int = 5;");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlContext_InlinesStringParameterAsLiteral()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		string name = "Pink Floyd";

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => e.Name == name);

		string result = query.RenderQueryAsScript(context);

		result.ShouldContain("-- ==== Parameters (inlined as literals in the command below) ====");
		result.ShouldContain("-- @name = 'Pink Floyd'");
		result.ShouldContain("-- ==== Command ====");
		string commandText = result[(result.IndexOf("-- ==== Command ====", StringComparison.Ordinal))..];
		commandText.ShouldContain("'Pink Floyd'");
		commandText.ShouldNotContain("@name");
		commandText.ShouldNotContain("DECLARE");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlDialect_EscapesEmbeddedQuotes()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		string name = "O'Brian";

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => e.Name == name);

		string result = query.RenderQueryAsScript(SqlDialect.PostgreSql);

		result.ShouldContain("-- @name = 'O''Brian'");
		result.ShouldContain("'O''Brian'");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlDialect_FormatsBooleanAsTrueOrFalse()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		bool active = true;

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => e.IsActive == active);

		string result = query.RenderQueryAsScript(SqlDialect.PostgreSql);

		result.ShouldContain("-- @active = TRUE");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlDialect_FormatsIntegerAsUnquotedNumber()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		int value = 42;

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => e.Value == value);

		string result = query.RenderQueryAsScript(SqlDialect.PostgreSql);

		result.ShouldContain("-- @value = 42");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlDialect_FormatsDecimalAsUnquotedNumber()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		decimal amount = 123.45m;

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => e.Amount == amount);

		string result = query.RenderQueryAsScript(SqlDialect.PostgreSql);

		result.ShouldContain("-- @amount = 123.45");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlDialect_FormatsDateTimeWithTimestampCast()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		DateTime date = new(2024, 5, 17, 13, 45, 30, DateTimeKind.Unspecified);

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => e.CreatedDate == date);

		string result = query.RenderQueryAsScript(SqlDialect.PostgreSql);

		result.ShouldContain("'2024-05-17 13:45:30.000000'::timestamp");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlDialect_FormatsGuidAsQuotedString()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		Guid id = Guid.Parse("11111111-2222-3333-4444-555555555555");

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => e.UniqueId == id);

		string result = query.RenderQueryAsScript(SqlDialect.PostgreSql);

		result.ShouldContain("-- @id = '11111111-2222-3333-4444-555555555555'");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlDialect_RendersMultipleParameters()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		string name = "Pink Floyd";
		int value = 5;

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => e.Name == name && e.Value == value);

		string result = query.RenderQueryAsScript(SqlDialect.PostgreSql);

		result.ShouldContain("-- @name = 'Pink Floyd'");
		result.ShouldContain("-- @value = 5");
		result.ShouldContain("'Pink Floyd'");
		result.ShouldContain("= 5");
	}

	[Fact]
	public void RenderQueryAsScript_WithNoParameters_OmitsParametersSection()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));

		IQueryable<DebugTestEntity> query = context.Entities;

		string result = query.RenderQueryAsScript(SqlDialect.PostgreSql);

		result.ShouldNotContain("Parameters");
		result.ShouldContain("-- ==== Command ====");
	}

	[Fact]
	public void RenderQueryAsScript_DeterminesDialectFromProviderName_SqlServer()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseSqlServer(SqlServerConnectionString));

		string result = context.Entities.RenderQueryAsScript(context);

		result.ShouldNotContain("-- ====");
	}

	[Fact]
	public void RenderQueryAsScript_DeterminesDialectFromProviderName_PostgreSql()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));

		string result = context.Entities.RenderQueryAsScript(context);

		result.ShouldContain("-- ==== Command ====");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlDialect_InlinesListContainsAsArrayLiteral()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		List<string> names = ["a", "b", "c"];

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => names.Contains(e.Name));

		string result = query.RenderQueryAsScript(SqlDialect.PostgreSql);

		string commandText = result[(result.IndexOf("-- ==== Command ====", StringComparison.Ordinal))..];
		commandText.ShouldContain("ARRAY['a', 'b', 'c']");
		commandText.ShouldNotContain("@names");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlDialect_KeepsNumericArrayElementsQuoted()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		List<int> values = [1, 2, 3];

		IQueryable<DebugTestEntity> query = context.Entities.Where(e => values.Contains(e.Value));

		string result = query.RenderQueryAsScript(SqlDialect.PostgreSql);

		string commandText = result[(result.IndexOf("-- ==== Command ====", StringComparison.Ordinal))..];

		// Npgsql always quotes array elements as strings in its debug comment regardless of the underlying element
		// type, so they must stay quoted text literals rather than being unquoted as numbers - otherwise a
		// "col::text LIKE ANY(ARRAY[...])" comparison elsewhere in the query would compare text against an int[].
		commandText.ShouldContain("ARRAY['1', '2', '3']");
		commandText.ShouldNotContain("ARRAY[1, 2, 3]");
	}

	[Fact]
	public void RenderQueryAsScript_WithPostgreSqlDialect_InlinesFromSqlInterpolatedParameters()
	{
		using DebugTestDbContext context = CreateContext(o => o.UseNpgsql(PostgreSqlConnectionString));
		string[] ids = ["1", "2"];
		List<string> names = ["a", "b"];
		string? nul = null;
		DateOnly? d = new DateOnly(2024, 1, 2);
		string empty = string.Empty; string[] emptyArr = [];
		bool f = false;
		List<string> weird = ["it's", "a,b", "100%"];
		string? multi = "line1\nline2";
		int? n = 5;
		FormattableString sql = $"""
			SELECT * FROM "Entities" WHERE ({nul}::text IS NULL OR "Name" LIKE ANY(ARRAY[{ids}])) AND ({d}::text IS NULL) AND ({n}::text IS NULL OR "Value" = {n}) AND "Name" = ANY(ARRAY[{names.ToArray()}]) AND "Id" = {Microsoft.EntityFrameworkCore.EntityState.Added} AND {empty} = {f} AND x = ANY(ARRAY[{emptyArr}]) AND y = ANY(ARRAY[{weird.ToArray()}]) AND {multi} = 1 AND {d} BETWEEN {d} AND {d}
			""";
		string result = context.Entities.FromSql(sql).AsNoTracking().RenderQueryAsScript(SqlDialect.PostgreSql);

		result[result.IndexOf("-- ==== Command ====", StringComparison.Ordinal)..].ShouldNotContain("@p");
		result.ShouldContain("ARRAY['it''s', 'a,b', '100%']");
		result.ShouldContain("'2024-01-02'::date");
	}

	private static DebugTestDbContext CreateContext(Action<DbContextOptionsBuilder<DebugTestDbContext>> configure)
	{
		DbContextOptionsBuilder<DebugTestDbContext> builder = new();
		configure(builder);
		return new DebugTestDbContext(builder.Options);
	}
}

internal sealed class DebugTestEntity
{
	public int Id { get; set; }

	public string Name { get; set; } = string.Empty;

	public int Value { get; set; }

	public bool IsActive { get; set; }

	public decimal Amount { get; set; }

	public DateTime CreatedDate { get; set; }

	public Guid UniqueId { get; set; }
}

internal sealed class DebugTestDbContext(DbContextOptions<DebugTestDbContext> options) : DbContext(options)
{
	public DbSet<DebugTestEntity> Entities => Set<DebugTestEntity>();
}

public sealed class EfCoreDebugBehaviorTests
{
























	private static (IServiceProvider Provider, CoverageContext Context) CreateProvider()
	{
		ServiceCollection services = new();
		services.AddDbContextPool<CoverageContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
		ServiceProvider provider = services.BuildServiceProvider();
		return (provider, provider.GetRequiredService<CoverageContext>());
	}

	private static CoverageContext CreateContext() =>
		new(new DbContextOptionsBuilder<CoverageContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

	private static IServiceProvider CreateThrowingTrackingProvider()
	{
		ServiceCollection services = new();
		services.AddDbContext<ThrowOnTrackingContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
		return services.BuildServiceProvider();
	}

	private static IAsyncEnumerable<CoverageEntity> InvokeStreamingHelper(Func<Func<bool, IQueryable<CoverageEntity>>> builderFactory)
	{
		MethodInfo method = typeof(BaseDbContextActions<CoverageEntity, CoverageContext>)
			.GetMethod("ExecuteStreamingWithCircularRefHandling", BindingFlags.Static | BindingFlags.NonPublic)!
			.MakeGenericMethod(typeof(CoverageEntity));
		return (IAsyncEnumerable<CoverageEntity>)method.Invoke(null, [builderFactory(), CancellationToken.None])!;
	}

	private static ConcurrentDictionary<Type, bool> GetCircularReferenceCache() =>
		(ConcurrentDictionary<Type, bool>)typeof(BaseDbContextActions<CoverageEntity, CoverageContext>)
			.GetField("circularReferencingEntities", BindingFlags.Static | BindingFlags.NonPublic)!
			.GetValue(null)!;

	private static (IProperty Property, IEntityType EntityType, DbContext Context) CreateDateTimeMetadata(
		string? providerName, string? storeType)
	{
		IProperty property = Substitute.For<IProperty>();
		property.GetColumnType().Returns(storeType);
		IEntityType entityType = Substitute.For<IEntityType>();
		entityType.FindProperty(nameof(CoverageDateEntity.CreatedDate)).Returns(property);
		return (property, entityType, CreateMockContext(providerName));
	}

	private static DbContext CreateMockContext(string? providerName)
	{
		DatabaseFacade database = Substitute.For<DatabaseFacade>(CreateContext());
		database.ProviderName.Returns(providerName);
		DbContext context = Substitute.For<DbContext>();
		context.Database.Returns(database);
		return context;
	}

	private static DateTime InvokeNormalize(
		DateTime value,
		IProperty property,
		IEntityType entityType,
		DbContext context,
		PropertyInfo? clrProperty = null)
	{
		MethodInfo method = typeof(Collections).GetMethod(
			"NormalizeDateTimeForDatabase", BindingFlags.Static | BindingFlags.NonPublic)!;
		return (DateTime)method.Invoke(null,
		[
			value,
			clrProperty ?? typeof(CoverageDateEntity).GetProperty(nameof(CoverageDateEntity.CreatedDate))!,
			entityType,
			context
		])!;
	}

	[Fact]
	public void Debug_PrivateDialectParser_HandlesBoundaryInputs()
	{
		typeof(CommonNetFuncs.EFCore.Debug).InvokeStatic<SqlDialect>("GetDialect", [null])
			.ShouldBe(SqlDialect.SqlServer);
		typeof(CommonNetFuncs.EFCore.Debug).InvokeStatic<SqlDialect>("GetDialect", string.Empty)
			.ShouldBe(SqlDialect.SqlServer);
		typeof(CommonNetFuncs.EFCore.Debug).InvokeStatic<SqlDialect>("GetDialect", "custom.NPGSQL.provider")
			.ShouldBe(SqlDialect.PostgreSql);

		string malformed = typeof(CommonNetFuncs.EFCore.Debug).InvokeStatic<string>(
			"RenderPostgreSqlScript", "-- not-a-parameter\nSELECT 1")!;
		malformed.ShouldContain("SELECT 1");

		typeof(CommonNetFuncs.EFCore.Debug).InvokeStatic<string>(
			"FormatPostgreSqlLiteral", "not-a-date", "DateTime").ShouldBe("'not-a-date'");
		typeof(CommonNetFuncs.EFCore.Debug).InvokeStatic<string>(
			"FormatPostgreSqlLiteral", "2024-03-02T01:02:03+02:30", "DateTime")
			.ShouldBe("'2024-03-02 01:02:03.000000+02:30'::timestamptz");
		typeof(CommonNetFuncs.EFCore.Debug).InvokeStatic<string>(
			"FormatPostgreSqlArrayLiteral", " 1, 2, three ")
			.ShouldBe("ARRAY['1', '2', 'three']");
	}

}
