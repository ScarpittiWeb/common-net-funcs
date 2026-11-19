using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;

namespace CommonNetFuncs.Web.Requests;

public static class DistributedCacheExtensions
{
	private static readonly JsonSerializerOptions serializerOptions = new()
	{
		PropertyNamingPolicy = null,
		WriteIndented = true,
		AllowTrailingCommas = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

	public static Task SetAsync<T>(this IDistributedCache cache, string key, T value)
	{
		return SetAsync(cache, key, value, new DistributedCacheEntryOptions());
	}

	public static Task SetAsync<T>(this IDistributedCache cache, string key, T value, DistributedCacheEntryOptions options)
	{
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, serializerOptions);
		return cache.SetAsync(key, bytes, options);
	}

	public static bool TryGetValue<T>(this IDistributedCache cache, string key, out T? value)
	{
		byte[]? val = cache.Get(key);
		value = default;
		if (val == null)
		{
			return false;
		}

		value = JsonSerializer.Deserialize<T>(val, serializerOptions);
		return true;
	}

	public static async Task<T?> TryGetValueAsync<T>(this IDistributedCache cache, string key)
	{
		byte[]? val = await cache.GetAsync(key).ConfigureAwait(false);
		if (val == null)
		{
			return default;
		}

		return DeserializeCachedValue<T>(val);
	}

	// The previous stream-based reader skipped a single leading UTF-8 BOM; span deserialization does not, so strip it here to keep parity
	private static T? DeserializeCachedValue<T>(byte[] val)
	{
		ReadOnlySpan<byte> utf8Json = val;
		if (utf8Json.StartsWith(Utf8Bom))
		{
			utf8Json = utf8Json[Utf8Bom.Length..];
		}
		return JsonSerializer.Deserialize<T>(utf8Json, serializerOptions);
	}
}
