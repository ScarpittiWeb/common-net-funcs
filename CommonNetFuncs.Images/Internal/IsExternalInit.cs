#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill required by the compiler to allow use of <c>init</c> accessors and positional <c>record</c> types on
/// netstandard2.1, where this marker type is missing from the BCL (it is built into the native net11.0 build).
/// </summary>
internal static class IsExternalInit;
#endif
