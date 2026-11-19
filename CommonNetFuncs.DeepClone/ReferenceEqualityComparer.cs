using System.Runtime.CompilerServices;

namespace CommonNetFuncs.DeepClone;

/// <summary>
/// Reference equality comparer for deep clone operations.
/// </summary>
internal sealed class ReferenceEqualityComparer : EqualityComparer<object>
{
	/// <summary>
	/// Shared stateless instance.
	/// </summary>
	public static readonly ReferenceEqualityComparer Instance = new();

	/// <summary>
	/// Determines whether the specified objects are the same instance.
	/// </summary>
	/// <param name="obj1">The first object to compare.</param>
	/// <param name="obj2">The second object to compare.</param>
	/// <returns><see langword="true"/> if <paramref name="obj1"/> and <paramref name="obj2"/> refer to the same instance otherwise, <see langword="false"/>.</returns>
	public override bool Equals(object? obj1, object? obj2)
	{
		return ReferenceEquals(obj1, obj2);
	}

	/// <summary>
	/// Returns an identity-based hash code for the specified object (never invokes an overridden <see cref="object.GetHashCode"/>).
	/// </summary>
	/// <param name="obj">The object for which to generate a hash code. Can be <see langword="null"/>.</param>
	/// <returns>The identity hash code of the specified object, or 0 if <paramref name="obj"/> is <see langword="null"/>.</returns>
	public override int GetHashCode(object obj)
	{
		// Identity hash keeps the comparer consistent with ReferenceEquals and avoids deep record / boxed struct hashing
		return RuntimeHelpers.GetHashCode(obj);
	}
}
