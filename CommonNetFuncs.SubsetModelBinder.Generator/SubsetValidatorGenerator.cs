using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CommonNetFuncs.SubsetModelBinder;

[Generator(LanguageNames.CSharp)]
public sealed class SubsetValidatorGenerator : IIncrementalGenerator
{
	private const string FullyQualifiedAttributeName = "CommonNetFuncs.SubsetModelBinder.SubsetOfAttribute";
	private const string AttributeName = "SubsetOfAttribute";

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		// All semantic work happens in the transform and produces an equatable model (no Compilation, symbols or syntax nodes cross the
		// pipeline boundary), so edits that do not change a subset class leave its diagnostics/source output cached.
		IncrementalValuesProvider<SubsetModel> models = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				FullyQualifiedAttributeName,
				predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
				transform: static (ctx, ct) => CreateModel(ctx, ct))
			.Where(static m => m is not null)!;

		context.RegisterSourceOutput(models, static (spc, model) => Emit(spc, model));
	}

	private static SubsetModel? CreateModel(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
	{
		// ForAttributeWithMetadataName only invokes the transform for nodes whose attribute binds to FullyQualifiedAttributeName,
		// and TargetSymbol == GetDeclaredSymbol(TargetNode).
		ClassDeclarationSyntax subsetClass = (ClassDeclarationSyntax)context.TargetNode;
		if (context.TargetSymbol is not INamedTypeSymbol subsetClassSymbol)
		{
			return null;
		}

		List<DiagnosticInfo> diagnostics = [];
		HashSet<string> reportedDiagnostics = [];

		// Check if the class is marked as partial
		if (!subsetClass.Modifiers.Any(SyntaxKind.PartialKeyword))
		{
			AddDiagnosticOnce(diagnostics, reportedDiagnostics, "SG0003", "Class must be partial", $"The class '{subsetClassSymbol.Name}' decorated with SubsetOf attribute must be marked as partial", subsetClass.Identifier.GetLocation());
		}

		AttributeData? subsetOfAttribute = subsetClassSymbol.GetAttributes().FirstOrDefault(a => string.Equals(a.AttributeClass?.Name, AttributeName));
		if (subsetOfAttribute == null || subsetOfAttribute.ConstructorArguments[0].Value is not INamedTypeSymbol originalTypeSymbol)
		{
			return new SubsetModel(null, null, null, false, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
		}

		bool isMvcApp = subsetOfAttribute.ConstructorArguments.Length > 1 && (bool)subsetOfAttribute.ConstructorArguments[1].Value!;
		bool allowInheritedProperties = subsetOfAttribute.ConstructorArguments.Length > 2 && (bool)subsetOfAttribute.ConstructorArguments[2].Value!;
		bool ignoreType = subsetOfAttribute.ConstructorArguments.Length > 3 && (bool)subsetOfAttribute.ConstructorArguments[3].Value!;

		// Get all properties of the original type, including inherited ones
		Dictionary<string, IPropertySymbol> originalProperties = GetAllProperties(originalTypeSymbol, allowInheritedProperties);

		foreach (IPropertySymbol subsetProperty in subsetClassSymbol.GetMembers().OfType<IPropertySymbol>())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!originalProperties.TryGetValue(subsetProperty.Name, out IPropertySymbol? originalProperty))
			{
				AddDiagnosticOnce(diagnostics, reportedDiagnostics, "SG0002", "Property not found", $"Property '{subsetProperty.Name}' is not present in the parent class '{originalTypeSymbol.Name}'{(allowInheritedProperties ? " or its base classes" : string.Empty)}",
					subsetProperty.Locations.FirstOrDefault());
			}
			else if (!ignoreType && !SymbolEqualityComparer.Default.Equals(subsetProperty.Type, originalProperty.Type))
			{
				AddDiagnosticOnce(diagnostics, reportedDiagnostics, "SG0001", "Property type mismatch", $"Property '{subsetProperty.Name}' has a different type than in the original class '{originalTypeSymbol.Name}'{(allowInheritedProperties ? " or its base classes" : string.Empty)}. Expected: {originalProperty.Type.Name}, Found: {subsetProperty.Type.Name}",
					subsetProperty.Locations.FirstOrDefault());
			}
		}

		return new SubsetModel(subsetClassSymbol.ContainingNamespace.ToDisplayString(), subsetClassSymbol.Name, originalTypeSymbol.Name, isMvcApp, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
	}

	private static void AddDiagnosticOnce(List<DiagnosticInfo> diagnostics, HashSet<string> reportedDiagnostics, string id, string title, string message, Location? location)
	{
		if (reportedDiagnostics.Add($"{id}:{location}:{message}"))
		{
			diagnostics.Add(new DiagnosticInfo(id, title, message, location));
		}
	}

	private static void Emit(SourceProductionContext context, SubsetModel model)
	{
		foreach (DiagnosticInfo diagnostic in model.Diagnostics.Items)
		{
			DiagnosticDescriptor descriptor = new(diagnostic.Id, diagnostic.Title, diagnostic.Message, nameof(SubsetValidatorGenerator), DiagnosticSeverity.Error, isEnabledByDefault: true);
			context.ReportDiagnostic(Diagnostic.Create(descriptor, diagnostic.Location ?? Location.None));
		}

		if (model.ClassName != null)
		{
			string attributeCode = GenerateAttributeCode(model.Namespace!, model.ClassName, model.OriginalTypeName!, model.IsMvcApp);
			context.AddSource($"{model.ClassName}_Attributes.g.cs", SourceText.From(attributeCode, Encoding.UTF8));
		}
	}

	private static string GenerateAttributeCode(string containingNamespace, string className, string originalTypeName, bool isMvcApp)
	{
		StringBuilder sb = new();
		sb.AppendLine("#nullable enable");
		sb.AppendLine("using System;");
		sb.AppendLine(isMvcApp ? "using Microsoft.AspNetCore.Mvc;" : "using System.ComponentModel.DataAnnotations;");
		sb.AppendLine($"namespace {containingNamespace}");
		sb.AppendLine("{");
		sb.AppendLine($"    [{(isMvcApp ? "ModelMetadataType" : "MetadataType")}(typeof({originalTypeName}))]");
		sb.AppendLine($"    public partial class {className}");
		sb.AppendLine("    {");
		sb.AppendLine("    }");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static Dictionary<string, IPropertySymbol> GetAllProperties(INamedTypeSymbol type, bool includeInheritedProperties)
	{
		Dictionary<string, IPropertySymbol> properties = [];
		INamedTypeSymbol? currentType = type;

		while (currentType != null)
		{
			foreach (IPropertySymbol? member in currentType.GetMembers().OfType<IPropertySymbol>().Where(member => !properties.ContainsKey(member.Name)))
			{
				properties[member.Name] = member;
			}

			if (!includeInheritedProperties)
			{
				break; // Stop after processing the current type if inherited properties are not allowed
			}

			currentType = currentType.BaseType;
		}

		return properties;
	}
}

/// <summary>Equatable diagnostic payload. SourceLocation equality is (SyntaxTree reference, span), and unchanged trees keep their identity across compilations.</summary>
internal sealed class DiagnosticInfo(string id, string title, string message, Location? location) : IEquatable<DiagnosticInfo>
{
	public string Id { get; } = id;

	public string Title { get; } = title;

	public string Message { get; } = message;

	public Location? Location { get; } = location;

	public bool Equals(DiagnosticInfo? other)
	{
		return other is not null && (ReferenceEquals(this, other) ||
			(string.Equals(Id, other.Id) && string.Equals(Title, other.Title) && string.Equals(Message, other.Message) && object.Equals(Location, other.Location)));
	}

	public override bool Equals(object? obj)
	{
		return Equals(obj as DiagnosticInfo);
	}

	public override int GetHashCode()
	{
		int hash = HashCombiner.Combine(Id.GetHashCode(), Title.GetHashCode());
		hash = HashCombiner.Combine(hash, Message.GetHashCode());
		return HashCombiner.Combine(hash, Location?.GetHashCode() ?? 0);
	}
}

/// <summary>Equatable per-class model: no Compilation, symbols or syntax nodes cross the incremental pipeline boundary.</summary>
internal sealed class SubsetModel(string? @namespace, string? className, string? originalTypeName, bool isMvcApp, EquatableArray<DiagnosticInfo> diagnostics) : IEquatable<SubsetModel>
{
	public string? Namespace { get; } = @namespace;

	/// <summary>Null when no source should be generated (attribute or source type could not be resolved).</summary>
	public string? ClassName { get; } = className;

	public string? OriginalTypeName { get; } = originalTypeName;

	public bool IsMvcApp { get; } = isMvcApp;

	public EquatableArray<DiagnosticInfo> Diagnostics { get; } = diagnostics;

	public bool Equals(SubsetModel? other)
	{
		return other is not null && (ReferenceEquals(this, other) ||
			(string.Equals(Namespace, other.Namespace) && string.Equals(ClassName, other.ClassName) && string.Equals(OriginalTypeName, other.OriginalTypeName) &&
			IsMvcApp == other.IsMvcApp && Diagnostics.Equals(other.Diagnostics)));
	}

	public override bool Equals(object? obj)
	{
		return Equals(obj as SubsetModel);
	}

	public override int GetHashCode()
	{
		int hash = HashCombiner.Combine(Namespace?.GetHashCode() ?? 0, ClassName?.GetHashCode() ?? 0);
		hash = HashCombiner.Combine(hash, OriginalTypeName?.GetHashCode() ?? 0);
		hash = HashCombiner.Combine(hash, IsMvcApp ? 1 : 0);
		return HashCombiner.Combine(hash, Diagnostics.GetHashCode());
	}
}

/// <summary>Array wrapper with element-wise (value) equality.</summary>
internal readonly struct EquatableArray<T>(T[] items) : IEquatable<EquatableArray<T>> where T : IEquatable<T>
{
	public T[] Items { get; } = items ?? [];

	public bool Equals(EquatableArray<T> other)
	{
		T[] left = Items ?? [];
		T[] right = other.Items ?? [];
		if (left.Length != right.Length)
		{
			return false;
		}

		EqualityComparer<T> comparer = EqualityComparer<T>.Default;
		for (int i = 0; i < left.Length; i++)
		{
			if (!comparer.Equals(left[i], right[i]))
			{
				return false;
			}
		}
		return true;
	}

	public override bool Equals(object? obj)
	{
		return obj is EquatableArray<T> other && Equals(other);
	}

	public override int GetHashCode()
	{
		int hash = 0;
		foreach (T item in Items ?? [])
		{
			hash = HashCombiner.Combine(hash, item?.GetHashCode() ?? 0);
		}
		return hash;
	}
}

// System.HashCode is not available on netstandard2.0
internal static class HashCombiner
{
	public static int Combine(int hash, int value)
	{
		return unchecked((hash * -1521134295) + value);
	}
}
