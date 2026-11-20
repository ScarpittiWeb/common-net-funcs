using CommonNetFuncs.SubsetModelBinder;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;
using static Xunit.TestContext;

namespace SubsetModelBinder.Generator.Tests;

public class SubsetValidatorGeneratorTests
{
	private const string SG0001 = "SG0001";
	private const string SG0002 = "SG0002";
	private const string CompilationText = "compilation";

	const string AttributeSource = @"
		namespace CommonNetFuncs.SubsetModelBinder
		{
			[System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
			public sealed class SubsetOfAttribute : System.Attribute
			{
				public SubsetOfAttribute(System.Type sourceType, bool isMvcApp = false, bool allowInheritedProperties = true, bool ignoreType = false) { }
			}
		}";

	[Fact]
	public static void Generator_Creates_MetadataAttribute()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		// Create an instance of our generator
		SubsetValidatorGenerator generator = new();

		// Create the driver that will inject our generator into the compilation
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Use driver to find the generated files
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		SyntaxTree generatedFile = result.GeneratedTrees[0];
		generatedFile.ToString().ShouldContain("[MetadataType(typeof(OriginalClass))]");
		generatedFile.ToString().ShouldContain("public partial class SubsetClass");
	}

	[Fact]
	public void Generator_Reports_TypeMismatch_Diagnostic()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public int Name { get; set; }  // Type mismatch here
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		Diagnostic diagnostic = result.Diagnostics[0];
		diagnostic.Id.ShouldBe(SG0001);
		diagnostic.GetMessage().ShouldContain("has a different type than in the original class");
	}

	[Fact]
	public void Generator_Reports_NonPartialClass_Diagnostic()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public class SubsetClass  // Missing partial modifier
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		Diagnostic? diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == "SG0003");
		diagnostic.ShouldNotBeNull();
		diagnostic.GetMessage().ShouldContain("must be marked as partial");
	}

	[Fact]
	public void Generator_Reports_PropertyNotFound_Diagnostic()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
					public string ExtraProperty { get; set; }  // Not in original
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		Diagnostic? diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == SG0002);
		diagnostic.ShouldNotBeNull();
		diagnostic.GetMessage().ShouldContain("is not present in the parent class");
	}

	[Fact]
	public void Generator_WithInheritedProperties_ShouldAllowBaseClassProperties()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class BaseClass
				{
					public string BaseProperty { get; set; }
				}

				public class OriginalClass : BaseClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), allowInheritedProperties: true)]
				public partial class SubsetClass
				{
					public string Name { get; set; }
					public string BaseProperty { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - No diagnostics should be reported
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithoutInheritedProperties_ShouldReportBaseClassProperties()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class BaseClass
				{
					public string BaseProperty { get; set; }
				}

				public class OriginalClass : BaseClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), allowInheritedProperties: false)]
				public partial class SubsetClass
				{
					public string Name { get; set; }
					public string BaseProperty { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		Diagnostic? diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == SG0002);
		diagnostic.ShouldNotBeNull();
		diagnostic.GetMessage().ShouldContain("BaseProperty");
	}

	[Fact]
	public void Generator_WithMvcFlag_ShouldGenerateModelMetadataType()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), isMvcApp: true)]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		SyntaxTree generatedFile = result.GeneratedTrees[0];
		string generatedCode = generatedFile.ToString();
		generatedCode.ShouldContain("using Microsoft.AspNetCore.Mvc;");
		generatedCode.ShouldContain("[ModelMetadataType(typeof(OriginalClass))]");
	}

	[Fact]
	public void Generator_WithoutMvcFlag_ShouldGenerateMetadataType()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), isMvcApp: false)]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		SyntaxTree generatedFile = result.GeneratedTrees[0];
		string generatedCode = generatedFile.ToString();
		generatedCode.ShouldContain("using System.ComponentModel.DataAnnotations;");
		generatedCode.ShouldContain("[MetadataType(typeof(OriginalClass))]");
	}

	[Fact]
	public void Generator_WithIgnoreTypeFlag_ShouldNotReportTypeMismatch()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), ignoreType: true)]
				public partial class SubsetClass
				{
					public int Name { get; set; }  // Type mismatch but ignored
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - No type mismatch diagnostic should be reported
		result.Diagnostics.Any(d => d.Id == SG0001).ShouldBeFalse();
	}

	[Fact]
	public void Generator_WithoutIgnoreTypeFlag_ShouldReportTypeMismatch()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), ignoreType: false)]
				public partial class SubsetClass
				{
					public int Name { get; set; }  // Type mismatch
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		Diagnostic? diagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == SG0001);
		diagnostic.ShouldNotBeNull();
		diagnostic.GetMessage().ShouldContain("has a different type");
	}

	[Fact]
	public void Generator_WithMultipleClasses_ShouldProcessAll()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass1
				{
					public string Name { get; set; }
				}

				public class OriginalClass2
				{
					public int Count { get; set; }
				}

				[SubsetOf(typeof(OriginalClass1))]
				public partial class SubsetClass1
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass2))]
				public partial class SubsetClass2
				{
					public int Count { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - Should generate 2 files
		result.GeneratedTrees.Length.ShouldBe(2);
		result.GeneratedTrees[0].ToString().ShouldContain("SubsetClass1");
		result.GeneratedTrees[1].ToString().ShouldContain("SubsetClass2");
	}

	[Fact]
	public void Generator_WithClassWithoutAttribute_ShouldNotProcess()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				public partial class SubsetClass  // No attribute
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - No files should be generated
		result.GeneratedTrees.Length.ShouldBe(0);
	}

	[Fact]
	public void Generator_WithEmptySubsetClass_ShouldStillGenerate()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
					public int Age { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					// Empty subset - no properties
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
		result.GeneratedTrees[0].ToString().ShouldContain("SubsetClass");
	}

	[Fact]
	public void Generator_WithComplexInheritanceChain_ShouldIncludeAllAncestorProperties()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class GrandparentClass
				{
					public string GrandparentProperty { get; set; }
				}

				public class ParentClass : GrandparentClass
				{
					public string ParentProperty { get; set; }
				}

				public class OriginalClass : ParentClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), allowInheritedProperties: true)]
				public partial class SubsetClass
				{
					public string Name { get; set; }
					public string ParentProperty { get; set; }
					public string GrandparentProperty { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - No diagnostics should be reported
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithMultipleDiagnostics_ShouldReportAll()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public int Name { get; set; }  // Type mismatch
					public string ExtraProperty { get; set; }  // Not in original
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.Diagnostics.Count(d => d.Id == SG0001).ShouldBe(1); // Type mismatch
		result.Diagnostics.Count(d => d.Id == SG0002).ShouldBe(1); // Property not found
	}

	[Fact]
	public void Generator_WithNestedNamespace_ShouldGenerateCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace.Nested.Deep
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		SyntaxTree generatedFile = result.GeneratedTrees[0];
		generatedFile.ToString().ShouldContain("namespace TestNamespace.Nested.Deep");
	}

	[Fact]
	public void Generator_WithAllFlags_ShouldRespectAllSettings()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class BaseClass
				{
					public string BaseProperty { get; set; }
				}

				public class OriginalClass : BaseClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), isMvcApp: true, allowInheritedProperties: true, ignoreType: true)]
				public partial class SubsetClass
				{
					public int Name { get; set; }  // Type mismatch but ignored
					public string BaseProperty { get; set; }  // Inherited property allowed
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.Diagnostics.ShouldBeEmpty();
		SyntaxTree generatedFile = result.GeneratedTrees[0];
		string generatedCode = generatedFile.ToString();
		generatedCode.ShouldContain("using Microsoft.AspNetCore.Mvc;");
		generatedCode.ShouldContain("[ModelMetadataType(typeof(OriginalClass))]");
	}

	[Fact]
	public void Generator_DiagnosticDeduplication_ShouldNotReportDuplicates()
	{
		// Arrange - Create a scenario that might cause duplicate diagnostics
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string ExtraProperty { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass multiple times
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - Should only have one diagnostic for ExtraProperty
		result.Diagnostics.Count(d => d.Id == SG0002 && d.GetMessage().Contains("ExtraProperty")).ShouldBe(1);
	}

	[Fact]
	public void Generator_WithNoClasses_ShouldHandleGracefully()
	{
		// Arrange - Empty source file
		const string source = @"
			namespace TestNamespace
			{
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - Should not generate any files or diagnostics
		result.GeneratedTrees.Length.ShouldBe(0);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithOtherAttributes_ShouldIgnoreThem()
	{
		// Arrange
		const string source = @"
using CommonNetFuncs.SubsetModelBinder;
using System;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[Obsolete]
				[Serializable]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - Should not process classes without SubsetOf attribute
		result.GeneratedTrees.Length.ShouldBe(0);
	}

	[Fact]
	public void Generator_WithNullOriginalType_ShouldHandleGracefully()
	{
		// Arrange - This tests the edge case where constructor argument is not a valid type
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation without SubsetOf attribute
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - Should handle gracefully without crashing
		result.GeneratedTrees.Length.ShouldBe(0);
	}

	[Fact]
	public void Generator_WithSamePropertyMultipleTimes_ShouldReportOnlyOnce()
	{
		// Arrange - Test diagnostic deduplication with same property appearing multiple times
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public int Name { get; set; }  // Type mismatch
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class AnotherSubsetClass
				{
					public int Name { get; set; }  // Same property name, same error
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get diagnostics
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - Should report one diagnostic per class
		result.Diagnostics.Count(d => d.Id == SG0001).ShouldBe(2);
	}

	[Fact]
	public void Generator_WithClassWithoutAttributeLists_ShouldBeFiltered()
	{
		// Arrange
		const string source = @"
			namespace TestNamespace
			{
				public class SimpleClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(0);
	}

	[Fact]
	public void Generator_WithDefaultParameterValues_ShouldUseDefaults()
	{
		// Arrange - Test with minimal attribute usage (only required parameter)
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - Should use default values (isMvcApp=false, allowInheritedProperties=true)
		result.Diagnostics.ShouldBeEmpty();
		SyntaxTree generatedFile = result.GeneratedTrees[0];
		string generatedCode = generatedFile.ToString();
		generatedCode.ShouldContain("using System.ComponentModel.DataAnnotations;");
		generatedCode.ShouldContain("[MetadataType(typeof(OriginalClass))]");
	}

	[Fact]
	public void Generator_WithPropertyInDifferentOrder_ShouldStillValidate()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
					public int Age { get; set; }
					public bool IsActive { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public bool IsActive { get; set; }
					public string Name { get; set; }
					public int Age { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - Order shouldn't matter
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithMultipleAttributes_ShouldIgnoreNonSubsetOfAttributes()
	{
		// Arrange - this tests the branch where attributeSymbol.ContainingType.Name != "SubsetOfAttribute"
		const string source = @"
using CommonNetFuncs.SubsetModelBinder;
using System;

			namespace TestNamespace
			{
				[AttributeUsage(AttributeTargets.Class)]
				public class CustomAttribute : Attribute { }

				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[Custom]
				[SubsetOf(typeof(OriginalClass))]
				[Obsolete]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - Should successfully generate despite multiple attributes
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithAttributeOnNonPartialClass_ShouldReportDiagnostic()
	{
		// Arrange - testing edge case with non-partial class
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - Should report partial keyword diagnostic
		result.Diagnostics.Any(d => d.GetMessage().Contains("partial")).ShouldBeTrue();
	}

	[Fact]
	public void Generator_WithNestedClass_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OuterClass
				{
					public class OriginalClass
					{
						public string Name { get; set; }
					}

					[SubsetOf(typeof(OriginalClass))]
					public partial class SubsetClass
					{
						public string Name { get; set; }
					}
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	[Fact]
	public void Generator_WithGenericClass_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass<T>
				{
					public T Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass<string>))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithAbstractClass_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public abstract class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithSealedClass_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public sealed class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithInternalClass_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				internal class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				internal partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithCompilationErrors_ShouldNotCrash()
	{
		// Arrange - test with invalid typeof() usage to trigger edge cases
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(NonExistentType))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass - should not crash
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - generator should handle gracefully (may or may not generate)
		// The key is it shouldn't crash
		result.ShouldNotBeNull();
	}

	[Fact]
	public void Generator_WithMissingAttributeArgument_ShouldHandleGracefully()
	{
		// Arrange - empty attribute arguments
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf()]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - should handle gracefully
		result.ShouldNotBeNull();
	}

	[Fact]
	public void Generator_WithPropertyHidingInheritance_ShouldHandleCorrectly()
	{
		// Arrange - test property hiding with 'new' keyword
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class BaseClass
				{
					public string Name { get; set; }
				}

				public class OriginalClass : BaseClass
				{
					public new string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithVirtualProperties_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public virtual string Name { get; set; }
					public virtual int Age { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithStaticProperties_ShouldIgnoreThem()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
					public static string StaticProperty { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
					public static string StaticProperty { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	[Fact]
	public void Generator_WithReadOnlyProperties_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
					public string ReadOnlyProperty { get; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
					public string ReadOnlyProperty { get; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithExpressionBodiedProperties_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					private string _name;
					public string Name => _name;
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					private string _name;
					public string Name => _name;
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithTypeIgnoreAndDifferentTypes_ShouldNotReportError()
	{
		// Arrange - test ignoreType with allowInheritedProperties=false combination
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class BaseClass
				{
					public string BaseProperty { get; set; }
				}

				public class OriginalClass : BaseClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), ignoreType: true, allowInheritedProperties: false)]
				public partial class SubsetClass
				{
					public int Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - no type mismatch errors due to ignoreType
		result.Diagnostics.Any(d => d.Id == SG0001).ShouldBeFalse();
	}

	[Fact]
	public void Generator_WithNullableReferenceTypes_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
#nullable enable
using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string? Name { get; set; }
					public int? Age { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string? Name { get; set; }
					public int? Age { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithDeeplyNestedInheritance_ShouldTraverseAllLevels()
	{
		// Arrange - test multiple levels of inheritance to cover BaseType traversal
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class Level1
				{
					public string Level1Property { get; set; }
				}

				public class Level2 : Level1
				{
					public string Level2Property { get; set; }
				}

				public class Level3 : Level2
				{
					public string Level3Property { get; set; }
				}

				public class OriginalClass : Level3
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), allowInheritedProperties: true)]
				public partial class SubsetClass
				{
					public string Name { get; set; }
					public string Level3Property { get; set; }
					public string Level2Property { get; set; }
					public string Level1Property { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - all properties from all levels should be found
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithNoInheritance_ShouldStopAtFirstLevel()
	{
		// Arrange - test that BaseType != null branch works correctly with no base class
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass), allowInheritedProperties: true)]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.Diagnostics.ShouldBeEmpty();
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	[Fact]
	public void Generator_WithInterfaceImplementation_ShouldOnlyCheckClassProperties()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public interface IEntity
				{
					string Name { get; set; }
				}

				public class OriginalClass : IEntity
				{
					public string Name { get; set; }
					public int Age { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass : IEntity
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation with System.Runtime reference for interfaces
		CSharpCompilation compilation = CSharpCompilation.Create(CompilationText,
			new[] { CSharpSyntaxTree.ParseText(source, cancellationToken: Current.CancellationToken), CSharpSyntaxTree.ParseText(AttributeSource, cancellationToken: Current.CancellationToken) },
			new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.Diagnostics.ShouldBeEmpty();
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	[Fact]
	public void Generator_WithRecordType_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public record OriginalClass(string Name, int Age);

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - records have properties too
		result.Diagnostics.ShouldBeEmpty();
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	[Fact]
	public void Generator_WithStructType_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public struct OriginalStruct
				{
					public string Name { get; set; }
					public int Age { get; set; }
				}

				[SubsetOf(typeof(OriginalStruct))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.Diagnostics.ShouldBeEmpty();
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	[Fact]
	public void Generator_WithMultipleAttributeLists_ShouldProcessCorrectly()
	{
		// Arrange - test with attributes in separate attribute lists
		const string source = @"
using CommonNetFuncs.SubsetModelBinder;
using System;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[Obsolete]
				[SubsetOf(typeof(OriginalClass))]
				[Serializable]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.Diagnostics.ShouldBeEmpty();
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	[Fact]
	public void Generator_WithAttributeInMiddleOfList_ShouldFind()
	{
		// Arrange - SubsetOf attribute in middle of attribute list
		const string source = @"
using CommonNetFuncs.SubsetModelBinder;
using System;

			namespace TestNamespace
			{
				[AttributeUsage(AttributeTargets.Class)]
				public class FirstAttribute : Attribute { }

				[AttributeUsage(AttributeTargets.Class)]
				public class LastAttribute : Attribute { }

				public class OriginalClass
				{
					public string Name { get; set; }
				}

				[First, SubsetOf(typeof(OriginalClass)), Last]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.Diagnostics.ShouldBeEmpty();
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	[Fact]
	public void Generator_WithEmptyClass_ShouldHandleGracefully()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - should generate even with no properties
		result.GeneratedTrees.Length.ShouldBe(1);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_WithComplexPropertyTypes_ShouldValidateCorrectly()
	{
		// Arrange
		const string source = @"
using CommonNetFuncs.SubsetModelBinder;
using System.Collections.Generic;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public List<string> Names { get; set; }
					public Dictionary<int, string> Map { get; set; }
					public int[] Numbers { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public List<string> Names { get; set; }
				}
			}";

		// Create compilation with additional references for System.Collections.Generic
		CSharpCompilation compilation = CSharpCompilation.Create(CompilationText,
			new[] { CSharpSyntaxTree.ParseText(source, cancellationToken: Current.CancellationToken), CSharpSyntaxTree.ParseText(AttributeSource, cancellationToken: Current.CancellationToken) },
			new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location)
			},
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.Diagnostics.ShouldBeEmpty();
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	[Fact]
	public void Generator_WithTypeMismatchInComplexTypes_ShouldReportError()
	{
		// Arrange
		const string source = @"
using CommonNetFuncs.SubsetModelBinder;
using System.Collections.Generic;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public List<string> Names { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public List<int> Names { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CSharpCompilation.Create(CompilationText,
			new[] { CSharpSyntaxTree.ParseText(source, cancellationToken: Current.CancellationToken), CSharpSyntaxTree.ParseText(AttributeSource, cancellationToken: Current.CancellationToken) },
			new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location) },
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - should report type mismatch (List<string> vs List<int>)
		result.Diagnostics.Any(d => d.Id == SG0001).ShouldBeTrue();
	}

	[Fact]
	public void Generator_WithPropertyOverrideKeyword_ShouldHandleCorrectly()
	{
		// Arrange
		const string source = @"
			using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class BaseClass
				{
					public virtual string Name { get; set; }
				}

				public class OriginalClass : BaseClass
				{
					public override string Name { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string Name { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert
		result.Diagnostics.ShouldBeEmpty();
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	[Fact]
	public void Generator_WithFieldsInsteadOfProperties_ShouldIgnoreFields()
	{
		// Arrange
		const string source = @"
using CommonNetFuncs.SubsetModelBinder;

			namespace TestNamespace
			{
				public class OriginalClass
				{
					public string Name;
					public string ActualProperty { get; set; }
				}

				[SubsetOf(typeof(OriginalClass))]
				public partial class SubsetClass
				{
					public string ActualProperty { get; set; }
				}
			}";

		// Create compilation
		CSharpCompilation compilation = CreateCompilation(source);

		SubsetValidatorGenerator generator = new();
		GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

		// Run the generation pass
		driver = driver.RunGenerators(compilation, Current.CancellationToken);

		// Get results
		GeneratorDriverRunResult result = driver.GetRunResult();

		// Assert - should only check properties, not fields
		result.Diagnostics.ShouldBeEmpty();
		result.GeneratedTrees.Length.ShouldBe(1);
	}

	private static CSharpCompilation CreateCompilation(string source)
	{
		return CSharpCompilation.Create(CompilationText,
			new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(AttributeSource) },
			new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
	}
}

public sealed class SubsetValidatorGeneratorBehaviorTests
{
	private static readonly Type DiagnosticInfoType = GetGeneratorType("DiagnosticInfo");
	private static readonly Type EquatableArrayDiagnosticType = GetGeneratorType("EquatableArray`1").MakeGenericType(DiagnosticInfoType);
	private static readonly Type SubsetModelType = GetGeneratorType("SubsetModel");

	private const string DefaultAttributeSource = """
		namespace CommonNetFuncs.SubsetModelBinder
		{
			[System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
			public sealed class SubsetOfAttribute : System.Attribute
			{
				public SubsetOfAttribute(System.Type sourceType, bool isMvcApp = false, bool allowInheritedProperties = true, bool ignoreType = false) { }
			}
		}
		""";









	private static GeneratorDriverRunResult Run(string modelSource, string? attributeSource = null)
	{
		MetadataReference[] references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
		SyntaxTree[] syntaxTrees =
		[
			CSharpSyntaxTree.ParseText(modelSource, path: "Model.cs", cancellationToken: Current.CancellationToken),
			CSharpSyntaxTree.ParseText(attributeSource ?? DefaultAttributeSource, path: "Attribute.cs", cancellationToken: Current.CancellationToken)
		];
		CSharpCompilation compilation = CSharpCompilation.Create(
			"GeneratorCoverage",
			syntaxTrees,
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		GeneratorDriver driver = CSharpGeneratorDriver.Create(new SubsetValidatorGenerator());
		driver = driver.RunGenerators(compilation, Current.CancellationToken);
		return driver.GetRunResult();
	}

	private static Type GetGeneratorType(string name)
		=> typeof(SubsetValidatorGenerator).Assembly.GetType($"CommonNetFuncs.SubsetModelBinder.{name}", throwOnError: true)!;

	private static object Create(Type type, params object?[] arguments)
		=> Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, arguments, null)!;

	private static object CreateArray(object? item)
		=> Create(EquatableArrayDiagnosticType, CreateDiagnosticArray(item));

	private static Array CreateDiagnosticArray(params object?[] items)
	{
		Array array = Array.CreateInstance(DiagnosticInfoType, items.Length);
		for (int i = 0; i < items.Length; i++)
		{
			array.SetValue(items[i], i);
		}
		return array;
	}

	private static bool InvokeTypedEquals(object target, object other)
		=> (bool)target.GetType().GetMethod("Equals", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, types: [target.GetType()], modifiers: null)!.Invoke(target, [other])!;

	private static bool InvokeEquals(object target, object? other)
		=> (bool)target.GetType().GetMethod("Equals", BindingFlags.Instance | BindingFlags.Public, binder: null, types: [typeof(object)], modifiers: null)!.Invoke(target, [other])!;

	private static int InvokeGetHashCode(object target)
		=> (int)target.GetType().GetMethod("GetHashCode")!.Invoke(target, null)!;

	[Theory]
	[InlineData(false, "System.ComponentModel.DataAnnotations", "MetadataType")]
	[InlineData(true, "Microsoft.AspNetCore.Mvc", "ModelMetadataType")]
	public void Generator_EmitsExactMetadataSource_ForAttributeMode(bool isMvcApp, string attributeNamespace, string attributeName)
	{
		const string modelSource = """
			using CommonNetFuncs.SubsetModelBinder;

			namespace Samples.Models
			{
				public sealed class Person
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(Person), IS_MVC_APP)]
				public partial class PersonSubset
				{
					public string Name { get; set; }
				}
			}
			""";

		GeneratorDriverRunResult result = Run(modelSource.Replace("IS_MVC_APP", isMvcApp ? "true" : "false"));
		GeneratedSourceResult generated = result.Results.Single().GeneratedSources.Single();

		string expected = string.Join(Environment.NewLine, [
			"#nullable enable",
			"using System;",
			$"using {attributeNamespace};",
			"namespace Samples.Models",
			"{",
			$"    [{attributeName}(typeof(Person))]",
			"    public partial class PersonSubset",
			"    {",
			"    }",
			"}",
			""
		]);

		generated.HintName.ShouldBe("PersonSubset_Attributes.g.cs");
		generated.SourceText.ToString().ShouldBe(expected);
		result.Diagnostics.ShouldBeEmpty();
	}

	[Fact]
	public void Generator_ReportsExactDiagnostics_WithLocations_ForPropertyFailures()
	{
		const string source = """
			using CommonNetFuncs.SubsetModelBinder;

			namespace Samples
			{
				public class BasePerson
				{
					public string BaseName { get; set; }
				}

				public class Person : BasePerson
				{
					public string Name { get; set; }
				}

				[SubsetOf(typeof(Person), allowInheritedProperties: false)]
				public partial class PersonSubset
				{
					public int Name { get; set; }
					public string Missing { get; set; }
					public string BaseName { get; set; }
				}
			}
			""";

		Diagnostic[] diagnostics = Run(source).Diagnostics.OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line).ToArray();

		diagnostics.Select(d => d.Id).ShouldBe(["SG0001", "SG0002", "SG0002"]);
		diagnostics[0].Descriptor.Title.ToString().ShouldBe("Property type mismatch");
		diagnostics[0].GetMessage().ShouldBe("Property 'Name' has a different type than in the original class 'Person'. Expected: String, Found: Int32");
		diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Error);
		diagnostics[0].Location.GetLineSpan().StartLinePosition.Line.ShouldBe(17);
		diagnostics[1].GetMessage().ShouldBe("Property 'Missing' is not present in the parent class 'Person'");
		diagnostics[2].GetMessage().ShouldBe("Property 'BaseName' is not present in the parent class 'Person'");
	}

	[Fact]
	public void Generator_ReportsPartialDiagnostic_AndStillEmitsExactPartialSource()
	{
		const string source = """
			using CommonNetFuncs.SubsetModelBinder;

			namespace Samples
			{
				public class Person { }

				[SubsetOf(typeof(Person))]
				public class PersonSubset { }
			}
			""";

		GeneratorDriverRunResult result = Run(source);
		Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();

		diagnostic.Id.ShouldBe("SG0003");
		diagnostic.Descriptor.Title.ToString().ShouldBe("Class must be partial");
		diagnostic.GetMessage().ShouldBe("The class 'PersonSubset' decorated with SubsetOf attribute must be marked as partial");
		diagnostic.Location.GetLineSpan().StartLinePosition.Line.ShouldBe(7);
		result.GeneratedTrees.ShouldHaveSingleItem().ToString().ShouldContain("public partial class PersonSubset");
	}

	[Theory]
	[InlineData("System.Type sourceType", "typeof(Person)", "MetadataType", true)]
	[InlineData("System.Type sourceType, bool isMvcApp = false", "typeof(Person), true", "ModelMetadataType", true)]
	[InlineData("System.Type sourceType, bool isMvcApp = false, bool allowInheritedProperties = true", "typeof(Person), false, false", "MetadataType", true)]
	[InlineData("System.Type sourceType, bool isMvcApp = false, bool allowInheritedProperties = true, bool ignoreType = false", "typeof(Person), false, true, true", "MetadataType", false)]
	public void Generator_HandlesEverySupportedAttributeConstructorShape(
		string constructorParameters,
		string constructorArguments,
		string expectedAttribute,
		bool inheritedPropertyDiagnostic)
	{
		string attributeSource = $$"""
			namespace CommonNetFuncs.SubsetModelBinder
			{
				[System.AttributeUsage(System.AttributeTargets.Class)]
				public sealed class SubsetOfAttribute({{constructorParameters}}) : System.Attribute
				{
				}
			}
			""";
		string modelSource = $$"""
			using CommonNetFuncs.SubsetModelBinder;

			namespace Samples
			{
				public class BasePerson { public string Id { get; set; } }
				public class Person : BasePerson { public string Name { get; set; } }

				[SubsetOf({{constructorArguments}})]
				public partial class PersonSubset
				{
					public int Name { get; set; }
					public string Id { get; set; }
				}
			}
			""";

		GeneratorDriverRunResult result = Run(modelSource, attributeSource);
		string generated = result.GeneratedTrees.ShouldHaveSingleItem().ToString();

		generated.ShouldContain($"[{expectedAttribute}(typeof(Person))]");
		result.Diagnostics.Any(d => d.Id == "SG0002").ShouldBe(inheritedPropertyDiagnostic);
	}

	[Theory]
	[InlineData("[SubsetOf(null)]")]
	[InlineData("[SubsetOf()]")]
	public void Generator_WithUnresolvableSourceType_EmitsOnlyExistingPartialDiagnostic(string attribute)
	{
		string source = $$"""
			using CommonNetFuncs.SubsetModelBinder;

			namespace Samples
			{
				{{attribute}}
				public class PersonSubset
				{
					public string Name { get; set; }
				}
			}
			""";

		GeneratorDriverRunResult result = Run(source);

		result.GeneratedTrees.ShouldBeEmpty();
		Diagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
		diagnostic.Id.ShouldBe("SG0003");
	}

	[Fact]
	public void InternalModels_EqualityAndHashing_DistinguishAllFields()
	{
		Location location = Location.Create(CSharpSyntaxTree.ParseText("class C {}", cancellationToken: Current.CancellationToken), new TextSpan(6, 1));
		Location otherLocation = Location.Create(CSharpSyntaxTree.ParseText("class D {}", cancellationToken: Current.CancellationToken), new TextSpan(6, 1));
		object diagnostic = Create(DiagnosticInfoType, "SG0001", "Title", "Message", location);
		object equalDiagnostic = Create(DiagnosticInfoType, "SG0001", "Title", "Message", location);
		object differentId = Create(DiagnosticInfoType, "SG0002", "Title", "Message", location);
		object differentTitle = Create(DiagnosticInfoType, "SG0001", "Other", "Message", location);
		object differentMessage = Create(DiagnosticInfoType, "SG0001", "Title", "Other", location);
		object differentLocation = Create(DiagnosticInfoType, "SG0001", "Title", "Message", otherLocation);
		object nullLocation = Create(DiagnosticInfoType, "SG0001", "Title", "Message", null);

		InvokeEquals(diagnostic, null).ShouldBeFalse();
		InvokeEquals(diagnostic, "not-a-diagnostic").ShouldBeFalse();
		InvokeTypedEquals(diagnostic, diagnostic).ShouldBeTrue();
		InvokeTypedEquals(diagnostic, equalDiagnostic).ShouldBeTrue();
		InvokeTypedEquals(diagnostic, differentId).ShouldBeFalse();
		InvokeTypedEquals(diagnostic, differentTitle).ShouldBeFalse();
		InvokeTypedEquals(diagnostic, differentMessage).ShouldBeFalse();
		InvokeTypedEquals(diagnostic, differentLocation).ShouldBeFalse();
		InvokeGetHashCode(diagnostic).ShouldBe(InvokeGetHashCode(equalDiagnostic));
		InvokeGetHashCode(nullLocation).ShouldNotBe(InvokeGetHashCode(diagnostic));

		object diagnostics = CreateArray(diagnostic);
		object equalDiagnostics = CreateArray(equalDiagnostic);
		object differentDiagnostics = CreateArray(differentId);
		object model = Create(SubsetModelType, "Samples", "PersonSubset", "Person", false, diagnostics);
		object equalModel = Create(SubsetModelType, "Samples", "PersonSubset", "Person", false, equalDiagnostics);

		InvokeEquals(model, null).ShouldBeFalse();
		InvokeEquals(model, "not-a-model").ShouldBeFalse();
		InvokeTypedEquals(model, model).ShouldBeTrue();
		InvokeTypedEquals(model, equalModel).ShouldBeTrue();
		InvokeTypedEquals(model, Create(SubsetModelType, null, "PersonSubset", "Person", false, diagnostics)).ShouldBeFalse();
		InvokeTypedEquals(model, Create(SubsetModelType, "Samples", null, "Person", false, diagnostics)).ShouldBeFalse();
		InvokeTypedEquals(model, Create(SubsetModelType, "Samples", "PersonSubset", null, false, diagnostics)).ShouldBeFalse();
		InvokeTypedEquals(model, Create(SubsetModelType, "Samples", "PersonSubset", "Person", true, diagnostics)).ShouldBeFalse();
		InvokeTypedEquals(model, Create(SubsetModelType, "Samples", "PersonSubset", "Person", false, differentDiagnostics)).ShouldBeFalse();
		InvokeGetHashCode(model).ShouldBe(InvokeGetHashCode(equalModel));
		InvokeGetHashCode(Create(SubsetModelType, null, null, null, true, differentDiagnostics))
			.ShouldNotBe(InvokeGetHashCode(model));
	}

	[Fact]
	public void InternalEquatableArrays_CompareElementsAndSupportDefaultAndNullElements()
	{
		object first = Create(DiagnosticInfoType, "SG0001", "Title", "Message", null);
		object equalFirst = Create(DiagnosticInfoType, "SG0001", "Title", "Message", null);
		object second = Create(DiagnosticInfoType, "SG0002", "Title", "Message", null);
		Array one = CreateDiagnosticArray(first);
		Array oneEqual = CreateDiagnosticArray(equalFirst);
		Array oneDifferent = CreateDiagnosticArray(second);
		object array = Create(EquatableArrayDiagnosticType, one);
		object equalArray = Create(EquatableArrayDiagnosticType, oneEqual);

		InvokeTypedEquals(array, equalArray).ShouldBeTrue();
		InvokeTypedEquals(array, Create(EquatableArrayDiagnosticType, CreateDiagnosticArray())).ShouldBeFalse();
		InvokeTypedEquals(array, Create(EquatableArrayDiagnosticType, oneDifferent)).ShouldBeFalse();
		InvokeEquals(array, null).ShouldBeFalse();
		InvokeEquals(array, "not-an-array").ShouldBeFalse();
		InvokeEquals(array, equalArray).ShouldBeTrue();
		InvokeGetHashCode(array).ShouldBe(InvokeGetHashCode(equalArray));

		object defaultArray = Activator.CreateInstance(EquatableArrayDiagnosticType)!;
		object emptyFromNullItems = Create(EquatableArrayDiagnosticType, new object?[] { null });
		InvokeTypedEquals(defaultArray, Create(EquatableArrayDiagnosticType, CreateDiagnosticArray())).ShouldBeTrue();
		InvokeTypedEquals(defaultArray, emptyFromNullItems).ShouldBeTrue();
		InvokeTypedEquals(emptyFromNullItems, defaultArray).ShouldBeTrue();
		InvokeTypedEquals(defaultArray, array).ShouldBeFalse();
		InvokeGetHashCode(defaultArray).ShouldBe(0);

		Array arrayWithNull = CreateDiagnosticArray(new object?[] { null });
		InvokeGetHashCode(Create(EquatableArrayDiagnosticType, arrayWithNull)).ShouldBe(0);
		InvokeTypedEquals(Create(EquatableArrayDiagnosticType, arrayWithNull), Create(EquatableArrayDiagnosticType, arrayWithNull)).ShouldBeTrue();
	}

	[Fact]
	public void HashCombiner_IsDeterministic_AndDoesNotOverflow()
	{
		Type hashCombiner = GetGeneratorType("HashCombiner");
		MethodInfo combine = hashCombiner.GetMethod("Combine", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

		((int)combine.Invoke(null, [int.MaxValue, 17])!).ShouldBe(unchecked((int.MaxValue * -1521134295) + 17));
		((int)combine.Invoke(null, [0, 0])!).ShouldBe(0);
	}

}
