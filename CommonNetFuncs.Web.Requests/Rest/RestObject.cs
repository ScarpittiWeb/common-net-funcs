namespace CommonNetFuncs.Web.Requests.Rest;

internal interface IRestResponse
{
	HttpResponseMessage? Response { get; }
}

/// <summary>
/// Helper class to get around not being able to pass primitive types directly to a generic type
/// </summary>
/// <typeparam name="T">Primitive type to pass to the REST request</typeparam>
public sealed class RestObject<T> : IRestResponse // where TBody : class
{
	public T? Result { get; set; }

	public HttpResponseMessage? Response { get; set; }

	public string? Error { get; set; }
}

/// <summary>
/// Helper class to get around not being able to pass primitive types directly to a generic type
/// </summary>
/// <typeparam name="T">Primitive type to pass to the REST request</typeparam>
public sealed class StreamingRestObject<T> : IRestResponse // where TBody : class
{
	public IAsyncEnumerable<T?>? Result { get; set; }

	public HttpResponseMessage? Response { get; set; }
}
