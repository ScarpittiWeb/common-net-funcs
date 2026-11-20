using AutoFixture;
using CommonNetFuncs.EFCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Reflection;

namespace EFCore.Tests;

public sealed partial class BaseDbContextActionsTests
{
	private readonly IServiceProvider serviceProvider;
	private readonly Fixture fixture;
	private readonly TestDbContext context;

	public BaseDbContextActionsTests()
	{
		fixture = new Fixture();
		fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList().ForEach(x => fixture.Behaviors.Remove(x));
		fixture.Behaviors.Add(new OmitOnRecursionBehavior());

		ServiceCollection services = new();
		services.AddDbContextPool<TestDbContext>(options => options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));
		serviceProvider = services.BuildServiceProvider();
		context = serviceProvider.GetRequiredService<TestDbContext>();
	}
}

public sealed class BaseDbContextActionsBehaviorTests
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

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GetAll_BoolProjectionSelector_ShouldHonorFullMode(bool full)
	{
		(IServiceProvider provider, CoverageContext context) = CreateProvider();
		await using (context)
		{
			context.Entities.AddRange(
				new CoverageEntity { Id = 1, Name = "one" },
				new CoverageEntity { Id = 2, Name = "two" });
			await context.SaveChangesAsync(TestContext.Current.CancellationToken);

			BaseDbContextActions<CoverageEntity, CoverageContext> actions = new(provider);
			List<string>? names = await actions.GetAll(full, x => x.Name, cancellationToken: TestContext.Current.CancellationToken);

			names!.Order().ShouldBe(["one", "two"]);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GetAllStreaming_BoolSelector_ShouldHonorFullMode(bool full)
	{
		(IServiceProvider provider, CoverageContext context) = CreateProvider();
		await using (context)
		{
			context.Entities.Add(new CoverageEntity { Id = 1, Name = "one" });
			await context.SaveChangesAsync(TestContext.Current.CancellationToken);

			BaseDbContextActions<CoverageEntity, CoverageContext> actions = new(provider);
			List<int> ids = [];
			await foreach (CoverageEntity entity in actions.GetAllStreaming(full, cancellationToken: TestContext.Current.CancellationToken)!)
			{
				ids.Add(entity.Id);
			}

			ids.ShouldBe([1]);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GetAllStreaming_BoolProjectionSelector_ShouldHonorFullMode(bool full)
	{
		(IServiceProvider provider, CoverageContext context) = CreateProvider();
		await using (context)
		{
			context.Entities.AddRange(
				new CoverageEntity { Id = 1, Name = "one" },
				new CoverageEntity { Id = 2, Name = "two" });
			await context.SaveChangesAsync(TestContext.Current.CancellationToken);

			BaseDbContextActions<CoverageEntity, CoverageContext> actions = new(provider);
			List<int> ids = [];
			await foreach (int id in actions.GetAllStreaming(full, x => x.Id, cancellationToken: TestContext.Current.CancellationToken)!)
			{
				ids.Add(id);
			}

			ids.Order().ShouldBe([1, 2]);
		}
	}

	[Theory]
	[InlineData(false, null)]
	[InlineData(false, true)]
	[InlineData(false, false)]
	[InlineData(true, null)]
	[InlineData(true, true)]
	[InlineData(true, false)]
	public async Task GetAllFull_SplitAndTrackingModes_ReturnExpectedRows(bool trackEntities, bool? splitQueryOverride)
	{
		(IServiceProvider provider, CoverageContext context) = CreateProvider();
		await using (context)
		{
			context.Entities.Add(new CoverageEntity { Id = 1, Name = "tracked" });
			await context.SaveChangesAsync(TestContext.Current.CancellationToken);
			context.ChangeTracker.Clear();

			BaseDbContextActions<CoverageEntity, CoverageContext> actions = new(provider);
			List<CoverageEntity>? result = await actions.GetAllFull(trackEntities: trackEntities, fullQueryOptions: new FullQueryOptions(splitQueryOverride), cancellationToken: TestContext.Current.CancellationToken);

			result.ShouldNotBeNull();
			result.Single().Name.ShouldBe("tracked");
		}
	}

	[Fact]
	public async Task ExecuteStreamingWithCircularRefHandling_RetrySuccess_AddsEntityTypeAndReturnsRows()
	{
		await using CoverageContext context = CreateContext();
		context.Entities.AddRange(new CoverageEntity { Id = 1, Name = "one" });
		await context.SaveChangesAsync(TestContext.Current.CancellationToken);
		ConcurrentDictionary<Type, bool> cache = GetCircularReferenceCache();
		cache.Clear();
		int calls = 0;

		IAsyncEnumerable<CoverageEntity> stream = InvokeStreamingHelper(() =>
		{
			Func<bool, IQueryable<CoverageEntity>> builder = handling =>
			{
				calls++;
				if (!handling)
				{
					throw new InvalidOperationException("retry this operation");
				}

				return context.Entities.AsNoTracking();
			};
			return builder;
		});

		List<CoverageEntity> result = await stream.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

		result.Single().Id.ShouldBe(1);
		calls.ShouldBe(2);
		cache.ContainsKey(typeof(CoverageEntity)).ShouldBeTrue();
	}

	[Fact]
	public async Task ExecuteStreamingWithCircularRefHandling_DifferentInvalidOperationException_ReturnsEmpty()
	{
		int calls = 0;
		IAsyncEnumerable<CoverageEntity> stream = InvokeStreamingHelper(() =>
		{
			Func<bool, IQueryable<CoverageEntity>> builder = _ =>
			{
				calls++;
				InvalidOperationException exception = new("not a circular-reference exception");
				typeof(Exception).GetField("_HResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(exception, -2146233088);
				throw exception;
			};
			return builder;
		});

		(await stream.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBeEmpty();
		calls.ShouldBe(1);
	}

	[Fact]
	public async Task ExecuteStreamingWithCircularRefHandling_GeneralException_ReturnsEmpty()
	{
		int calls = 0;
		IAsyncEnumerable<CoverageEntity> stream = InvokeStreamingHelper(() =>
		{
			Func<bool, IQueryable<CoverageEntity>> builder = _ =>
			{
				calls++;
				throw new ArgumentException("builder failed");
			};
			return builder;
		});

		(await stream.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBeEmpty();
		calls.ShouldBe(1);
	}

	[Fact]
	public async Task ExecuteStreamingWithCircularRefHandling_SecondaryException_ReturnsEmpty()
	{
		int calls = 0;
		IAsyncEnumerable<CoverageEntity> stream = InvokeStreamingHelper(() =>
		{
			Func<bool, IQueryable<CoverageEntity>> builder = handling =>
			{
				calls++;
				throw handling
					? new ArgumentException("retry failed")
					: new InvalidOperationException("retry this operation");
			};
			return builder;
		});

		(await stream.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBeEmpty();
		calls.ShouldBe(2);
	}

	[Fact]
	public async Task ApplyGlobalFilters_AllOptionShapes_PreserveExpectedRows()
	{
		MethodInfo method = typeof(BaseDbContextActions<FilteredCoverageEntity, FilteredCoverageContext>)
			.GetMethod("ApplyGlobalFilters", BindingFlags.Static | BindingFlags.NonPublic)!;
		await using FilteredCoverageContext filteredContext = new(
			new DbContextOptionsBuilder<FilteredCoverageContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
		filteredContext.Entities.AddRange(
			new FilteredCoverageEntity { Id = 1, IsActive = true },
			new FilteredCoverageEntity { Id = 2, IsActive = false });
		await filteredContext.SaveChangesAsync(TestContext.Current.CancellationToken);

		(GlobalFilterOptions? Options, int[] ExpectedIds)[] cases =
		[
			(null, [1]),
			(new() { DisableAllFilters = false, FilterNamesToDisable = [] }, [1]),
			(new() { DisableAllFilters = true, FilterNamesToDisable = null }, [1, 2]),
			(new() { DisableAllFilters = false, FilterNamesToDisable = ["Active"] }, [1, 2]),
			(new() { DisableAllFilters = true, FilterNamesToDisable = ["Active"] }, [1, 2])
		];

		foreach ((GlobalFilterOptions? options, int[] expectedIds) in cases)
		{
			IQueryable<FilteredCoverageEntity> filtered = (IQueryable<FilteredCoverageEntity>)method.Invoke(
				null, [filteredContext.Entities, options])!;
			(await filtered.Select(entity => entity.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken))
				.Order()
				.ShouldBe(expectedIds);
		}
	}

	[Fact]
	public async Task Create_AddTrackingFailure_IsContained()
	{
		IServiceProvider provider = CreateThrowingTrackingProvider();
		await using AsyncServiceScope scope = provider.CreateAsyncScope();
		BaseDbContextActions<CoverageEntity, ThrowOnTrackingContext> actions = new(scope.ServiceProvider);

		await Should.NotThrowAsync(() => actions.Create(new CoverageEntity { Id = 1, Name = "one" }));
		await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
		(await verificationScope.ServiceProvider.GetRequiredService<ThrowOnTrackingContext>().Entities.ToListAsync(cancellationToken: TestContext.Current.CancellationToken))
			.ShouldBeEmpty();
	}

	[Fact]
	public async Task CreateMany_AddTrackingFailure_IsContained()
	{
		IServiceProvider provider = CreateThrowingTrackingProvider();
		await using AsyncServiceScope scope = provider.CreateAsyncScope();
		BaseDbContextActions<CoverageEntity, ThrowOnTrackingContext> actions = new(scope.ServiceProvider);

		await Should.NotThrowAsync(() => actions.CreateMany(
		[
			new CoverageEntity { Id = 1, Name = "one" },
			new CoverageEntity { Id = 2, Name = "two" }
		]));
		await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
		(await verificationScope.ServiceProvider.GetRequiredService<ThrowOnTrackingContext>().Entities.ToListAsync(cancellationToken: TestContext.Current.CancellationToken))
			.ShouldBeEmpty();
	}

	[Fact]
	public void UpdateMany_TrackingFailure_ReturnsFalse()
	{
		using IServiceScope scope = CreateThrowingTrackingProvider().CreateScope();
		BaseDbContextActions<CoverageEntity, ThrowOnTrackingContext> actions = new(scope.ServiceProvider);

		bool result = actions.UpdateMany([new CoverageEntity { Id = 1, Name = "one" }], cancellationToken: TestContext.Current.CancellationToken);

		result.ShouldBeFalse();
	}

	[Fact]
	public async Task UpdateMany_ExecuteUpdateConstraintFailure_ReturnsNull()
	{
		await using SqliteConnection connection = new("Data Source=:memory:");
		await connection.OpenAsync(TestContext.Current.CancellationToken);
		ServiceCollection services = new();
		services.AddDbContext<UniqueCoverageContext>(options => options.UseSqlite(connection));
		await using ServiceProvider provider = services.BuildServiceProvider();
		await using AsyncServiceScope scope = provider.CreateAsyncScope();
		UniqueCoverageContext context = scope.ServiceProvider.GetRequiredService<UniqueCoverageContext>();
		await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
		context.Entities.AddRange(
			new CoverageEntity { Id = 1, Name = "unique-a" },
			new CoverageEntity { Id = 2, Name = "unique-b" });
		await context.SaveChangesAsync(TestContext.Current.CancellationToken);
		BaseDbContextActions<CoverageEntity, UniqueCoverageContext> actions = new(scope.ServiceProvider);

		int? result = await actions.UpdateMany(entity => entity.Id == 2, setters => setters.SetProperty(entity => entity.Name, "unique-a"), cancellationToken: TestContext.Current.CancellationToken);

		result.ShouldBeNull();
		await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
		UniqueCoverageContext verificationContext = verificationScope.ServiceProvider.GetRequiredService<UniqueCoverageContext>();
		(await verificationContext.Entities.OrderBy(x => x.Id).Select(x => x.Name).ToListAsync(cancellationToken: TestContext.Current.CancellationToken))
			.ShouldBe(["unique-a", "unique-b"]);
	}

	[Fact]
	public async Task SaveChanges_DbUpdateFailure_ReturnsFalse()
	{
		ServiceCollection services = new();
		services.AddDbContext<ThrowOnSaveContext>(options =>
			options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
		await using ServiceProvider provider = services.BuildServiceProvider();
		BaseDbContextActions<CoverageEntity, ThrowOnSaveContext> actions = new(provider);

		bool result = await actions.SaveChanges();

		result.ShouldBeFalse();
	}

	[Fact]
	public async Task UpdateMany_InterceptorDbUpdateFailure_ReturnsNull()
	{
		await using SqliteConnection connection = new("Data Source=:memory:");
		await connection.OpenAsync(TestContext.Current.CancellationToken);
		DbContextOptions<InterceptedCoverageContext> seedOptions =
			new DbContextOptionsBuilder<InterceptedCoverageContext>().UseSqlite(connection).Options;
		await using (InterceptedCoverageContext seed = new(seedOptions))
		{
			await seed.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
			seed.Entities.Add(new CoverageEntity { Id = 1, Name = "original" });
			await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
		}

		DbContextOptions<InterceptedCoverageContext> interceptedOptions =
			new DbContextOptionsBuilder<InterceptedCoverageContext>()
				.UseSqlite(connection)
				.AddInterceptors(new ThrowingDbCommandInterceptor())
				.Options;
		ServiceCollection services = new();
		services.AddScoped(_ => new InterceptedCoverageContext(interceptedOptions));
		await using ServiceProvider provider = services.BuildServiceProvider();
		BaseDbContextActions<CoverageEntity, InterceptedCoverageContext> actions = new(provider);

		int? result = await actions.UpdateMany(entity => entity.Id == 1, setters => setters.SetProperty(entity => entity.Name, "changed"), cancellationToken: TestContext.Current.CancellationToken);

		result.ShouldBeNull();
		await using InterceptedCoverageContext verification = new(seedOptions);
		(await verification.Entities.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Name.ShouldBe("original");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GetByKeyFull_PropertyReadFailure_ReturnsInitiallyLoadedEntity(bool useArrayOverload)
	{
		string databaseName = Guid.NewGuid().ToString();
		ServiceCollection services = new();
		services.AddDbContextPool<ThrowingPropertyContext>(builder => builder.UseInMemoryDatabase(databaseName));
		await using ServiceProvider provider = services.BuildServiceProvider();
		ThrowingPropertyContext context = provider.GetRequiredService<ThrowingPropertyContext>();
		ThrowingPropertyEntity entity = new() { Id = 7, Name = "loaded" };
		context.Entities.Add(entity);
		await context.SaveChangesAsync(TestContext.Current.CancellationToken);
		context.ChangeTracker.Clear();
		BaseDbContextActions<ThrowingPropertyEntity, ThrowingPropertyContext> actions = new(provider);

		ThrowingPropertyEntity? result = useArrayOverload
			? await actions.GetByKeyFull([7], cancellationToken: TestContext.Current.CancellationToken) : await actions.GetByKeyFull(7, cancellationToken: TestContext.Current.CancellationToken);

		result.ShouldNotBeNull();
		result.Id.ShouldBe(7);
		result.Name.ShouldBe("loaded");
	}

}

internal static class ReflectionTestExtensions
{
	public static T? InvokeStatic<T>(this Type type, string name, params object?[] args)
	{
		MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
		return (T?)method.Invoke(null, args);
	}
}

public sealed class CoverageEntity
{
	public int Id { get; set; }

	public string Name { get; set; } = string.Empty;
}

public sealed class CoverageDateEntity
{
	public int Id { get; set; }

	public DateTime CreatedDate { get; set; }
}

public sealed class UnmappedCoverageEntity
{
	public int Id { get; set; }
}

public sealed class FilteredCoverageEntity
{
	public int Id { get; set; }

	public bool IsActive { get; set; }
}

public sealed class CoverageContext(DbContextOptions<CoverageContext> options) : DbContext(options)
{
	public DbSet<CoverageEntity> Entities => Set<CoverageEntity>();
}

public sealed class FilteredCoverageContext(DbContextOptions<FilteredCoverageContext> options) : DbContext(options)
{
	public DbSet<FilteredCoverageEntity> Entities => Set<FilteredCoverageEntity>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<FilteredCoverageEntity>().HasQueryFilter("Active", entity => entity.IsActive);
	}
}

public sealed class ThrowOnTrackingContext : DbContext
{
	public ThrowOnTrackingContext(DbContextOptions<ThrowOnTrackingContext> options) : base(options)
	{
		ChangeTracker.Tracking += (_, _) => throw new DbUpdateException("forced tracking failure");
	}

	public DbSet<CoverageEntity> Entities => Set<CoverageEntity>();
}

public sealed class ThrowOnSaveContext(DbContextOptions<ThrowOnSaveContext> options) : DbContext(options)
{
	public DbSet<CoverageEntity> Entities => Set<CoverageEntity>();

	public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
		Task.FromException<int>(new DbUpdateException("forced save failure"));
}

public sealed class UniqueCoverageContext(DbContextOptions<UniqueCoverageContext> options) : DbContext(options)
{
	public DbSet<CoverageEntity> Entities => Set<CoverageEntity>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<CoverageEntity>().HasIndex(entity => entity.Name).IsUnique();
	}
}

public sealed class InterceptedCoverageContext(DbContextOptions<InterceptedCoverageContext> options) : DbContext(options)
{
	public DbSet<CoverageEntity> Entities => Set<CoverageEntity>();
}

public sealed class ThrowingPropertyContext(DbContextOptions<ThrowingPropertyContext> options) : DbContext(options)
{
	public DbSet<ThrowingPropertyEntity> Entities => Set<ThrowingPropertyEntity>();
}

public sealed class ThrowingPropertyEntity
{
	public int Id { get; set; }

	public string Name { get; set; } = string.Empty;

	[NotMapped]
	public string Explosive => throw new InvalidOperationException("forced full-query projection failure");
}

internal sealed class ThrowingDbCommandInterceptor : DbCommandInterceptor
{
	public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
		DbCommand command,
		CommandEventData eventData,
		InterceptionResult<int> result,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<InterceptionResult<int>>(new DbUpdateException("forced interceptor failure"));
}

public sealed class NavigationCoverageContext(DbContextOptions<NavigationCoverageContext> options) : DbContext(options)
{
	public DbSet<NavigationCoverageRoot> Roots => Set<NavigationCoverageRoot>();
	public DbSet<NavigationCoverageChild> Children => Set<NavigationCoverageChild>();
}

public sealed class NavigationCoverageRoot
{
	public int Id { get; set; }

	public NavigationCoverageChild? Included { get; set; }

	[CoverageIgnore]
	public NavigationCoverageChild? Ignored { get; set; }
}

public sealed class NavigationCoverageChild
{
	public int Id { get; set; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class CoverageIgnoreAttribute : Attribute;

internal sealed class RecordingProvider : ILoggerProvider
{
	public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

	public void Dispose()
	{
	}
}

internal sealed class NullLogger : ILogger
{
	public static NullLogger Instance { get; } = new();

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
	}
}
