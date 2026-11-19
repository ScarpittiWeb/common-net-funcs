using CommonNetFuncs.Web.Api.MsgPack;
using CommonNetFuncs.Web.Requests.MessagePack;
using MessagePack;
using MessagePack.AspNetCoreMvcFormatter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Xunit.TestContext;

namespace Web.Api.Tests.MsgPack;

[MessagePackObject]
public sealed class MessagePackStreamingItem
{
	[Key(0)]
	public DateTime Timestamp { get; init; }
}

[ApiController]
[Route("message-pack-streaming")]
public sealed class MessagePackStreamingController : ControllerBase
{
	private static readonly DateTime Timestamp = new(2026, 11, 19, 15, 30, 0, DateTimeKind.Utc);

	public static readonly MessagePackSerializerOptions ExplicitOptions =
		MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);

	[HttpGet("default-options")]
	public IActionResult GetWithDefaultOptions() => this.StreamMessagePack(GetItems());

	[HttpGet("explicit-options")]
	public IActionResult GetWithExplicitOptions() => this.StreamMessagePack(GetItems(), ExplicitOptions);

	[HttpGet("standard-options")]
	public IActionResult GetWithStandardOptions() => this.StreamMessagePack(GetItems());

	private static async IAsyncEnumerable<MessagePackStreamingItem> GetItems()
	{
		await Task.Yield();
		yield return new MessagePackStreamingItem { Timestamp = Timestamp };
	}
}

public sealed class MessagePackStreamingTests
{
	[Fact]
	public async Task StreamMessagePack_WithoutExplicitOptions_UsesMvcOutputFormatterOptions()
	{
		MessagePackSerializerOptions mvcOptions = MsgPackSerializerConfig.DateTimesAsStrings
			.WithCompression(MessagePackCompression.Lz4BlockArray);
		await using WebApplication app = await CreateApp(mvcOptions);

		using HttpClient client = app.GetTestClient();
		byte[] response = await GetMessagePackAsync(client, "default-options");

		MessagePackStreamingItem item = MessagePackSerializer.Deserialize<MessagePackStreamingItem>(
			response,
			mvcOptions,
			Current.CancellationToken);
		item.Timestamp.ShouldBe(new DateTime(2026, 11, 19, 15, 30, 0, DateTimeKind.Utc));
	}

	[Fact]
	public async Task StreamMessagePack_WithExplicitOptions_UsesExplicitOptionsInsteadOfMvcOutputFormatterOptions()
	{
		MessagePackSerializerOptions mvcOptions = MsgPackSerializerConfig.DateTimesAsStrings
			.WithCompression(MessagePackCompression.Lz4BlockArray);
		await using WebApplication app = await CreateApp(mvcOptions);

		using HttpClient client = app.GetTestClient();
		byte[] response = await GetMessagePackAsync(client, "explicit-options");

		MessagePackStreamingItem item = MessagePackSerializer.Deserialize<MessagePackStreamingItem>(
			response,
			MessagePackStreamingController.ExplicitOptions,
			Current.CancellationToken);
		item.Timestamp.ShouldBe(new DateTime(2026, 11, 19, 15, 30, 0, DateTimeKind.Utc));
	}

	[Fact]
	public async Task StreamMessagePack_WithoutMvcOutputFormatter_UsesStandardOptions()
	{
		await using WebApplication app = await CreateApp();

		using HttpClient client = app.GetTestClient();
		byte[] response = await GetMessagePackAsync(client, "standard-options");

		MessagePackStreamingItem item = MessagePackSerializer.Deserialize<MessagePackStreamingItem>(
			response,
			MessagePackSerializerOptions.Standard,
			Current.CancellationToken);
		item.Timestamp.ShouldBe(new DateTime(2026, 11, 19, 15, 30, 0, DateTimeKind.Utc));
	}

	private static async Task<byte[]> GetMessagePackAsync(HttpClient client, string action)
	{
		using HttpRequestMessage request = new(HttpMethod.Get, $"/message-pack-streaming/{action}");
		request.Headers.Accept.ParseAdd("application/x-msgpack");
		using HttpResponseMessage response = await client.SendAsync(request, Current.CancellationToken);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsByteArrayAsync(Current.CancellationToken);
	}

	private static async Task<WebApplication> CreateApp(MessagePackSerializerOptions? mvcOptions = null)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseTestServer();
		IMvcBuilder mvcBuilder = builder.Services.AddControllers();
		if (mvcOptions is not null)
		{
			mvcBuilder.AddMvcOptions(options => options.OutputFormatters.Insert(
				0,
				new MessagePackOutputFormatter(mvcOptions)));
		}

		WebApplication app = builder.Build();
		app.MapControllers();
		await app.StartAsync();
		return app;
	}
}
