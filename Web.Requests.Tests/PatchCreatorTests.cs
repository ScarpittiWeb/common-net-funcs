using CommonNetFuncs.Web.Requests;
using MessagePack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Reflection;

namespace Web.Requests.Tests;

public class PatchCreatorTests
{
	private sealed class TestModel
	{
		public int Id { get; set; }

		public string? Name { get; set; }

		public decimal Amount { get; set; }

		public DateTime? DateTime { get; set; }

		public DateOnly? DateOnly { get; set; }

		public DateTimeOffset? DateTimeOffset { get; set; }

		public NestedModel? Nested { get; set; }
	}

	private sealed class NestedModel
	{
		public string? Value { get; set; }
	}

	private const string AddOperation = "add";
	private const string RemoveOperation = "remove";
	private const string ReplaceOperation = "replace";

	[Fact]
	public void CreatePatch_ShouldReturnEmptyPatch_WhenObjectsAreEqual()
	{
		TestModel original = new() { Id = 1, Name = "Test", Amount = 10.5m, DateTime = DateTime.UtcNow, DateTimeOffset = DateTimeOffset.Now, DateOnly = DateOnly.FromDateTime(DateTime.Now), Nested = new NestedModel { Value = "A" } };
		TestModel modified = JsonConvert.DeserializeObject<TestModel>(JsonConvert.SerializeObject(original))!;

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.ShouldBeEmpty();
	}

	[Theory]
	[InlineData(1, 2)]
	[InlineData(10, 20)]
	public void CreatePatch_ShouldDetectIntChange(int originalId, int modifiedId)
	{
		TestModel original = new() { Id = originalId, Name = "Test" };
		TestModel modified = new() { Id = modifiedId, Name = "Test" };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Id");
		patch.Operations[0].value.ShouldBeOfType<JValue>();
		((JValue)patch.Operations[0].value).Value.ShouldBe(modifiedId);
	}

	[Theory]
	[InlineData("Alpha", "Beta")]
	[InlineData("Test", "Test2")]
	public void CreatePatch_ShouldDetectStringChange(string originalName, string modifiedName)
	{
		TestModel original = new() { Id = 1, Name = originalName };
		TestModel modified = new() { Id = 1, Name = modifiedName };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Name");
		patch.Operations[0].value.ShouldBeOfType<JValue>();
		((JValue)patch.Operations[0].value).Value.ShouldBe(modifiedName);
	}

	[Fact]
	public void CreatePatch_ShouldDetectDecimalChange()
	{
		TestModel original = new() { Id = 1, Amount = 1.23m };
		TestModel modified = new() { Id = 1, Amount = 4.56m };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Amount");
		patch.Operations[0].value.ShouldBeOfType<JValue>();
		((JValue)patch.Operations[0].value).Value.ShouldBe(4.56m);
	}

	[Fact]
	public void CreatePatch_ShouldDetectDateChange()
	{
		DateTime originalDate = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
		DateTime modifiedDate = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
		TestModel original = new() { Id = 1, DateTime = originalDate, DateOnly = DateOnly.FromDateTime(originalDate), DateTimeOffset = new(originalDate) };
		TestModel modified = new() { Id = 1, DateTime = modifiedDate, DateOnly = DateOnly.FromDateTime(modifiedDate), DateTimeOffset = new(modifiedDate) };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(3);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/DateTime");
		patch.Operations[0].value.ShouldBeOfType<JValue>();
		patch.Operations[1].op.ShouldBe(ReplaceOperation);
		patch.Operations[1].path.ShouldBe("/DateOnly");
		patch.Operations[1].value.ShouldBeOfType<JValue>();
		patch.Operations[2].op.ShouldBe(ReplaceOperation);
		patch.Operations[2].path.ShouldBe("/DateTimeOffset");
		patch.Operations[2].value.ShouldBeOfType<JValue>();
		((JValue)patch.Operations[0].value).Value.ShouldBe(modified.DateTime);
		DateOnly.TryParse(((JValue)patch.Operations[1].value).Value?.ToString(), new CultureInfo("en-US"), out DateOnly dateOnlyResult).ShouldBeTrue();
		DateOnly? nullableDateOnlyResult = dateOnlyResult;
		nullableDateOnlyResult.ShouldBe(modified.DateOnly);
		((DateTimeOffset?)((JValue)patch.Operations[2].value).Value).ShouldBe(modified.DateTimeOffset);
	}

	[Fact]
	public void CreatePatch_ShouldDetectAddedProperty()
	{
		JObject original = new() { ["A"] = 1 };
		JObject modified = new() { ["A"] = 1, ["B"] = 2 };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(AddOperation);
		patch.Operations[0].path.ShouldBe("/B");
		patch.Operations[0].value.ShouldBeOfType<JValue>();
		((JValue)patch.Operations[0].value).Value.ShouldBe(2);
	}

	[Fact]
	public void CreatePatch_ShouldDetectRemovedProperty()
	{
		JObject original = new() { ["A"] = 1, ["B"] = 2 };
		JObject modified = new() { ["A"] = 1 };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(RemoveOperation);
		patch.Operations[0].path.ShouldBe("/B");
	}

	[Fact]
	public void CreatePatch_ShouldDetectNestedObjectChange()
	{
		TestModel original = new() { Id = 1, Nested = new NestedModel { Value = "X" } };
		TestModel modified = new() { Id = 1, Nested = new NestedModel { Value = "Y" } };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Nested/Value");
		patch.Operations[0].value.ShouldBeOfType<JValue>();
		((JValue)patch.Operations[0].value).Value.ShouldBe("Y");
	}

	[Fact]
	public void CreatePatch_ShouldDetectNestedObjectAdded()
	{
		TestModel original = new() { Id = 1, Nested = null };
		TestModel modified = new() { Id = 1, Nested = new NestedModel { Value = "Z" } };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Nested");
		patch.Operations[0].value.ShouldBeOfType<JObject>();
	}

	[Fact]
	public void CreatePatch_ShouldDetectNestedObjectRemoved()
	{
		TestModel original = new() { Id = 1, Nested = new NestedModel { Value = "Z" } };
		TestModel modified = new() { Id = 1, Nested = null };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Nested");
	}

	[Fact]
	public void CreatePatch_ShouldUseCustomJsonSerializer()
	{
		TestModel original = new() { Id = 1, Name = "A" };
		TestModel modified = new() { Id = 1, Name = "B" };

		JsonSerializerSettings customSettings = new() { NullValueHandling = NullValueHandling.Ignore };
		JsonSerializer customSerializer = JsonSerializer.Create(customSettings);

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified, customSerializer);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Name");
		patch.Operations[0].value.ShouldBeOfType<JValue>();
		((JValue)patch.Operations[0].value).Value.ShouldBe("B");
	}

	[Fact]
	public void CreatePatch_ShouldHandleNullProperties()
	{
		TestModel original = new() { Id = 1, Name = null };
		TestModel modified = new() { Id = 1, Name = "NotNull" };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Name");
		patch.Operations[0].value.ShouldBeOfType<JValue>();
		((JValue)patch.Operations[0].value).Value.ShouldBe("NotNull");
	}

	[Fact]
	public void CreatePatch_ShouldHandleNullToNull_NoPatch()
	{
		TestModel original = new() { Id = 1, Name = null };
		TestModel modified = new() { Id = 1, Name = null };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.ShouldBeEmpty();
	}

	[Fact]
	public void CreatePatch_ShouldHandleComplexTypeChange()
	{
		var original = new { A = new { B = 1 } };
		var modified = new { A = new { B = 2 } };

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/A/B");
		patch.Operations[0].value.ShouldBeOfType<JValue>();
		((JValue)patch.Operations[0].value).Value.ShouldBe(2);
	}

	[Fact]
	public void FillPatchForObject_ShouldRecurse_WhenFloatTypeAndObjectTypeIsTrue()
	{
		// Arrange: Create nested objects where the property is a float and a nested object

		JObject originalNested = new() { ["Value"] = 1.23f };
		JObject modifiedNested = new() { ["Value"] = 4.56f };

		JObject original = new() { ["Nested"] = originalNested };
		JObject modified = new() { ["Nested"] = modifiedNested };

		JsonPatchDocument patch = new();

		// Act: Call FillPatchForObject directly via reflection since it's private

		MethodInfo? method = typeof(PatchCreator).GetMethod("FillPatchForObject", BindingFlags.NonPublic | BindingFlags.Static);
		method!.Invoke(null, new object[] { original, modified, patch, "/" });

		// Assert: Should have a replace operation for the nested float value

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe("replace");
		patch.Operations[0].path.ShouldBe("/Nested/Value");
		patch.Operations[0].value.ShouldBeOfType<JValue>();
		((JValue)patch.Operations[0].value).Value.ShouldBe(4.56f);
	}

	[Fact]
	public void CreatePatch_ShouldDetectTypeMismatch_StringToNumber()
	{
		// Arrange: Property changes type from string to number

		JObject original = new() { ["Value"] = "123" };
		JObject modified = new() { ["Value"] = 123 };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: Should replace due to type mismatch

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Value");
		((JValue)patch.Operations[0].value).Value.ShouldBe(123);
	}

	[Fact]
	public void CreatePatch_ShouldDetectTypeMismatch_NumberToBoolean()
	{
		// Arrange: Property changes type from number to boolean

		JObject original = new() { ["Flag"] = 1 };
		JObject modified = new() { ["Flag"] = true };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: Should replace due to type mismatch

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Flag");
		((JValue)patch.Operations[0].value).Value.ShouldBe(true);
	}

	[Fact]
	public void CreatePatch_ShouldNotGeneratePatch_WhenFloatsAreEqual()
	{
		// Arrange: Same float value

		TestModel original = new() { Id = 1, Amount = 99.99m };
		TestModel modified = new() { Id = 1, Amount = 99.99m };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: No operations when values are equal

		patch.Operations.ShouldBeEmpty();
	}

	[Fact]
	public void CreatePatch_ShouldNotGeneratePatch_WhenDateTimesAreEqual()
	{
		// Arrange: Same DateTime value

		DateTime sameDate = new(2024, 6, 15, 10, 30, 45, DateTimeKind.Utc);
		TestModel original = new() { Id = 1, DateTime = sameDate };
		TestModel modified = new() { Id = 1, DateTime = sameDate };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: No operations when dates are equal

		patch.Operations.ShouldBeEmpty();
	}

	[Fact]
	public void CreatePatch_ShouldNotGeneratePatch_WhenDateOnlyAreEqual()
	{
		// Arrange: Same DateOnly value

		DateOnly sameDate = new(2024, 6, 15);
		TestModel original = new() { Id = 1, DateOnly = sameDate };
		TestModel modified = new() { Id = 1, DateOnly = sameDate };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: No operations when dates are equal

		patch.Operations.ShouldBeEmpty();
	}

	[Fact]
	public void CreatePatch_ShouldNotGeneratePatch_WhenDateTimeOffsetsAreEqual()
	{
		// Arrange: Same DateTimeOffset value

		DateTimeOffset sameOffset = new(2024, 6, 15, 10, 30, 45, TimeSpan.FromHours(-5));
		TestModel original = new() { Id = 1, DateTimeOffset = sameOffset };
		TestModel modified = new() { Id = 1, DateTimeOffset = sameOffset };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: No operations when offsets are equal

		patch.Operations.ShouldBeEmpty();
	}

	[Fact]
	public void CreatePatch_ShouldDetectDateTimeOffsetChange()
	{
		// Arrange: Different DateTimeOffset values

		DateTimeOffset originalOffset = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
		DateTimeOffset modifiedOffset = new(2024, 1, 2, 12, 0, 0, TimeSpan.Zero);
		TestModel original = new() { Id = 1, DateTimeOffset = originalOffset };
		TestModel modified = new() { Id = 1, DateTimeOffset = modifiedOffset };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: Should detect the change

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/DateTimeOffset");
	}

	[Fact]
	public void CreatePatch_ShouldDetectDateOnlyChange()
	{
		// Arrange: Different DateOnly values

		DateOnly originalDate = new(2024, 1, 1);
		DateOnly modifiedDate = new(2024, 12, 31);
		TestModel original = new() { Id = 1, DateOnly = originalDate };
		TestModel modified = new() { Id = 1, DateOnly = modifiedDate };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: Should detect the change

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/DateOnly");
	}

	[Fact]
	public void CreatePatch_ShouldHandleMultiplePropertiesAdded()
	{
		// Arrange: Multiple new properties

		JObject original = new() { ["A"] = 1 };
		JObject modified = new() { ["A"] = 1, ["B"] = 2, ["C"] = 3 };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: Should have add operations for both new properties

		patch.Operations.Count.ShouldBe(2);
		patch.Operations.ShouldAllBe(op => op.op == AddOperation);
		patch.Operations.ShouldContain(op => op.path == "/B");
		patch.Operations.ShouldContain(op => op.path == "/C");
	}

	[Fact]
	public void CreatePatch_ShouldHandleMultiplePropertiesRemoved()
	{
		// Arrange: Multiple properties removed

		JObject original = new() { ["A"] = 1, ["B"] = 2, ["C"] = 3 };
		JObject modified = new() { ["A"] = 1 };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: Should have remove operations for both removed properties

		patch.Operations.Count.ShouldBe(2);
		patch.Operations.ShouldAllBe(op => op.op == RemoveOperation);
		patch.Operations.ShouldContain(op => op.path == "/B");
		patch.Operations.ShouldContain(op => op.path == "/C");
	}

	[Fact]
	public void CreatePatch_ShouldHandleMixedOperations()
	{
		// Arrange: Add, remove, and replace operations

		JObject original = new() { ["Keep"] = 1, ["Change"] = "old", ["Remove"] = 99 };
		JObject modified = new() { ["Keep"] = 1, ["Change"] = "new", ["Add"] = 42 };

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: Should have all three operation types

		patch.Operations.Count.ShouldBe(3);
		patch.Operations.ShouldContain(op => op.op == RemoveOperation && op.path == "/Remove");
		patch.Operations.ShouldContain(op => op.op == AddOperation && op.path == "/Add");
		patch.Operations.ShouldContain(op => op.op == ReplaceOperation && op.path == "/Change");
	}

	[Fact]
	public void CreatePatch_ShouldHandleDeepNestedObjects()
	{
		// Arrange: Deeply nested structure

		var original = new
		{
			Level1 = new
			{
				Level2 = new
				{
					Level3 = new
					{
						Value = "original"
					}
				}
			}
		};
		var modified = new
		{
			Level1 = new
			{
				Level2 = new
				{
					Level3 = new
					{
						Value = "modified"
					}
				}
			}
		};

		// Act

		JsonPatchDocument patch = PatchCreator.CreatePatch(original, modified);

		// Assert: Should detect the deep change

		patch.Operations.Count.ShouldBe(1);
		patch.Operations[0].op.ShouldBe(ReplaceOperation);
		patch.Operations[0].path.ShouldBe("/Level1/Level2/Level3/Value");
		((JValue)patch.Operations[0].value).Value.ShouldBe("modified");
	}

	[Fact]
	public void CreatePatch_ShouldIgnoreReferenceLoops()
	{
		// Arrange: Test that reference loop handling works (via JsonSerializerSettings)

		var circularObject = new { Id = 1, Name = "Test" };

		// Act & Assert: Should not throw with reference loop handling

		JsonPatchDocument patch = PatchCreator.CreatePatch(circularObject, circularObject);
		patch.Operations.ShouldBeEmpty();
	}
}

public sealed class PatchCreatorDateBehaviorTests
{
	private static readonly FieldInfo JValueTypeField = typeof(JValue).GetField("_valueType", BindingFlags.Instance | BindingFlags.NonPublic)!;
	private static readonly FieldInfo JValueValueField = typeof(JValue).GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic)!;
	private static readonly MethodInfo FillPatchForObjectMethod = typeof(PatchCreator).GetMethod("FillPatchForObject", BindingFlags.Static | BindingFlags.NonPublic)!;








	private static ServiceProvider CreateServices(MessagePackSerializerOptions options, bool addSupportedFormatter)
	{
		ServiceCollection services = new();
		MvcOptions mvcOptions = new();
		mvcOptions.OutputFormatters.Add(addSupportedFormatter
			? new FormatterWithOptions(options)
			: new FormatterWithOptions(options, supportsMessagePack: false));
		services.AddSingleton<IOptions<MvcOptions>>(Options.Create(mvcOptions));
		return services.BuildServiceProvider();
	}

	private static DefaultHttpContext CreateContext(string accept)
	{
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();
		context.Request.Headers.Accept = accept;
		context.Features.Set<IServiceProvidersFeature>(new RequestServicesFeature());
		return context;
	}

	private static JValue CreateArtificialDateValue(object value)
	{
		JValue result = new("seed");
		JValueTypeField.SetValue(result, JTokenType.Date);
		JValueValueField.SetValue(result, value);
		return result;
	}

	private static async IAsyncEnumerable<StreamingModel> AsyncItems(params StreamingModel[] items)
	{
		foreach (StreamingModel item in items)
		{
			await Task.Yield();
			yield return item;
		}
	}

	[MessagePackObject(AllowPrivate = true)]
	internal sealed class StreamingModel
	{
		[Key(0)]
		public int Id { get; set; }

		[Key(1)]
		public string Name { get; set; } = string.Empty;
	}

	private sealed class FormatterWithOptions : OutputFormatter
	{
		private readonly MessagePackSerializerOptions options;

		public FormatterWithOptions(MessagePackSerializerOptions options, bool supportsMessagePack = true)
		{
			this.options = options;
			SupportedMediaTypes.Add(supportsMessagePack ? "application/x-msgpack" : "application/xml");
		}

		public override Task WriteResponseBodyAsync(OutputFormatterWriteContext context) => Task.CompletedTask;
	}

	private sealed class FormatterWithoutOptions : OutputFormatter
	{
		public FormatterWithoutOptions()
		{
			SupportedMediaTypes.Add("application/x-msgpack");
		}

		public override Task WriteResponseBodyAsync(OutputFormatterWriteContext context) => Task.CompletedTask;
	}

	private sealed class RequestServicesFeature : IServiceProvidersFeature
	{
		public IServiceProvider RequestServices { get; set; } = null!;
	}

	private sealed class ConvertibleDateValue(DateTime value) : IConvertible
	{
		public TypeCode GetTypeCode() => TypeCode.Object;
		public bool ToBoolean(IFormatProvider? provider) => Convert.ToBoolean(value, provider);
		public byte ToByte(IFormatProvider? provider) => Convert.ToByte(value, provider);
		public char ToChar(IFormatProvider? provider) => Convert.ToChar(value, provider);
		public DateTime ToDateTime(IFormatProvider? provider) => value;
		public decimal ToDecimal(IFormatProvider? provider) => Convert.ToDecimal(value, provider);
		public double ToDouble(IFormatProvider? provider) => Convert.ToDouble(value, provider);
		public short ToInt16(IFormatProvider? provider) => Convert.ToInt16(value, provider);
		public int ToInt32(IFormatProvider? provider) => Convert.ToInt32(value, provider);
		public long ToInt64(IFormatProvider? provider) => Convert.ToInt64(value, provider);
		public sbyte ToSByte(IFormatProvider? provider) => Convert.ToSByte(value, provider);
		public float ToSingle(IFormatProvider? provider) => Convert.ToSingle(value, provider);
		public string ToString(IFormatProvider? provider) => value.ToString(provider);
		public object ToType(Type conversionType, IFormatProvider? provider) => Convert.ChangeType(value, conversionType, provider);
		public ushort ToUInt16(IFormatProvider? provider) => Convert.ToUInt16(value, provider);
		public uint ToUInt32(IFormatProvider? provider) => Convert.ToUInt32(value, provider);
		public ulong ToUInt64(IFormatProvider? provider) => Convert.ToUInt64(value, provider);
	}

	[Fact]
	public void PatchCreator_DateTokenWithNonDateRuntimeValue_IsIgnored()
	{
		JObject original = new(new JProperty("Moment", CreateArtificialDateValue(DateTime.Parse("2024-01-01"))));
		JObject modified = new(new JProperty("Moment", CreateArtificialDateValue(new ConvertibleDateValue(new DateTime(2024, 1, 2)))));
		JsonPatchDocument patch = new();

		FillPatchForObjectMethod.Invoke(null, [original, modified, patch, "/"]);

		patch.Operations.ShouldBeEmpty();
	}

	[Fact]
	public void PatchCreator_DateTimeOffsetPattern_HandlesEqualAndChangedOffsets()
	{
		JObject original = new(new JProperty("Moment", new JValue(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(2)))));
		JObject equal = new(new JProperty("Moment", new JValue(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(2)))));
		JObject changed = new(new JProperty("Moment", new JValue(new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.FromHours(2)))));
		JsonPatchDocument equalPatch = new();
		JsonPatchDocument changedPatch = new();

		FillPatchForObjectMethod.Invoke(null, [original, equal, equalPatch, "/"]);
		FillPatchForObjectMethod.Invoke(null, [original, changed, changedPatch, "/"]);

		equalPatch.Operations.ShouldBeEmpty();
		changedPatch.Operations.Count.ShouldBe(1);
		changedPatch.Operations[0].op.ShouldBe("replace");
		changedPatch.Operations[0].path.ShouldBe("/Moment");
	}

	[Fact]
	public void PatchCreator_DateTimeOffsetPattern_IgnoresConvertibleNonOffsetRuntimeValue()
	{
		JObject original = new(new JProperty("Moment", new JValue(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))));
		JObject modified = new(new JProperty("Moment", CreateArtificialDateValue(new ConvertibleDateValue(new DateTime(2024, 1, 2)))));
		JsonPatchDocument patch = new();

		FillPatchForObjectMethod.Invoke(null, [original, modified, patch, "/"]);

		patch.Operations.ShouldBeEmpty();
	}

}
