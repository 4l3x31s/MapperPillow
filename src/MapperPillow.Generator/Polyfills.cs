// netstandard2.0 lacks IsExternalInit, which `record` / `init` setters require.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
