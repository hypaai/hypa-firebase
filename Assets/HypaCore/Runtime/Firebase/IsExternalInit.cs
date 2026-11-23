// Unity's C# supports records but its runtime does not ship this marker type, which the compiler needs for them.
// Remove this file if a future Unity version provides it.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
