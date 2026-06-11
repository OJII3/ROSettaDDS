// Polyfills for types/attributes introduced in newer .NET runtimes that are
// required by C# compiler features rosettadds uses. These are declared only
// when the target framework does not already provide them, so both the
// upstream .NET 8 build and Unity (netstandard2.1) can share source.

#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>Required by the compiler to emit <c>init</c> accessors.</summary>
    internal static class IsExternalInit
    {
    }
}
#endif

#if !NET7_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// Indicates that the <c>ref</c> return / parameter escapes its defining scope
    /// (e.g. a <c>ref struct</c> method that returns a field by <c>ref</c>).
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Property,
        AllowMultiple = false,
        Inherited = false)]
    internal sealed class UnscopedRefAttribute : Attribute
    {
    }
}
#endif
