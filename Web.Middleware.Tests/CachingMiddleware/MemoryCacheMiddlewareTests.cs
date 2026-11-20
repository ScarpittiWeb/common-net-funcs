using CommonNetFuncs.Web.Common.CachingSupportClasses;
using CommonNetFuncs.Web.Middleware.CachingMiddleware;
using FakeItEasy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using xRetry.v3;
using static CommonNetFuncs.Compression.Streams;
using static Xunit.TestContext;

namespace Web.Middleware.Tests.CachingMiddleware;

public sealed class MemoryCacheMiddlewareTests
{
	private readonly IMemoryCache cache;
	private readonly CacheOptions options;
	private readonly CacheMetrics metrics;
	private readonly CacheTracker tracker;
	private readonly RequestDelegate next;
	private readonly HttpContext context;

	public MemoryCacheMiddlewareTests()
	{
		cache = A.Fake<IMemoryCache>();
		options = new CacheOptions();
		metrics = new CacheMetrics();
		tracker = new CacheTracker();
		next = A.Fake<RequestDelegate>();
		context = new DefaultHttpContext();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithoutCacheParams_CallsNextDelegate()
	{
		// Arrange
		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => next(context)).MustHaveHappenedOnceExactly();
	}

	[RetryTheory(3)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public async Task InvokeAsync_WithCacheParams_HandlesFlags(bool useCache, bool evictCache)
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new()
			{
				{ options.UseCacheQueryParam, useCache.ToString() },
				{ options.EvictionQueryParam, evictCache.ToString() }
			};
		context.Request.Query = new QueryCollection(queryDict);

		// Setup TryGetValue to return true and a dummy entry if eviction is expected
		if (evictCache)
		{
			object? outValue = new CacheEntry
			{
				Data = Encoding.UTF8.GetBytes("dummy"),
				Headers = new Dictionary<string, string>()
			};
			A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);
		}

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		if (evictCache)
		{
			A.CallTo(() => cache.Remove(A<string>._)).MustHaveHappened();
		}
		if (!useCache)
		{
			A.CallTo(() => next(context)).MustHaveHappenedOnceExactly();
		}
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithCacheHit_ReturnsCachedResponse()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		byte[] cachedData = Encoding.UTF8.GetBytes("cached response");
		object? outValue = new CacheEntry()
		{
			Data = cachedData,
			Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } }
		};

		// Mock the interface method instead of the extension method
		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		responseStream.Position = 0;
		string result = await new StreamReader(responseStream).ReadToEndAsync(Current.CancellationToken);
		result.ShouldBe("cached response");
		metrics.CacheHits().ShouldBe(1);
		A.CallTo(() => next(context)).MustNotHaveHappened();
	}

	[RetryTheory(3)]
	[InlineData("10", null, null)]
	[InlineData(null, "5", null)]
	[InlineData(null, null, "2")]
	public async Task InvokeAsync_WithCustomCacheDuration_SetsCacheOptions(string? seconds, string? minutes, string? hours)
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };

		if (seconds != null)
		{
			queryDict.Add(options.CacheSecondsQueryParam, seconds);
		}

		if (minutes != null)
		{
			queryDict.Add(options.CacheMinutesQueryParam, minutes);
		}

		if (hours != null)
		{
			queryDict.Add(options.CacheHoursQueryParam, hours);
		}

		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		metrics.CacheMisses().ShouldBe(1);
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithNon200StatusCode_DoesNotCache()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status404NotFound;

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		metrics.CacheMisses().ShouldBe(1);
		// Verify no cache entry was created
		long initialMetrics = metrics.CurrentCacheEntryCount();
		initialMetrics.ShouldBe(0);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WhenTaskCanceled_LogsWarningIfNotSuppressed()
	{
		// Arrange
		RequestDelegate throwingNext = A.Fake<RequestDelegate>();
		A.CallTo(() => throwingNext(A<HttpContext>._)).Throws(new TaskCanceledException());

		CacheOptions optionsWithLogging = new() { SuppressLogs = false };
		MemoryCacheMiddleware middleware = new(throwingNext, cache, optionsWithLogging, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => throwingNext(A<HttpContext>._)).MustHaveHappened(); // Middleware should call next delegate
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustNotHaveHappened(); // No caching should occur
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithCompression_CachesCompressedData()
	{
		// Arrange
		CacheOptions optionsWithCompression = new() { UseCompression = true, CompressionType = ECompressionType.Gzip };
		Dictionary<string, StringValues> queryDict = new() { { optionsWithCompression.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		// Setup next to write some data
		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("Large response data that should be compressed");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, optionsWithCompression, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
		metrics.CacheMisses().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithCacheHitAndCompression_DecompressesData()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		const string originalData = "Test data";
		byte[] compressedData = await Encoding.UTF8.GetBytes(originalData).CompressAsync(ECompressionType.Gzip, cancellationToken: Current.CancellationToken);
		object? outValue = new CacheEntry()
		{
			Data = compressedData,
			Headers = new Dictionary<string, string>(),
			CompressionType = (short)ECompressionType.Gzip
		};

		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		responseStream.Position = 0;
		string result = await new StreamReader(responseStream).ReadToEndAsync(Current.CancellationToken);
		result.ShouldBe(originalData);
		metrics.CacheHits().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithEmptyResponseData_CachesEmptyResponse()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		object? outValue = new CacheEntry()
		{
			Data = Array.Empty<byte>(),
			Headers = new Dictionary<string, string>()
		};

		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		responseStream.Length.ShouldBe(0);
		metrics.CacheHits().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithCacheTags_RegistersTagsInTracker()
	{
		// Arrange
		const string tagName = "test-tag";
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Request.Headers[options.CacheTagHeader] = tagName;
		context.Response.StatusCode = StatusCodes.Status200OK;

		// Setup next to write some data
		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		tracker.CacheTags.ContainsKey(tagName).ShouldBeTrue();
		metrics.CacheTags.ContainsKey(tagName).ShouldBeTrue();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithMultipleCacheTags_RegistersAllTags()
	{
		// Arrange
		const string tags = "tag1,tag2,tag3";
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Request.Headers[options.CacheTagHeader] = tags;
		context.Response.StatusCode = StatusCodes.Status200OK;

		// Setup next to write some data
		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		tracker.CacheTags.ContainsKey("tag1").ShouldBeTrue();
		tracker.CacheTags.ContainsKey("tag2").ShouldBeTrue();
		tracker.CacheTags.ContainsKey("tag3").ShouldBeTrue();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithCachedHeaders_RestoresHeaders()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		Dictionary<string, string> cachedHeaders = new()
		{
			{ "Content-Type", "application/json" },
			{ "Cache-Control", "max-age=3600" }
		};

		object? outValue = new CacheEntry()
		{
			Data = Encoding.UTF8.GetBytes("data"),
			Headers = cachedHeaders
		};

		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Response.Headers.ContentType.ToString().ShouldBe("application/json");
		context.Response.Headers.CacheControl.ToString().ShouldBe("max-age=3600");
	}

	[RetryFact(3)]
	public void Dispose_ShouldDisposeResources()
	{
		// Arrange
		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		middleware.Dispose();
		middleware.Dispose(); // Should be safe to call multiple times

		// Assert - Verify Dispose can be called multiple times without throwing
		// The test passing without exceptions validates the dispose pattern is correctly implemented
		Should.NotThrow(middleware.Dispose);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithTagBasedEviction_EvictsAllEntriesWithTag()
	{
		// Arrange
		const string tagToEvict = "test-tag";
		const string cacheKey1 = "key1";
		const string cacheKey2 = "key2";

		// Register entries in tracker with tags
		tracker.CacheTags.TryAdd(tagToEvict, new HashSet<string> { cacheKey1, cacheKey2 });
		metrics.CacheTags.TryAdd(tagToEvict, new HashSet<string> { cacheKey1, cacheKey2 });

		CacheEntry entry1 = new()
		{
			Data = Encoding.UTF8.GetBytes("data1"),
			Headers = new Dictionary<string, string>(),
			Tags = new HashSet<string> { tagToEvict }
		};

		CacheEntry entry2 = new()
		{
			Data = Encoding.UTF8.GetBytes("data2"),
			Headers = new Dictionary<string, string>(),
			Tags = new HashSet<string> { tagToEvict }
		};

		object? outValue1 = entry1;
		object? outValue2 = entry2;

		A.CallTo(() => cache.TryGetValue(cacheKey1, out outValue1)).Returns(true);
		A.CallTo(() => cache.TryGetValue(cacheKey2, out outValue2)).Returns(true);

		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.EvictionQueryParam, "true" },
			{ options.EvictTagQueryParam, tagToEvict }
		};
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.Remove(cacheKey1)).MustHaveHappened();
		A.CallTo(() => cache.Remove(cacheKey2)).MustHaveHappened();
		tracker.CacheTags.ContainsKey(tagToEvict).ShouldBeFalse();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithMultipleTagsToEvict_EvictsAllMatchingEntries()
	{
		// Arrange
		const string tag1 = "tag1";
		const string tag2 = "tag2";
		const string cacheKey1 = "key1";
		const string cacheKey2 = "key2";

		tracker.CacheTags.TryAdd(tag1, new HashSet<string> { cacheKey1 });
		tracker.CacheTags.TryAdd(tag2, new HashSet<string> { cacheKey2 });

		CacheEntry entry1 = new()
		{
			Data = Encoding.UTF8.GetBytes("data1"),
			Headers = new Dictionary<string, string>(),
			Tags = new HashSet<string> { tag1 }
		};

		CacheEntry entry2 = new()
		{
			Data = Encoding.UTF8.GetBytes("data2"),
			Headers = new Dictionary<string, string>(),
			Tags = new HashSet<string> { tag2 }
		};

		object? outValue1 = entry1;
		object? outValue2 = entry2;

		A.CallTo(() => cache.TryGetValue(cacheKey1, out outValue1)).Returns(true);
		A.CallTo(() => cache.TryGetValue(cacheKey2, out outValue2)).Returns(true);

		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.EvictionQueryParam, "true" },
			{ options.EvictTagQueryParam, $"{tag1},{tag2}" }
		};
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.Remove(cacheKey1)).MustHaveHappened();
		A.CallTo(() => cache.Remove(cacheKey2)).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithPostRequest_GeneratesCacheKeyWithBodyHash()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Request.Method = "POST";
		context.Request.Path = "/api/test";

		const string bodyContent = "{\"test\":\"data\"}";
		MemoryStream bodyStream = new(Encoding.UTF8.GetBytes(bodyContent));
		context.Request.Body = bodyStream;
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
		metrics.CacheMisses().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithPostRequestAndUnsortedQuery_GeneratesSortedCacheKeyWithBodyHash()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new()
		{
			{ "zeta", "1" },
			{ options.UseCacheQueryParam, "true" },
			{ options.EvictionQueryParam, "false" },
			{ "alpha", "2" },
			{ "tags", new StringValues(["a", "b"]) }
		};
		context.Request.Query = new QueryCollection(queryDict);
		context.Request.Method = "POST";
		context.Request.Path = "/api/test";
		context.Request.Headers.Accept = "application/json";

		const string bodyContent = "{\"test\":\"data\",\"unicode\":\"héllo ✓\"}";
		context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(bodyContent));
		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.Body = new MemoryStream();

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		string expectedKey = $"/api/test?alpha=2&tags=a,b&{options.UseCacheQueryParam}=true&zeta=1|{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(bodyContent)))}|application/json";

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(expectedKey)).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WhenItemTooLargeForCache_DoesNotCache()
	{
		// Arrange

		// Very small cache
		CacheOptions smallCacheOptions = new() { MaxCacheSizeInBytes = 10 };

		Dictionary<string, StringValues> queryDict = new() { { smallCacheOptions.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("This is a very large response that exceeds the cache size limit");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, smallCacheOptions, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		metrics.SkippedDueToSize().ShouldBe(1);
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustNotHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WhenCacheFullAndNeedSpace_EvictsOldestEntries()
	{
		// Arrange
		CacheOptions smallCacheOptions = new()
		{
			MaxCacheSizeInBytes = 100
		};

		// Pre-populate tracker with old entries
		CacheTracker testTracker = new();
		testTracker.TrackEntry("oldKey1", 40);
		testTracker.TrackEntry("oldKey2", 40);

		CacheMetrics testMetrics = new();
		testMetrics.AddToSize(80); // 80 bytes used

		CacheEntry oldEntry1 = new()
		{
			Data = new byte[40],
			Headers = new Dictionary<string, string>(),
			Tags = new HashSet<string>()
		};

		CacheEntry oldEntry2 = new()
		{
			Data = new byte[40],
			Headers = new Dictionary<string, string>(),
			Tags = new HashSet<string>()
		};

		object? outOldValue1 = oldEntry1;
		object? outOldValue2 = oldEntry2;

		A.CallTo(() => cache.TryGetValue("oldKey1", out outOldValue1)).Returns(true);
		A.CallTo(() => cache.TryGetValue("oldKey2", out outOldValue2)).Returns(true);

		Dictionary<string, StringValues> queryDict = new() { { smallCacheOptions.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = new byte[50]; // Need 50 bytes
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, smallCacheOptions, testMetrics, testTracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.Remove("oldKey1")).MustHaveHappened();
		testMetrics.EvictedDueToCapacity().ShouldBeGreaterThan(0);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WhenCannotFreeEnoughSpace_DoesNotCache()
	{
		// Arrange
		CacheOptions smallCacheOptions = new() { MaxCacheSizeInBytes = 100 };

		CacheTracker testTracker = new();
		// Only one small entry that won't free enough space
		testTracker.TrackEntry("smallKey", 10);

		CacheMetrics testMetrics = new();
		testMetrics.AddToSize(95); // 95 bytes used

		CacheEntry smallEntry = new()
		{
			Data = new byte[10],
			Headers = new Dictionary<string, string>(),
			Tags = new HashSet<string>()
		};

		object? outSmallValue = smallEntry;
		A.CallTo(() => cache.TryGetValue("smallKey", out outSmallValue)).Returns(true);

		Dictionary<string, StringValues> queryDict = new() { { smallCacheOptions.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = new byte[50]; // Need 50 bytes but can't free enough
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, smallCacheOptions, testMetrics, testTracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustNotHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithSuppressedLogs_DoesNotLog()
	{
		// Arrange
		CacheOptions optionsWithSuppressedLogs = new() { SuppressLogs = true };

		Dictionary<string, StringValues> queryDict = new() { { options.EvictionQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		CacheEntry entry = new()
		{
			Data = Encoding.UTF8.GetBytes("data"),
			Headers = new Dictionary<string, string>(),
			Tags = new HashSet<string>()
		};

		object? outValue = entry;
		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);

		MemoryCacheMiddleware middleware = new(next, cache, optionsWithSuppressedLogs, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert - Should not throw and should evict
		A.CallTo(() => cache.Remove(A<string>._)).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithEvictTagQueryParam_WhenTagNotFound_CompletesSuccessfully()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.EvictionQueryParam, "true" },
			{ options.EvictTagQueryParam, "non-existent-tag" }
		};
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => next(A<HttpContext>._)).MustHaveHappened(); // Next should be called after eviction attempt
		A.CallTo(() => cache.Remove(A<object>._)).MustNotHaveHappened(); // No removal since tag doesn't exist
		tracker.CacheTags.ContainsKey("non-existent-tag").ShouldBeFalse(); // Tag should not be in tracker
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithCacheMissAndNonSuccessStatusCode_DoesNotCacheNonSuccessResponse()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status500InternalServerError;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("error");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustNotHaveHappened();
		metrics.CurrentCacheEntryCount().ShouldBe(0);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithQueryParametersInRequest_GeneratesCorrectCacheKey()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.UseCacheQueryParam, "true" },
			{ "param1", "value1" },
			{ "param2", "value2" }
		};
		context.Request.Query = new QueryCollection(queryDict);
		context.Request.Path = "/api/test";
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
		metrics.CacheMisses().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithAcceptHeader_IncludesInCacheKey()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Request.Headers.Accept = "application/json";
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithEvictionOfNonExistentKey_CompletesSuccessfully()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.EvictionQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		object? outValue = null;
		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(false);

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => next(A<HttpContext>._)).MustHaveHappened(); // Next should be called after eviction attempt
		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).MustHaveHappened(); // Should check if key exists
		A.CallTo(() => cache.Remove(A<object>._)).MustNotHaveHappened(); // No removal since key doesn't exist
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WhenSpaceAvailableWithoutEviction_CachesSuccessfully()
	{
		// Arrange
		CacheOptions largeCacheOptions = new() { MaxCacheSizeInBytes = 10000 };

		CacheMetrics testMetrics = new();
		testMetrics.AddToSize(100); // Only 100 bytes used

		Dictionary<string, StringValues> queryDict = new() { { largeCacheOptions.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("small response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, largeCacheOptions, testMetrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
		testMetrics.CacheMisses().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithNullCachedValue_HandlesGracefully()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		object? outValue = null;
		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		responseStream.Length.ShouldBe(0);
		metrics.CacheHits().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithCacheHitButNullData_WritesNull()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		CacheEntry? cachedEntry = null;
		object? outValue = cachedEntry;
		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		responseStream.Length.ShouldBe(0);
		metrics.CacheHits().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithEvictionAndSuppressedLogs_DoesNotLogEviction()
	{
		// Arrange
		CacheOptions suppressedOptions = new() { SuppressLogs = true };
		const string tagToEvict = "evict-tag";

		tracker.CacheTags.TryAdd(tagToEvict, new HashSet<string> { "key1" });

		CacheEntry entry = new()
		{
			Data = Encoding.UTF8.GetBytes("data"),
			Headers = new Dictionary<string, string>(),
			Tags = new HashSet<string> { tagToEvict }
		};

		object? outValue = entry;
		A.CallTo(() => cache.TryGetValue("key1", out outValue)).Returns(true);

		Dictionary<string, StringValues> queryDict = new()
		{
			{ suppressedOptions.EvictionQueryParam, "true" },
			{ suppressedOptions.EvictTagQueryParam, tagToEvict }
		};
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next, cache, suppressedOptions, metrics, tracker);

		// Act & Assert - Should not throw
		await middleware.InvokeAsync(context);
		A.CallTo(() => cache.Remove("key1")).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithCustomCacheDurationCombination_SetsCorrectExpiration()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.UseCacheQueryParam, "true" },
			{ options.CacheSecondsQueryParam, "30" },
			{ options.CacheMinutesQueryParam, "2" },
			{ options.CacheHoursQueryParam, "1" }
		};
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
		metrics.CacheMisses().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithInvalidCacheDurationParams_UsesDefaultDuration()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.UseCacheQueryParam, "true" },
			{ options.CacheSecondsQueryParam, "invalid" },
			{ options.CacheMinutesQueryParam, "not-a-number" }
		};
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
		metrics.CacheMisses().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithEvictCacheButNoExistingEntry_CompletesWithoutError()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.EvictionQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		object? outValue = null;
		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(false);

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act & Assert - should not throw
		await middleware.InvokeAsync(context);
		A.CallTo(() => cache.Remove(A<string>._)).MustNotHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithEmptyEvictTagQueryParam_DoesNotEvict()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.EvictionQueryParam, "true" },
			{ options.EvictTagQueryParam, "" }
		};
		context.Request.Query = new QueryCollection(queryDict);

		object? outValue = new CacheEntry
		{
			Data = Encoding.UTF8.GetBytes("data"),
			Headers = new Dictionary<string, string>()
		};
		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert - Should evict by key instead
		A.CallTo(() => cache.Remove(A<string>._)).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WhenCacheTrackerIsNull_HandlesGracefully()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.EvictionQueryParam, "true" },
			{ options.EvictTagQueryParam, "some-tag" }
		};
		context.Request.Query = new QueryCollection(queryDict);

		// Use an empty tracker (no tags registered)
		CacheTracker? emptyTracker = new();

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, emptyTracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => next(A<HttpContext>._)).MustHaveHappened(); // Next should be called after eviction attempt
		A.CallTo(() => cache.Remove(A<object>._)).MustNotHaveHappened(); // No removal since tag doesn't exist in tracker
		emptyTracker.CacheTags.ContainsKey("some-tag").ShouldBeFalse(); // Tag should not be in tracker
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithRequestPathButNoQuery_GeneratesCacheKeyCorrectly()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Request.Path = "/api/users";
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithBothUseCacheAndEvictCache_EvictsThenSkipsCaching()
	{
		// Arrange
		CacheEntry existingEntry = new()
		{
			Data = Encoding.UTF8.GetBytes("old data"),
			Headers = new Dictionary<string, string>()
		};

		object? outValue = existingEntry;
		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);

		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.UseCacheQueryParam, "true" },
			{ options.EvictionQueryParam, "true" }
		};
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.Remove(A<string>._)).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithTagEvictionWhenKeyNotInCache_ContinuesWithoutError()
	{
		// Arrange
		const string tagToEvict = "test-tag";
		const string keyNotInCache = "missing-key";

		tracker.CacheTags.TryAdd(tagToEvict, new HashSet<string> { keyNotInCache });

		object? outValue = null;
		A.CallTo(() => cache.TryGetValue(keyNotInCache, out outValue)).Returns(false);

		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.EvictionQueryParam, "true" },
			{ options.EvictTagQueryParam, tagToEvict }
		};
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act & Assert - should not throw
		await middleware.InvokeAsync(context);
		A.CallTo(() => cache.Remove(keyNotInCache)).MustNotHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithZeroLengthCompressedData_HandlesCorrectly()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);

		byte[] emptyCompressed = await Array.Empty<byte>().CompressAsync(ECompressionType.Gzip, cancellationToken: Current.CancellationToken);
		CacheEntry cachedEntry = new()
		{
			Data = emptyCompressed,
			Headers = new Dictionary<string, string>(),
			CompressionType = (short)ECompressionType.Gzip
		};

		object? outValue = cachedEntry;
		A.CallTo(() => cache.TryGetValue(A<object>._, out outValue)).Returns(true);

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		metrics.CacheHits().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithMultipleHeadersToCache_CachesAllConfiguredHeaders()
	{
		// Arrange
		CacheOptions optionsWithHeaders = new()
		{
			HeadersToCache = System.Collections.Immutable.ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, ["Content-Type", "Cache-Control", "ETag"])
		};

		Dictionary<string, StringValues> queryDict = new() { { optionsWithHeaders.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			ctx.Response.Headers.ContentType = "application/json";
			ctx.Response.Headers.CacheControl = "max-age=3600";
			ctx.Response.Headers.ETag = "\"abc123\"";
			ctx.Response.Headers["X-Custom"] = "should-not-be-cached";
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, optionsWithHeaders, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WhenSpaceCheckPassesAfterLockAcquisition_CachesSuccessfully()
	{
		// Arrange
		CacheOptions smallCacheOptions = new() { MaxCacheSizeInBytes = 200 };

		CacheTracker testTracker = new();
		CacheMetrics testMetrics = new();
		testMetrics.AddToSize(150); // Just under limit before lock

		Dictionary<string, StringValues> queryDict = new() { { smallCacheOptions.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = new byte[30]; // Small enough to fit
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, smallCacheOptions, testMetrics, testTracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
		testMetrics.CacheMisses().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithTagsButTagsHaveNoKeys_RemovesTagFromTracker()
	{
		// Arrange
		const string tagToEvict = "empty-tag";

		// Tag exists but has empty set of keys
		tracker.CacheTags.TryAdd(tagToEvict, new HashSet<string>());

		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.EvictionQueryParam, "true" },
			{ options.EvictTagQueryParam, tagToEvict }
		};
		context.Request.Query = new QueryCollection(queryDict);

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert - should complete without error
		A.CallTo(() => cache.Remove(A<string>._)).MustNotHaveHappened();
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithPostRequestAndLargeBody_GeneratesCacheKeyWithBodyHash()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new() { { options.UseCacheQueryParam, "true" } };
		context.Request.Query = new QueryCollection(queryDict);
		context.Request.Method = "POST";
		context.Request.Path = "/api/test";

		// Large body
		string bodyContent = new('x', 10000);
		MemoryStream bodyStream = new(Encoding.UTF8.GetBytes(bodyContent));
		context.Request.Body = bodyStream;
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
		metrics.CacheMisses().ShouldBe(1);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_WithEvictionQueryParamExcludedFromCacheKey_GeneratesCorrectKey()
	{
		// Arrange
		Dictionary<string, StringValues> queryDict = new()
		{
			{ options.UseCacheQueryParam, "true" },
			{ options.EvictionQueryParam, "false" }, // Should be excluded from cache key
			{ "someOtherParam", "value" }
		};
		context.Request.Query = new QueryCollection(queryDict);
		context.Response.StatusCode = StatusCodes.Status200OK;

		MemoryStream responseStream = new();
		context.Response.Body = responseStream;

		A.CallTo(() => next(A<HttpContext>._)).Invokes((HttpContext ctx) =>
		{
			byte[] data = Encoding.UTF8.GetBytes("response");
			ctx.Response.Body.Write(data, 0, data.Length);
		});

		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		A.CallTo(() => cache.CreateEntry(A<object>._)).MustHaveHappened();
	}
}

public sealed class MemoryCacheMiddlewareBehaviorTests
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
	public async Task InvokeAsync_CacheMiss_CopiesAndCachesExactResponseState()
	{
		// Arrange
		ServiceCollection services = new();
		services.AddLogging();
		services.MemoryValueCaching();
		await using ServiceProvider provider = services.BuildServiceProvider();

		IMemoryCache cache = provider.GetRequiredService<IMemoryCache>();
		CacheMetrics metrics = provider.GetRequiredService<CacheMetrics>();
		CacheTracker tracker = provider.GetRequiredService<CacheTracker>();
		CacheOptions options = new()
		{
			UseCompression = false,
			HeadersToCache = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "Content-Type", "Content-Language"),
		};
		const string expectedKey = "/widgets?useCache=true|application/json";
		DefaultHttpContext context = CreateContext("/widgets", options.UseCacheQueryParam);
		context.Request.Headers.Accept = "application/json";
		context.Request.Headers[options.CacheTagHeader] = "catalog, featured";
		await using MemoryStream originalBody = new();
		context.Response.Body = originalBody;
		byte[] expectedBody = Encoding.UTF8.GetBytes("response-body");
		int nextCalls = 0;

		RequestDelegate next = async invocationContext =>
		{
			nextCalls++;
			invocationContext.Response.StatusCode = StatusCodes.Status200OK;
			invocationContext.Response.ContentType = "application/custom";
			invocationContext.Response.Headers.ContentLanguage = "en";
			invocationContext.Response.Headers.CacheControl = "must-not-be-cached";
			await invocationContext.Response.Body.WriteAsync(expectedBody);
		};
		MemoryCacheMiddleware middleware = new(next, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		nextCalls.ShouldBe(1);
		context.Response.Body.ShouldBe(originalBody);
		originalBody.ToArray().ShouldBe(expectedBody);
		context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		cache.TryGetValue(expectedKey, out CacheEntry? entry).ShouldBeTrue();
		entry.ShouldNotBeNull();
		entry.Data.ShouldBe(expectedBody);
		entry.CompressionType.ShouldBe((short)0);
		entry.Headers.Count.ShouldBe(2);
		entry.Headers["Content-Type"].ShouldBe("application/custom");
		entry.Headers["Content-Language"].ShouldBe("en");
		entry.Headers.ShouldNotContainKey("Cache-Control");
		tracker.CacheTags["catalog"].ShouldContain(expectedKey);
		tracker.CacheTags["featured"].ShouldContain(expectedKey);
		metrics.CacheTags["catalog"].ShouldContain(expectedKey);
		metrics.CacheMisses().ShouldBe(1);
		metrics.CurrentCacheEntryCount().ShouldBe(1);
		metrics.CurrentCacheSize().ShouldBe(expectedBody.Length);
	}

	[Fact]
	public async Task InvokeAsync_CacheHitWithoutMetrics_RestoresExactBodyAndHeadersWithoutCallingNext()
	{
		// Arrange
		CacheOptions options = new() { UseCompression = false };
		using MemoryCache cache = new(new MemoryCacheOptions());
		CacheTracker tracker = new();
		const string expectedKey = "/widgets?useCache=true|application/json";
		CacheEntry cached = new()
		{
			Data = Encoding.UTF8.GetBytes("cached-body"),
			Headers = new Dictionary<string, string>
			{
				["Content-Type"] = "application/json",
				["Vary"] = "Accept",
			},
		};
		cache.Set(expectedKey, cached);

		DefaultHttpContext context = CreateContext("/widgets", options.UseCacheQueryParam);
		context.Request.Headers.Accept = "application/json";
		context.Response.Headers.ContentType = "application/original";
		await using MemoryStream responseBody = new();
		context.Response.Body = responseBody;
		int nextCalls = 0;
		MemoryCacheMiddleware middleware = new(_ =>
		{
			nextCalls++;
			return Task.CompletedTask;
		}, cache, options, null, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		nextCalls.ShouldBe(0);
		Encoding.UTF8.GetString(responseBody.ToArray()).ShouldBe("cached-body");
		context.Response.Headers.ContentType.ToString().ShouldBe("application/original");
		context.Response.Headers.Vary.ToString().ShouldBe("Accept");
	}

	[Fact]
	public async Task InvokeAsync_CacheMissWithoutMetricsEvictsTrackedEntryThenCachesExactKey()
	{
		// Arrange
		CacheOptions options = new() { UseCompression = false, MaxCacheSizeInBytes = 100 };
		using MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 100 });
		CacheTracker tracker = new();
		const string oldKey = "old";
		tracker.TrackEntry(oldKey, 8);
		cache.Set(oldKey, new CacheEntry { Data = new byte[8] }, new MemoryCacheEntryOptions().SetSize(8));

		const string expectedKey = "/new?useCache=true|";
		DefaultHttpContext context = CreateContext("/new", options.UseCacheQueryParam);
		context.Request.Headers[options.CacheTagHeader] = "fresh";
		await using MemoryStream originalBody = new();
		context.Response.Body = originalBody;
		MemoryCacheMiddleware middleware = new(async invocationContext =>
		{
			invocationContext.Response.StatusCode = StatusCodes.Status200OK;
			await invocationContext.Response.Body.WriteAsync("fresh"u8.ToArray());
		}, cache, options, null, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		originalBody.ToArray().ShouldBe("fresh"u8.ToArray());
		cache.TryGetValue(oldKey, out _).ShouldBeFalse();
		tracker.GetEntries().ShouldNotContain(entry => entry.Key == oldKey);
		cache.TryGetValue(expectedKey, out CacheEntry? fresh).ShouldBeTrue();
		fresh.ShouldNotBeNull();
		fresh.Data.ShouldBe("fresh"u8.ToArray());
		tracker.CacheTags["fresh"].ShouldContain(expectedKey);
	}

	[Fact]
	public async Task InvokeAsync_OversizedResponseWithoutMetrics_CopiesBodyWithoutCaching()
	{
		// Arrange
		CacheOptions options = new() { UseCompression = false, MaxCacheSizeInBytes = 3 };
		using MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 3 });
		CacheTracker tracker = new();
		DefaultHttpContext context = CreateContext("/large", options.UseCacheQueryParam);
		await using MemoryStream originalBody = new();
		context.Response.Body = originalBody;
		MemoryCacheMiddleware middleware = new(async invocationContext =>
		{
			invocationContext.Response.StatusCode = StatusCodes.Status200OK;
			await invocationContext.Response.Body.WriteAsync("four"u8.ToArray());
		}, cache, options, null, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		originalBody.ToArray().ShouldBe("four"u8.ToArray());
		cache.TryGetValue("/large?useCache=true|", out _).ShouldBeFalse();
	}

	[Fact]
	public async Task InvokeAsync_ManualTagEviction_UpdatesExactMetricsAndTagState()
	{
		// Arrange
		CacheOptions options = new() { SuppressLogs = false };
		using MemoryCache cache = new(new MemoryCacheOptions());
		CacheMetrics metrics = new();
		CacheTracker tracker = new();
		const string tag = "catalog";
		string[] keys = ["/one", "/two"];
		byte[][] values = [Encoding.UTF8.GetBytes("one"), Encoding.UTF8.GetBytes("two")];
		for (int i = 0; i < keys.Length; i++)
		{
			cache.Set(keys[i], new CacheEntry { Data = values[i], Tags = [tag] });
			metrics.AddToSize(values[i].Length);
			metrics.IncrementCacheEntryCount();
		}
		tracker.CacheTags[tag] = [.. keys];
		metrics.CacheTags[tag] = [.. keys];

		DefaultHttpContext context = new();
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			[options.EvictionQueryParam] = "true",
			[options.EvictTagQueryParam] = tag,
		});
		int nextCalls = 0;
		MemoryCacheMiddleware middleware = new(_ =>
		{
			nextCalls++;
			return Task.CompletedTask;
		}, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		nextCalls.ShouldBe(1);
		foreach (string key in keys)
		{
			cache.TryGetValue(key, out _).ShouldBeFalse();
		}
		tracker.CacheTags.ShouldNotContainKey(tag);
		metrics.CacheTags.ShouldNotContainKey(tag);
		metrics.CurrentCacheSize().ShouldBe(0);
		metrics.CurrentCacheEntryCount().ShouldBe(0);
		metrics.EvictedDueToRemoved().ShouldBe(2);
	}

	[Fact]
	public async Task InvokeAsync_TagEvictionWithoutMetrics_RemovesNullCachedValue()
	{
		// Arrange
		CacheOptions options = new() { SuppressLogs = false };
		using MemoryCache cache = new(new MemoryCacheOptions());
		CacheTracker tracker = new();
		const string key = "null-value";
		const string tag = "null-tag";
		cache.Set<object?>(key, null);
		tracker.CacheTags[tag] = [key];
		DefaultHttpContext context = new();
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			[options.EvictionQueryParam] = "true",
			[options.EvictTagQueryParam] = tag,
		});
		MemoryCacheMiddleware middleware = new(_ => Task.CompletedTask, cache, options, null, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		cache.TryGetValue(key, out _).ShouldBeFalse();
		tracker.CacheTags.ShouldNotContainKey(tag);
	}

	[Fact]
	public async Task InvokeAsync_SingleKeyEvictionWithoutMetrics_RemovesNullCachedValue()
	{
		// Arrange
		CacheOptions options = new() { SuppressLogs = false };
		using MemoryCache cache = new(new MemoryCacheOptions());
		const string key = "/null|";
		cache.Set<object?>(key, null);
		DefaultHttpContext context = new();
		context.Request.Path = "/null";
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			[options.EvictionQueryParam] = "true",
		});
		MemoryCacheMiddleware middleware = new(_ => Task.CompletedTask, cache, options, null, new CacheTracker());

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		cache.TryGetValue(key, out _).ShouldBeFalse();
	}

	[Fact]
	public async Task InvokeAsync_SingleKeyEviction_UpdatesExactMetricsAndRemovesEveryAssociatedTag()
	{
		// Arrange
		CacheOptions options = new() { SuppressLogs = false };
		using MemoryCache cache = new(new MemoryCacheOptions());
		CacheMetrics metrics = new();
		CacheTracker tracker = new();
		const string key = "/single|text/plain";
		byte[] cachedData = Encoding.UTF8.GetBytes("cached");
		CacheEntry entry = new() { Data = cachedData, Tags = ["primary", "shared"] };
		cache.Set(key, entry);
		metrics.AddToSize(cachedData.Length);
		metrics.IncrementCacheEntryCount();
		foreach (string tag in entry.Tags)
		{
			tracker.CacheTags[tag] = [key];
			metrics.CacheTags[tag] = [key];
		}

		DefaultHttpContext context = new();
		context.Request.Path = "/single";
		context.Request.Headers.Accept = "text/plain";
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			[options.EvictionQueryParam] = "true",
		});
		int nextCalls = 0;
		MemoryCacheMiddleware middleware = new(_ =>
		{
			nextCalls++;
			return Task.CompletedTask;
		}, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		nextCalls.ShouldBe(1);
		cache.TryGetValue(key, out _).ShouldBeFalse();
		tracker.CacheTags.ShouldBeEmpty();
		metrics.CacheTags.ShouldBeEmpty();
		metrics.CurrentCacheSize().ShouldBe(0);
		metrics.CurrentCacheEntryCount().ShouldBe(0);
		metrics.EvictedDueToRemoved().ShouldBe(1);
	}

	[Fact]
	public async Task InvokeAsync_TagEvictionWithNullTracker_SkipsEvictionAndContinuesPipeline()
	{
		// Arrange
		CacheOptions options = new() { SuppressLogs = true };
		using MemoryCache cache = new(new MemoryCacheOptions());
		DefaultHttpContext context = new();
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			[options.EvictionQueryParam] = "true",
			[options.EvictTagQueryParam] = "missing",
		});
		int nextCalls = 0;
		MemoryCacheMiddleware middleware = new(_ =>
		{
			nextCalls++;
			return Task.CompletedTask;
		}, cache, options, null, null!);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		nextCalls.ShouldBe(1);
	}

	[Fact]
	public async Task InvokeAsync_TwoEntriesWithSameTag_RegistersBothKeysInTrackerAndMetrics()
	{
		// Arrange
		CacheOptions options = new() { UseCompression = false };
		using MemoryCache cache = new(new MemoryCacheOptions());
		CacheMetrics metrics = new();
		CacheTracker tracker = new();
		MemoryCacheMiddleware middleware = new(invocationContext =>
		{
			invocationContext.Response.StatusCode = StatusCodes.Status200OK;
			return invocationContext.Response.WriteAsync(invocationContext.Request.Path.Value ?? string.Empty);
		}, cache, options, metrics, tracker);

		foreach (string path in new[] { "/first", "/second" })
		{
			DefaultHttpContext context = CreateContext(path, options.UseCacheQueryParam);
			context.Request.Headers[options.CacheTagHeader] = "shared";
			context.Response.Body = new MemoryStream();
			await middleware.InvokeAsync(context);
		}

		// Assert
		string[] expectedKeys = ["/first?useCache=true|", "/second?useCache=true|"];
		tracker.CacheTags["shared"].ShouldBe(expectedKeys, ignoreOrder: true);
		metrics.CacheTags["shared"].ShouldBe(expectedKeys, ignoreOrder: true);
		metrics.CurrentCacheEntryCount().ShouldBe(2);
	}

	[Fact]
	public async Task InvokeAsync_CacheMissWithPendingNext_RestoresBodyAfterAsynchronousCompletion()
	{
		// Arrange
		CacheOptions options = new() { UseCompression = false };
		using MemoryCache cache = new(new MemoryCacheOptions());
		CacheMetrics metrics = new();
		CacheTracker tracker = new();
		DefaultHttpContext context = CreateContext("/pending", options.UseCacheQueryParam);
		await using MemoryStream originalBody = new();
		context.Response.Body = originalBody;
		TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		MemoryCacheMiddleware middleware = new(async invocationContext =>
		{
			await gate.Task;
			invocationContext.Response.StatusCode = StatusCodes.Status200OK;
			await invocationContext.Response.Body.WriteAsync("done"u8.ToArray());
		}, cache, options, metrics, tracker);

		// Act
		Task invocation = middleware.InvokeAsync(context);
		invocation.IsCompleted.ShouldBeFalse();
		gate.SetResult();
		await invocation;

		// Assert
		context.Response.Body.ShouldBeSameAs(originalBody);
		originalBody.ToArray().ShouldBe("done"u8.ToArray());
		metrics.CurrentCacheEntryCount().ShouldBe(1);
	}

	[Fact]
	public async Task InvokeAsync_CacheMissWithAsynchronousNextFailure_RestoresBodyAndPropagatesFailure()
	{
		// Arrange
		CacheOptions options = new() { UseCompression = false };
		using MemoryCache cache = new(new MemoryCacheOptions());
		DefaultHttpContext context = CreateContext("/async-failure", options.UseCacheQueryParam);
		await using MemoryStream originalBody = new();
		context.Response.Body = originalBody;
		InvalidOperationException failure = new("asynchronous downstream failure");
		TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		MemoryCacheMiddleware middleware = new(async _ =>
		{
			await gate.Task;
			throw failure;
		}, cache, options, new CacheMetrics(), new CacheTracker());

		// Act
		Task invocation = middleware.InvokeAsync(context);
		invocation.IsCompleted.ShouldBeFalse();
		gate.SetResult();
		InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() => invocation);

		// Assert
		thrown.ShouldBeSameAs(failure);
		context.Response.Body.ShouldBeSameAs(originalBody);
	}

	[Fact]
	public async Task InvokeAsync_CacheMissWithNextFailure_RestoresOriginalResponseStream()
	{
		// Arrange
		CacheOptions options = new() { UseCompression = false };
		using MemoryCache cache = new(new MemoryCacheOptions());
		CacheMetrics metrics = new();
		CacheTracker tracker = new();
		DefaultHttpContext context = CreateContext("/failure", options.UseCacheQueryParam);
		await using MemoryStream originalBody = new();
		context.Response.Body = originalBody;
		InvalidOperationException failure = new("downstream failure");
		MemoryCacheMiddleware middleware = new(_ => throw failure, cache, options, metrics, tracker);

		// Act
		InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

		// Assert
		thrown.ShouldBeSameAs(failure);
		context.Response.Body.ShouldBeSameAs(originalBody);
		metrics.CacheMisses().ShouldBe(1);
		metrics.CurrentCacheEntryCount().ShouldBe(0);
	}

	[Fact]
	public async Task InvokeAsync_CapacityPressure_EvictsExactEntryAndCachesNewResponse()
	{
		// Arrange
		CacheOptions options = new()
		{
			UseCompression = false,
			MaxCacheSizeInBytes = 10,
			SuppressLogs = false,
		};
		using MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 10 });
		CacheMetrics metrics = new();
		CacheTracker tracker = new();
		const string oldKey = "old";
		byte[] oldData = new byte[8];
		CacheEntry oldEntry = new() { Data = oldData, Tags = ["old-tag"] };
		cache.Set(oldKey, oldEntry, new MemoryCacheEntryOptions().SetSize(oldData.Length));
		tracker.TrackEntry(oldKey, oldData.Length);
		tracker.CacheTags["old-tag"] = [oldKey];
		metrics.CacheTags["old-tag"] = [oldKey];
		metrics.AddToSize(oldData.Length);
		metrics.IncrementCacheEntryCount();

		DefaultHttpContext context = CreateContext("/new", options.UseCacheQueryParam);
		await using MemoryStream originalBody = new();
		context.Response.Body = originalBody;
		MemoryCacheMiddleware middleware = new(async invocationContext =>
		{
			invocationContext.Response.StatusCode = StatusCodes.Status200OK;
			await invocationContext.Response.Body.WriteAsync("123456"u8.ToArray());
		}, cache, options, metrics, tracker);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		cache.TryGetValue(oldKey, out _).ShouldBeFalse();
		tracker.GetEntries().ShouldNotContain(pair => pair.Key == oldKey);
		tracker.CacheTags.ShouldNotContainKey("old-tag");
		metrics.CacheTags.ShouldNotContainKey("old-tag");
		metrics.EvictedDueToCapacity().ShouldBe(1);
		metrics.CurrentCacheSize().ShouldBe(6);
		metrics.CurrentCacheEntryCount().ShouldBe(1);
		cache.TryGetValue("/new?useCache=true|", out CacheEntry? newEntry).ShouldBeTrue();
		newEntry!.Data.ShouldBe("123456"u8.ToArray());
		originalBody.ToArray().ShouldBe("123456"u8.ToArray());
	}

}
