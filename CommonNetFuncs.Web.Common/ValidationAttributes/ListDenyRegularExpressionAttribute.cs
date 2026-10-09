using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

using static CommonNetFuncs.Core.Strings;
using static CommonNetFuncs.Core.TypeChecks;

namespace CommonNetFuncs.Web.Common.ValidationAttributes;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]

/// <summary>
/// Validates that all items in a list do NOT match the specified regular expression pattern.
/// Validation fails if any item matches the pattern.
/// </summary>
public sealed class ListDenyRegularExpressionAttribute : ValidationAttribute
{
	/// <summary>
	///	Gets or sets the timeout to use when matching the regular expression pattern (in milliseconds)
	///	(-1 means never timeout).
	/// </summary>
	public int MatchTimeoutInMilliseconds { get; set; }

	/// <summary>
	/// Gets the timeout to use when matching the regular expression pattern
	/// </summary>
	public TimeSpan MatchTimeout => TimeSpan.FromMilliseconds(MatchTimeoutInMilliseconds);

	/// <summary>
	/// Gets the regular expression pattern to use
	/// </summary>
	public string Pattern { get; }

	/// <summary>
	/// Validation mode: if true, only deny full matches of the pattern; if false, deny any match within the string
	/// </summary>
	public bool DenyOnlyFullMatch { get; set; }

	private Regex? Regex { get; set; }

	/// <summary>
	/// Constructor that accepts the regular expression pattern
	/// </summary>
	/// <param name="pattern">The regular expression pattern to deny. Items matching this pattern will fail validation.</param>
	/// <param name="denyOnlyFullMatch">If true, only deny items that fully match the pattern; if false, deny items containing any match.</param>
	public ListDenyRegularExpressionAttribute([StringSyntax(StringSyntaxAttribute.Regex)] string pattern, bool denyOnlyFullMatch = false)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		Pattern = pattern;
		DenyOnlyFullMatch = denyOnlyFullMatch;
		MatchTimeoutInMilliseconds = 2000;
		ErrorMessage = "Item at index {0} '{1}' must not match the pattern '{2}'.";
	}

	protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
	{
		SetupRegex();

		return ValidationAttributeHelpers.ValidateStringList(value, validationContext, nameof(ListDenyRegularExpressionAttribute), ValidateEnumerable);
	}

	private ValidationResult? ValidateEnumerable(IEnumerable<string?> values, string memberName)
	{
		int index = 0;
		foreach (string? item in values)
		{
			// Null / empty passes automatically. Denies any match in the string, or only a match of the entire string when DenyOnlyFullMatch is set
			if (!string.IsNullOrEmpty(item) && (DenyOnlyFullMatch ? ValidationAttributeHelpers.HasFullMatch(Regex!, item) : Regex!.IsMatch(item)))
			{
				return new ValidationResult(
					string.Format(CultureInfo.CurrentCulture, ErrorMessageString, index, item.UrlEncodeReadable(), Pattern),
					[memberName]
				);
			}
			index++;
		}
		return null;
	}

	/// <summary>
	///	Override of <see cref="ValidationAttribute.FormatErrorMessage" />
	/// </summary>
	/// <remarks>This override provides a formatted error message describing the pattern</remarks>
	/// <param name="name">The user-visible name to include in the formatted message.</param>
	/// <returns>The localized message to present to the user</returns>
	/// <exception cref="InvalidOperationException"> is thrown if the current attribute is ill-formed.</exception>
	/// <exception cref="ArgumentException"> is thrown if the <see cref="Pattern" /> is not a valid regular expression.</exception>
	public override string FormatErrorMessage(string name)
	{
		SetupRegex();

		return string.Format(CultureInfo.CurrentCulture, ErrorMessageString, "{index}", "{value}", Pattern);
	}

	/// <summary>
	/// Sets up the <see cref="Regex" /> property from the <see cref="Pattern" /> property.
	/// </summary>
	/// <exception cref="ArgumentException"> is thrown if the current <see cref="Pattern" /> cannot be parsed</exception>
	/// <exception cref="InvalidOperationException"> is thrown if the current attribute is ill-formed.</exception>
	/// <exception cref="ArgumentOutOfRangeException"> thrown if <see cref="MatchTimeoutInMilliseconds" /> is negative (except -1),
	/// zero or greater than approximately 24 days </exception>
	[MemberNotNull(nameof(Regex))]
	private void SetupRegex()
	{
		Regex ??= ValidationAttributeHelpers.CreateRegex(Pattern, MatchTimeoutInMilliseconds);
	}
}
