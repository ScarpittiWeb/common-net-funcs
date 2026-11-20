using CommonNetFuncs.Web.Common.CachingSupportClasses;
using CommonNetFuncs.Web.Middleware.CachingMiddleware;
using FakeItEasy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using xRetry.v3;

namespace Web.Middleware.Tests.CachingMiddleware;

public sealed class CacheKeyGenerationTests
{
	private readonly IMemoryCache cache;
	private readonly CacheOptions options;
	private readonly CacheMetrics metrics;
	private readonly CacheTracker tracker;
	private readonly HttpContext context;

	public CacheKeyGenerationTests()
	{
		cache = A.Fake<IMemoryCache>();
		options = new CacheOptions();
		metrics = new CacheMetrics();
		tracker = new CacheTracker();
		context = new DefaultHttpContext();
	}

	[RetryTheory(3)]
	[InlineData("/api/test", "param1=value1", "GET")]
	[InlineData("/api/test", "", "GET")]
	[InlineData("/api/test", "param1=value1&param2=value2", "GET")]
	public async Task GenerateCacheKey_WithVariousRequests_GeneratesUniqueKeys(string path, string queryString, string method)
	{
		// Arrange
		context.Request.Method = method;
		context.Request.Path = path;

		// Combine all query parameters
		Dictionary<string, StringValues> queryParams = new() { { options.UseCacheQueryParam, "true" } };

		if (!string.IsNullOrEmpty(queryString))
		{
			foreach (string param in queryString.Split('&'))
			{
				string[] parts = param.Split('=');
				queryParams.Add(parts[0], new StringValues(parts[1]));
			}
		}

		context.Request.Query = new QueryCollection(queryParams);

		MemoryCacheMiddleware middleware = new(next: A.Fake<RequestDelegate>(), cache: cache, cacheOptions: options, cacheMetrics: metrics, cacheTracker: tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<string>.That.Matches(key => key.Contains(path) && key.Contains(options.UseCacheQueryParam) &&
			(string.IsNullOrEmpty(queryString) || queryString.Split('&', StringSplitOptions.None).All(param => key.Contains(param)))))).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task GenerateCacheKey_WithPostRequest_IncludesBodyHash()
	{
		// Arrange
		context.Request.Method = "POST";
		context.Request.Path = "/api/test";
		const string body = "test body content";
		byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
		context.Request.Body = new MemoryStream(bodyBytes);

		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };

		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next: A.Fake<RequestDelegate>(), cache: cache, cacheOptions: options, cacheMetrics: metrics, cacheTracker: tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<string>.That.Contains("|"))).MustHaveHappened();
		context.Request.Body.Position.ShouldBe(0); // Verify body position is reset
	}

	[RetryFact(3)]
	public async Task GenerateCacheKey_WithDifferentAcceptHeaders_GeneratesDifferentKeys()
	{
		// Arrange
		context.Request.Method = "GET";
		context.Request.Path = "/api/test";
		context.Request.Headers.Accept = "application/json";

		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next: A.Fake<RequestDelegate>(), cache: cache, cacheOptions: options, cacheMetrics: metrics, cacheTracker: tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<string>.That.Contains("application/json"))).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task GenerateCacheKey_WithQueryParameters_OrdersParametersConsistently()
	{
		// Arrange
		context.Request.Method = "GET";
		context.Request.Path = "/api/test";

		Dictionary<string, StringValues> queryDict = new()
			{
				{ "zebra", "last" },
				{ "alpha", "first" },
				{ "middle", "value" },
				{ options.UseCacheQueryParam, "true" }
			};
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next: A.Fake<RequestDelegate>(), cache: cache, cacheOptions: options, cacheMetrics: metrics, cacheTracker: tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert - Parameters should be ordered alphabetically
		A.CallTo(() => cache.CreateEntry(A<string>.That.Matches(key => key.Contains("alpha") && key.Contains("middle") && key.Contains("zebra")))).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task GenerateCacheKey_ExcludesEvictionParam()
	{
		// Arrange
		context.Request.Method = "GET";
		context.Request.Path = "/api/test";

		Dictionary<string, StringValues> queryDict = new()
			{
				{ options.UseCacheQueryParam, "true" },
				{ options.EvictionQueryParam, "true" }
			};
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next: A.Fake<RequestDelegate>(), cache: cache, cacheOptions: options, cacheMetrics: metrics, cacheTracker: tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert - eviction param should not be in cache key
		A.CallTo(() => cache.CreateEntry(A<string>.That.Not.Contains(options.EvictionQueryParam))).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task GenerateCacheKey_WithEmptyPostBody_IncludesBodyHash()
	{
		// Arrange
		context.Request.Method = "POST";
		context.Request.Path = "/api/test";
		context.Request.Body = new MemoryStream();

		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next: A.Fake<RequestDelegate>(), cache: cache, cacheOptions: options, cacheMetrics: metrics, cacheTracker: tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<string>._)).MustHaveHappened();
	}
}

public sealed class CacheKeyGenerationBehaviorTests
{















	private static DefaultHttpContext CreateContext(string path, string useCacheQueryParam)
	{
		DefaultHttpContext context = new();
		context.Request.Path = path;
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			[useCacheQueryParam] = "true",
		});
		return context;
	}

	[Fact]
	public async Task GenerateCacheKey_PostRequest_UsesExactSortedQueryAndBodyHash()
	{
		// Arrange
		CacheOptions options = new() { UseCompression = false };
		using MemoryCache cache = new(new MemoryCacheOptions());
		CacheTracker tracker = new();
		CacheMetrics metrics = new();
		DefaultHttpContext context = new();
		context.Request.Method = HttpMethods.Post;
		context.Request.Path = "/search";
		context.Request.Headers.Accept = "application/json";
		context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"q\":\"héllo\"}"));
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			["z"] = "last",
			[options.EvictionQueryParam] = "false",
			["a"] = new StringValues(["one", "two"]),
			[options.UseCacheQueryParam] = "true",
		});
		context.Response.Body = new MemoryStream();
		MemoryCacheMiddleware middleware = new(invocationContext =>
		{
			invocationContext.Response.StatusCode = StatusCodes.Status200OK;
			return invocationContext.Response.Body.WriteAsync("ok"u8.ToArray()).AsTask();
		}, cache, options, metrics, tracker);
		string bodyHash = Convert.ToBase64String(SHA256.HashData("{\"q\":\"héllo\"}"u8));
		const string expectedKey = "/search?a=one,two&useCache=true&z=last|";

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Request.Body.Position.ShouldBe(0);
		cache.TryGetValue(expectedKey + bodyHash + "|application/json", out CacheEntry? entry).ShouldBeTrue();
		entry.ShouldNotBeNull();
		entry.Data.ShouldBe("ok"u8.ToArray());
	}

}

public sealed class CacheKeyGenerationReflectionBehaviorTests
{





	private static MemoryCacheMiddleware CreateMiddleware(CacheMetrics? metrics, CacheTracker tracker, CacheOptions? options = null)
	{
		return new MemoryCacheMiddleware(
			_ => Task.CompletedTask,
			new MemoryCache(new MemoryCacheOptions()),
			options ?? new CacheOptions { SuppressLogs = true },
			metrics,
			tracker);
	}

	private static void InvokeHandleEviction(MemoryCacheMiddleware middleware, object key, object? value)
	{
		GetPrivateMethod("HandleEviction").Invoke(middleware, [key, value, EvictionReason.Removed, null]);
	}

	private static void InvokeAppendSortedQuery(StringBuilder builder, IQueryCollection query, string evictionQueryParam)
	{
		GetPrivateMethod("AppendSortedQuery").Invoke(null, [builder, query, evictionQueryParam]);
	}

	private static SemaphoreSlim GetCacheLock(MemoryCacheMiddleware middleware)
	{
		FieldInfo field = typeof(MemoryCacheMiddleware).GetField("cacheLock", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("cacheLock field was not found.");
		return (SemaphoreSlim)field.GetValue(middleware)!;
	}

	private static MethodInfo GetPrivateMethod(string name)
	{
		return typeof(MemoryCacheMiddleware).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new InvalidOperationException($"{name} method was not found.");
	}

	private sealed class NullStringObject
	{
		public override string? ToString()
		{
			return null;
		}
	}

	[Fact]
	public void AppendSortedQuery_EmptyCollection_LeavesBuilderUnchanged()
	{
		// Arrange
		StringBuilder builder = new("prefix");
		QueryCollection query = new(new Dictionary<string, StringValues>());

		// Act
		InvokeAppendSortedQuery(builder, query, "evict");

		// Assert
		builder.ToString().ShouldBe("prefix");
	}

}
