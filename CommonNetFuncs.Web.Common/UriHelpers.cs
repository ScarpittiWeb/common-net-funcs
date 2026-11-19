using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using static System.Web.HttpUtility;
using static CommonNetFuncs.Core.Strings;

namespace CommonNetFuncs.Web.Common;

public static class UriHelpers
{
	/// <summary>
	/// Converts list of query parameters into a query parameter string
	/// </summary>
	/// <param name="parameters">List of a type that can be converted to string</param>
	/// <param name="queryParameterName">The name to be used in front of the equals sign for the query parameter string</param>
	/// <returns>String representation of the list passed in as query parameters with the name passed in as queryParameterName</returns>
	public static string ListToQueryParameters<T>(this IEnumerable<T>? parameters, string? queryParameterName)
	{
		if (parameters?.Any() != true || queryParameterName.IsNullOrWhiteSpace())
		{
			return string.Empty;
		}

		StringBuilder stringBuilder = new();
		bool firstItem = true;
		foreach (T parameter in parameters)
		{
			if (!firstItem)
			{
				stringBuilder.Append('&');
			}
			stringBuilder.Append(queryParameterName);
			stringBuilder.Append('=');
			AppendValue(stringBuilder, parameter);
			firstItem = false;
		}
		return stringBuilder.ToString();
	}

	// The typeof(T) checks are JIT-time constants, so each instantiation keeps exactly one branch and the (X)(object)value casts do not box.
	// Every typed overload formats with the current culture, exactly like the boxed ToString() call made by Append(object).
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void AppendValue<T>(StringBuilder stringBuilder, T value)
	{
		if (typeof(T) == typeof(int))
		{
			stringBuilder.Append((int)(object)value!);
		}
		else if (typeof(T) == typeof(long))
		{
			stringBuilder.Append((long)(object)value!);
		}
		else if (typeof(T) == typeof(short))
		{
			stringBuilder.Append((short)(object)value!);
		}
		else if (typeof(T) == typeof(byte))
		{
			stringBuilder.Append((byte)(object)value!);
		}
		else if (typeof(T) == typeof(sbyte))
		{
			stringBuilder.Append((sbyte)(object)value!);
		}
		else if (typeof(T) == typeof(uint))
		{
			stringBuilder.Append((uint)(object)value!);
		}
		else if (typeof(T) == typeof(ulong))
		{
			stringBuilder.Append((ulong)(object)value!);
		}
		else if (typeof(T) == typeof(ushort))
		{
			stringBuilder.Append((ushort)(object)value!);
		}
		else if (typeof(T) == typeof(double))
		{
			stringBuilder.Append((double)(object)value!);
		}
		else if (typeof(T) == typeof(float))
		{
			stringBuilder.Append((float)(object)value!);
		}
		else if (typeof(T) == typeof(decimal))
		{
			stringBuilder.Append((decimal)(object)value!);
		}
		else if (typeof(T) == typeof(bool))
		{
			stringBuilder.Append((bool)(object)value!);
		}
		else if (typeof(T) == typeof(char))
		{
			stringBuilder.Append((char)(object)value!);
		}
		else if (typeof(T) == typeof(Guid))
		{
			stringBuilder.Append($"{(Guid)(object)value!}");
		}
		else
		{
			stringBuilder.Append(value);
		}
	}

	/// <summary>
	/// Converts list of query parameters into a query parameter string.
	/// </summary>
	/// <typeparam name="T">The type of the value in the KeyValuePair that can be converted to string.</typeparam>
	/// <param name="parameters">Collection of key value pairs with a value that can be converted to string.</param>
	/// <returns>String representation of the list passed in as query parameters with the name passed in as queryParameterName</returns>
	public static string ListToQueryParameters<T>(this IEnumerable<KeyValuePair<string, T>>? parameters)
	{
		if (parameters?.Any() != true)
		{
			return string.Empty;
		}

		StringBuilder stringBuilder = new();
		bool firstItem = true;
		foreach (KeyValuePair<string, T> parameter in parameters)
		{
			if (!firstItem)
			{
				stringBuilder.Append('&');
			}
			stringBuilder.Append(parameter.Key);
			stringBuilder.Append('=');
			stringBuilder.Append(parameter.Value);
			firstItem = false;
		}
		return stringBuilder.ToString();
	}

	[return: NotNullIfNotNull(nameof(dateTime))]
	public static string? ToUriSafeString(this DateTime? dateTime, string? dateFormat = null)
	{
		return dateTime.ToNString(dateFormat ?? TimestampUrlFormat);
	}

	public static string ToUriSafeString(this DateTime dateTime, string? dateFormat = null)
	{
		return dateTime.ToString(dateFormat ?? TimestampUrlFormat);
	}

	[return: NotNullIfNotNull(nameof(date))]
	public static string? ToUriSafeString(this DateOnly? date, string? dateFormat = null)
	{
		return date.ToNString(dateFormat ?? DateOnlyUrlFormat);
	}

	public static string ToUriSafeString(this DateOnly date, string? dateFormat = null)
	{
		return date.ToString(dateFormat ?? DateOnlyUrlFormat);
	}

	public static DateTime? ParseUriSafeDateTime(this string? urlSafeDateTime, string? dateFormat = null)
	{
		if (DateTime.TryParseExact(urlSafeDateTime, dateFormat ?? TimestampUrlFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dateTime))
		{
			return dateTime;
		}
		return null;
	}

	public static DateOnly? ParseUriSafeDateOnly(this string? urlSafeDateTime, string? dateFormat = null)
	{
		if (DateOnly.TryParseExact(urlSafeDateTime, dateFormat ?? DateOnlyUrlFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly dateTime))
		{
			return dateTime;
		}
		return null;
	}

	[return: NotNullIfNotNull(nameof(url))]
	public static string? GetRedactedUri(this string? url, string redactedString = "<REDACTED>")
	{
		if (url.IsNullOrWhiteSpace())
		{
			return null;
		}

		// Parse the URL to get the base part and the query string
		Uri uri = new(url);
		NameValueCollection queryParameters = ParseQueryString(uri.Query);

		if (queryParameters.Count > 0)
		{
			for (int i = 0; i < queryParameters.Count; i++)
			{
				queryParameters.Set(queryParameters.GetKey(i), redactedString); //Replace values with redactedString value
			}

			// Use Authority instead of GetLeftPart to avoid extra slash, then rebuild the path
			string basePart = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
			return $"{basePart}?{queryParameters}";
		}
		else
		{
			return url;
		}
	}

	public static string GetUriFromRequest(HttpRequest request)
	{
		return $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
	}
}
