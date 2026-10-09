using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using static CommonNetFuncs.Core.TypeChecks;

namespace CommonNetFuncs.Web.Common.ValidationAttributes;

internal static class ValidationAttributeHelpers
{
	// Compile the regex for better performance when used multiple times
	internal static Regex CreateRegex(string pattern, int matchTimeoutInMilliseconds)
	{
		if (string.IsNullOrEmpty(pattern))
		{
			throw new InvalidOperationException("Regex pattern cannot be null or empty");
		}

		return matchTimeoutInMilliseconds == -1 ? new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(5)) : new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(matchTimeoutInMilliseconds));
	}

	// We are looking for an exact match, not just a search hit. This matches what the RegularExpressionValidator control does
	internal static bool HasFullMatch(Regex regex, string value)
	{
		foreach (ValueMatch m in regex.EnumerateMatches(value))
		{
			if (m.Index == 0 && m.Length == value.Length)
			{
				return true;
			}
		}
		return false;
	}

	internal static ValidationResult? ValidateStringList(object? value, ValidationContext validationContext, string attributeName,
		Func<IEnumerable<string?>, string, ValidationResult?> validateEnumerable)
	{
		if (value is null)
		{
			return ValidationResult.Success;
		}

		string memberName = validationContext.MemberName ?? string.Empty;

		// Handle different types of collections
		if (value is IEnumerable<string?> enumerable)
		{
			return validateEnumerable(enumerable, memberName) ?? ValidationResult.Success;
		}
		else if (value.GetType().IsEnumerable())
		{
			return validateEnumerable(((IEnumerable)value).Cast<object?>().Select(x => Convert.ToString(x, CultureInfo.CurrentCulture)), memberName) ?? ValidationResult.Success;
		}
		throw new InvalidDataException($"${attributeName} can only be used on properties that implement IEnumerable");
	}
}
