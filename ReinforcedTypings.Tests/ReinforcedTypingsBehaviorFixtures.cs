using CommonNetFuncs.ReinforcedTypings.Collections;
using CommonNetFuncs.ReinforcedTypings.Constants;
using Reinforced.Typings.Attributes;

[TsConst]
public static class GlobalCoverageConsts
{
	public const string Value = "global";
}

namespace ReinforcedTypings.Tests
{
	namespace TestModels.Coverage
	{
		[TsConst]
		public static class RichCoverageConsts
		{
			public static readonly bool Disabled = false;

			public static readonly DateTimeOffset UpdatedAt = new(2025, 2, 3, 4, 5, 6, TimeSpan.Zero);

			public static readonly ComplexCoverageValue Complex = new();
		}

		public sealed class ComplexCoverageValue
		{
			public string Included { get; set; } = "yes";

			[TsIgnore]
			public string Ignored { get; set; } = "no";

			public string this[int index]
			{
				get => index.ToString();
			}

			public string WriteOnly
			{
				set { }
			}
		}

		[TsCollection]
		public static class CollectionShapeCoverage
		{
			public static readonly IDictionary<string, int> GenericDictionary = new Dictionary<string, int>();

			public static readonly IEnumerable<int> ExactEnumerable = [1];

			public static readonly List<int?> NullableValues = [1, null];

			public static readonly SimpleColor[] UnexportedEnumValues = [SimpleColor.Green];
		}

		[TsCollection]
		public static class NamespacedCoverageCollections
		{
			public static readonly string[] Values = ["one"];
		}

		[TsInterface]
		public sealed class ParentReference;

		[TsCollection]
		public static class ParentReferenceCollection
		{
			public static readonly ParentReference[] Values = [new()];
		}
	}

	namespace TestModels.Coverage.Child
	{
		[TsCollection]
		public static class ChildReferenceCollection
		{
			public static readonly global::ReinforcedTypings.Tests.TestModels.Coverage.ParentReference[] Values = [new()];
		}
	}

	namespace TestModels.PathCase.Upper
	{
		public sealed class ParentCaseModel;
	}

	namespace TestModels.PathCase.UPPER
	{
		public sealed class ChildCaseModel
		{
			public global::ReinforcedTypings.Tests.TestModels.PathCase.Upper.ParentCaseModel? Parent { get; set; }
		}
	}

	namespace TestModels.Coverage.Attributes
	{
		[AttributeUsage(AttributeTargets.Property)]
		public sealed class DenyCharactersAttribute(string characters) : Attribute
		{
			public string DeniedCharacters { get; } = characters;
		}
	}

	namespace TestModels.Coverage.Generic
	{
		public readonly struct GenericValue<T>
		{
		}

		public sealed class GenericValueModel
		{
			public GenericValue<int> Value { get; set; }
		}
	}

	namespace TestModels.Coverage.Attributes
	{
		[AttributeUsage(AttributeTargets.Property)]
		public sealed class AllowedNullableValuesAttribute(params object[] values) : Attribute
		{
			public object[] AllowedValues { get; } = values;
		}

		public sealed class NonAttributeCoverageModel
		{
			public string Value { get; set; } = string.Empty;
		}

		[AttributeUsage(AttributeTargets.Class)]
		public sealed class SubsetOfAttribute : Attribute
		{
			public object? SourceType => null;
		}

		[SubsetOf]
		public sealed class MalformedSubsetModel
		{
			public string Value { get; set; } = string.Empty;
		}
	}

}
