// .NET Framework lacks System.Runtime.CompilerServices.IsExternalInit, which the
// compiler requires to consume (and declare) C# 'init' accessors.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}

// .NET Framework also lacks ModuleInitializerAttribute (used by TestSetup).
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class ModuleInitializerAttribute : Attribute
{
}
