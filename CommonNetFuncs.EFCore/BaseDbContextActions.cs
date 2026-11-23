using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommonNetFuncs.EFCore;

/// <summary>
/// Common EF Core interactions with a database. Must be using dependency injection for this class to work.
/// </summary>
/// <typeparam name="TEntity">Entity <see langword="class"/> to be used with these methods.</typeparam>
/// <typeparam name="TContext">DB Context for the database you with to run these actions against.</typeparam>
/// <param name="serviceProvider"><see cref="IServiceProvider"/> for dependency injection.</param>
public partial class BaseDbContextActions<TEntity, TContext>(IServiceProvider serviceProvider) : IBaseDbContextActions<TEntity, TContext> where TEntity : class where TContext : DbContext
{
	private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
	private static readonly JsonSerializerOptions defaultJsonSerializerOptions = new() { ReferenceHandler = ReferenceHandler.IgnoreCycles };
	static readonly ConcurrentDictionary<Type, bool> circularReferencingEntities = new();

	private static readonly ConcurrentDictionary<Type, EntityKeyMetadata> entityKeyCache = new();
	private static readonly ConcurrentDictionary<Type, Func<object, Expression<Func<TEntity, bool>>>> singleKeyExpressionBuilder = new();
	private static readonly ConcurrentDictionary<Type, Func<object[], Expression<Func<TEntity, bool>>>> compositeKeyExpressionBuilder = new();

	private const string Error1LocationTemplate = "{ExceptionLocation} Error1";
	private const string Error2LocationTemplate = "{ExceptionLocation} Error2";
	private const string AddCircularRefTemplate = "Adding {Type} to circularReferencingEntities";

	/// <summary>
	/// Gets (or creates and caches) the function that builds the single-field primary key equality expression for <typeparamref name="TEntity"/>.
	/// The key value is embedded in the resulting expression as a constant.
	/// </summary>
	/// <param name="context">Context used to read the entity's primary key metadata on first use.</param>
	private static Func<object, Expression<Func<TEntity, bool>>> GetSingleKeyExpressionBuilder(DbContext context)
	{
		// Static factory + context argument: no closure / delegate allocation per call
		return singleKeyExpressionBuilder.GetOrAdd(typeof(TEntity), static (type, ctx) => CreateSingleKeyExpressionBuilder(type, ctx), context);
	}

	private static Func<object, Expression<Func<TEntity, bool>>> CreateSingleKeyExpressionBuilder(Type type, DbContext context)
	{
		// Get cached entity metadata or retrieve and cache it
		EntityKeyMetadata keyMetadata = entityKeyCache.GetOrAdd(type, static (t, ctx) =>
		{
			IEntityType? entityType = ctx.Model.FindEntityType(t);
			IKey? primaryKey_Property = entityType?.FindPrimaryKey();

			if (primaryKey_Property == null || primaryKey_Property.Properties.Count != 1)
			{
				throw new InvalidOperationException($"Entity {t.Name} does not have a single-field primary key. Use the array overload instead.");
			}

			return new EntityKeyMetadata
			{
				KeyPropertyName = primaryKey_Property.Properties[0].Name,
				KeyPropertyType = primaryKey_Property.Properties[0].ClrType
			};
		}, context);

		Type keyType = keyMetadata.KeyPropertyType!;
		// Resolved once through the same Expression.Property(Expression, string) lookup that was previously performed on every call
		PropertyInfo keyProperty = (PropertyInfo)Expression.Property(Expression.Parameter(typeof(TEntity), "x"), keyMetadata.KeyPropertyName!).Member;

		// Return a function that builds an expression tree with the actual key value
		// This function is cached, but the expression it creates includes the specific key value
		return keyValue =>
		{
			ParameterExpression parameter = Expression.Parameter(typeof(TEntity), "x");
			BinaryExpression equality = Expression.Equal(Expression.Property(parameter, keyProperty), Expression.Constant(keyValue, keyType));
			return Expression.Lambda<Func<TEntity, bool>>(equality, parameter);
		};
	}

}

/// <summary>
/// Optional configurations for the <see cref="BaseDbContextActions"/> class
/// </summary>
public sealed class FullQueryOptions(bool? splitQueryOverride = null) : NavigationPropertiesOptions
{
	/// <summary>
	/// Optional: Override the database default split query behavior. Only used when running "Full" queries that include navigation properties.
	/// </summary>
	public bool? SplitQueryOverride { get; set; } = splitQueryOverride;
}

public sealed class GlobalFilterOptions
{
	/// <summary>
	/// Optional: If <see langword="true"/>, will disable all global filters for this query.
	/// </summary>
	/// <remarks>If <see langword="false"/>, no filters are disabled unless specified in <see cref="FilterNamesToDisable"/>. <see cref="FilterNamesToDisable"/> having a value will make the code ignore this value.</remarks>
	public bool DisableAllFilters { get; set; }

	/// <summary>
	/// Optional: Used to specify the names of filters that should be excluded from execution.
	/// </summary>
	/// <remarks>If not empty, overwrites <see cref="DisableAllFilters"/>. If <see langword="null"/> or empty, no filters are disabled or all filters are disabled if  <see cref="DisableAllFilters"/> is <see langword="true"/>.</remarks>
	public string[]? FilterNamesToDisable { get; set; }
}

public sealed class GenericPagingModel<T>// where TObj : class
{
	public GenericPagingModel()
	{
		Entities = [];
	}

	public List<T> Entities { get; set; }

	public int TotalRecords { get; set; }
}

/// <summary>
/// Cached metadata for entity primary keys to avoid repeated reflection and EF Core model lookups
/// </summary>
internal sealed class EntityKeyMetadata
{
	/// <summary>
	/// The name of the single primary key property (for single-key entities)
	/// </summary>
	public string? KeyPropertyName { get; set; }

	/// <summary>
	/// The CLR type of the single primary key property
	/// </summary>
	public Type? KeyPropertyType { get; set; }

	/// <summary>
	/// Array of property names for compound primary keys
	/// </summary>
	public string[]? CompositeKeyPropertyNames { get; set; }

	/// <summary>
	/// Array of CLR types for compound primary key properties
	/// </summary>
	public Type[]? CompositeKeyPropertyTypes { get; set; }
}
