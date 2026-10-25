using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CommonNetFuncs.Web.Middleware;

public sealed class UseXsrfTokenMiddleware(RequestDelegate next, IAntiforgery antiforgery, bool httpOnly, bool secure = true)
{
	private readonly RequestDelegate next = next;
	private readonly IAntiforgery antiforgery = antiforgery;
	private readonly bool httpOnly = httpOnly;
	private readonly bool secure = secure;

	public async Task InvokeAsync(HttpContext context)
	{
		AntiforgeryTokenSet tokenSet = antiforgery.GetAndStoreTokens(context);
		context.Response.Cookies.Append("XSRF-TOKEN", tokenSet.RequestToken!, new CookieOptions { HttpOnly = httpOnly, Secure = secure });
		await next(context).ConfigureAwait(false);
	}
}

/// <summary>
/// Extension method used to add the middleware to the HTTP request pipeline
/// </summary>
public static class UseXsrfTokenMiddlewareExtension
{
	/// <summary>
	/// Adds the XSRF token middleware to the HTTP request pipeline.
	/// </summary>
	/// <param name="builder">The application builder.</param>
	/// <param name="httpOnly">Indicates whether the XSRF token cookie should be HTTP-only. If true, the cookie will not be accessible via client-side scripts.</param>
	/// <param name="secure">Indicates whether the XSRF token cookie should be secure. If true, the cookie will only be sent over HTTPS connections.</param>
	/// <returns>The application builder.</returns>
	public static IApplicationBuilder UseXsrfToken(this IApplicationBuilder builder, bool httpOnly = false, bool secure = true)
	{
		return builder.UseMiddleware<UseXsrfTokenMiddleware>(httpOnly, secure);
	}
}
