using CommonNetFuncs.Core;
using System.ComponentModel.DataAnnotations;
using System.Data;

namespace Core.Tests;

public sealed class ValidationTests
{
	public sealed class TestModel
	{
		[Required]
		public string? RequiredString { get; set; }

		[StringLength(5)]
		public string? MaxLengthString { get; set; }

		[Range(1, 10)]
		public int RangeNumber { get; set; }

		public string? UnvalidatedProperty { get; set; }
	}

	[Fact]
	public void SetInvalidPropertiesToDefault_NullObject_ReturnsNull()
	{
		// Arrange
		TestModel? model = null;

		// Act
		TestModel? result = model.SetInvalidPropertiesToDefault();

		// Assert
		result.ShouldBeNull();
	}

	[Fact]
	public void SetInvalidPropertiesToDefault_ValidObject_ReturnsUnchangedObject()
	{
		// Arrange
		TestModel model = new()
		{
			RequiredString = "Test",
			MaxLengthString = "Test",
			RangeNumber = 5,
			UnvalidatedProperty = "Test"
		};

		// Act
		TestModel result = model.SetInvalidPropertiesToDefault();

		// Assert
		result.RequiredString.ShouldBe("Test");
		result.MaxLengthString.ShouldBe("Test");
		result.RangeNumber.ShouldBe(5);
		result.UnvalidatedProperty.ShouldBe("Test");
	}

	[Theory]
	[InlineData(null, "Valid", 5)] // Required string violation
	[InlineData("Valid", "TooLongString", 5)] // MaxLength violation
	[InlineData("Valid", "Valid", 20)] // Range violation
	public void SetInvalidPropertiesToDefault_InvalidProperties_ResetsInvalidProperties(string? requiredString, string? maxLengthString, int rangeNumber)
	{
		// Arrange
		TestModel model = new()
		{
			RequiredString = requiredString,
			MaxLengthString = maxLengthString,
			RangeNumber = rangeNumber,
			UnvalidatedProperty = "Test"
		};

		// Act
		TestModel result = model.SetInvalidPropertiesToDefault();

		// Assert
		if (requiredString is null)
		{
			result.RequiredString.ShouldBeNull();
		}

		if (maxLengthString == "TooLongString")
		{
			result.MaxLengthString.ShouldBeNull();
		}

		if (rangeNumber == 20)
		{
			result.RangeNumber.ShouldBe(0);
		}

		result.UnvalidatedProperty.ShouldBe("Test"); // Unvalidated property should remain unchanged
	}

	[Fact]
	public void SetInvalidPropertiesToDefault_MultipleValidationFailures_ResetsAllInvalidProperties()
	{
		// Arrange
		TestModel model = new()
		{
			RequiredString = null,
			MaxLengthString = "ThisStringIsTooLong",
			RangeNumber = 100,
			UnvalidatedProperty = "Test"
		};

		// Act
		TestModel result = model.SetInvalidPropertiesToDefault();

		// Assert
		result.RequiredString.ShouldBeNull();
		result.MaxLengthString.ShouldBeNull();
		result.RangeNumber.ShouldBe(0);
		result.UnvalidatedProperty.ShouldBe("Test");
	}

	[Fact]
	public void SetInvalidPropertiesToDefault_ValidateAllFalse_OnlyValidatesRequiredAttributes()
	{
		// Arrange
		TestModel model = new()
		{
			RequiredString = null, // Required validation will still occur
			MaxLengthString = "ThisStringIsTooLong", // This validation will be skipped
			RangeNumber = 100, // This validation will be skipped
			UnvalidatedProperty = "Test"
		};

		// Act
		TestModel result = model.SetInvalidPropertiesToDefault(validateAll: false);

		// Assert
		result.RequiredString.ShouldBeNull();
		result.MaxLengthString.ShouldBe("ThisStringIsTooLong"); // Should remain unchanged
		result.RangeNumber.ShouldBe(100); // Should remain unchanged
		result.UnvalidatedProperty.ShouldBe("Test");
	}
}

public sealed class ValidationBehaviorTests
{























	private static DataTable CreateOneRowTable()
	{
		DataTable table = new();
		table.Columns.Add("Value", typeof(int));
		table.Rows.Add(9);
		return table;
	}

	[GapNullTypeId]
	private sealed class GapNullTypeIdHolder
	{
	}

	[AttributeUsage(AttributeTargets.Class)]
	private sealed class GapNullTypeIdAttribute : Attribute
	{
		public override object TypeId => null!;
	}

	[GapNamelessTypeId]
	private sealed class GapNamelessTypeIdHolder
	{
	}

	[AttributeUsage(AttributeTargets.Class)]
	private sealed class GapNamelessTypeIdAttribute : Attribute
	{
		public override object TypeId { get; } = new object();
	}

	private sealed class GapNestedEquality
	{
		public GapNestedValue? Child { get; set; }
	}

	private sealed class GapNestedValue
	{
		public int Value { get; set; }
	}

	private sealed class GapHashHolder
	{
		public List<GapNestedValue?> Values { get; set; } = [];
	}

	private sealed class GapNonSeekableReadStream(byte[] bytes) : MemoryStream(bytes)
	{
		public bool AllowRead { get; set; } = true;

		public override bool CanRead => AllowRead;

		public override bool CanSeek => false;
	}

	private sealed class GapNonWritableTargetStream : MemoryStream
	{
		public override bool CanWrite => false;
	}

	private sealed class GapUnseekableSourceStream(byte[] bytes) : MemoryStream(bytes)
	{
		public override bool CanSeek => false;
	}

	private sealed class GapNonSeekableWriteStream : MemoryStream
	{
		public override bool CanSeek => false;

		public byte[] Data => ToArray();
	}

	private sealed class GapValidated
	{
		[Required]
		public string? Required { get; set; }

		[Range(1, 10)]
		public int Count { get; set; }

		public string? Optional { get; set; }
	}

	[Fact]
	public void SetInvalidPropertiesToDefault_RespectsValidateAllAndValidObjects()
	{
		GapValidated value = new() { Required = null, Count = -1 };

		value.SetInvalidPropertiesToDefault(validateAll: true);
		value.Required.ShouldBeNull();
		value.Count.ShouldBe(0);

		GapValidated valid = new() { Required = "value", Count = 2 };
		valid.SetInvalidPropertiesToDefault(validateAll: false).ShouldBeSameAs(valid);
	}

}
