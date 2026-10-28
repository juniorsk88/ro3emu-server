using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// The publisher's release 140075 (21 Oct 2026) wraps the login and every URL of Happyengine.Network in a "sealed login
/// fallback" (namespace RO3.SealedLoginFallback): RegionalBootstrap forwards to SealedFallbackRuntime, which only talks
/// to origins from a compiled list or signed by the publisher and rewrites each URL to one of them. A client pointed at
/// another server (this project's) therefore never reaches it, and the login starts with a MethodAccessException
/// anyway (HttpNetworkManager.RecoveryOfficialSdkLoginStart takes the address of a private method of its nested
/// coroutine class, which Unity's Mono refuses). Older releases do not have the namespace: nothing is changed there.
///
/// The patch is by role, not by the obfuscated names the publisher changes between releases:
///   - every method of RegionalBootstrap becomes a pass-through (the first argument comes back, or nothing for void), and its
///     static constructor, which would initialise the signed-origins runtime, does nothing;
///   - the runtime's Redact* helpers (log redaction of hosts) become pass-throughs too, as they read that runtime;
///   - a private member that another, unrelated type of the same DLL uses becomes assembly-private (the access error).
/// A shape it does not know (a method whose result is not its first argument's type) is refused, not guessed at, so a new
/// release makes the patch fail with a clear message instead of writing a broken DLL.
/// </summary>
internal static class OriginGuardPatch
{
    internal const string GuardNamespace = "RO3.SealedLoginFallback";
    private const string BootstrapType = "RegionalBootstrap";
    private const string RuntimeType = "SealedFallbackRuntime";

    /// <summary>Applies the patch to Happyengine.Network; the text for the log (or why nothing was needed).</summary>
    public static string Patch(ModuleDefinition module)
    {
        var bootstrap = module.Types.FirstOrDefault(t => t.Namespace == GuardNamespace && t.Name == BootstrapType);
        if (bootstrap == null) return "origin guard: not in this client";

        int passed = 0;
        foreach (var method in bootstrap.Methods)
        {
            if (!method.HasBody) continue;
            if (method.IsConstructor && method.IsStatic) { Replace(method, il => il.Emit(OpCodes.Ret)); continue; }
            PassThrough(method);
            passed++;
        }

        var runtime = module.Types.FirstOrDefault(t => t.Namespace == GuardNamespace && t.Name == RuntimeType);
        int redactions = 0;
        if (runtime != null)
        {
            foreach (var method in runtime.Methods.Where(m => m.HasBody && m.IsStatic && !m.IsConstructor && m.Name.StartsWith("Redact", StringComparison.Ordinal)))
            {
                PassThrough(method);
                redactions++;
            }
        }

        int opened = OpenPrivateAccess(module);
        return $"origin guard bypassed ({passed} bootstrap + {redactions} redaction methods pass through, {opened} private member(s) opened)";
    }

    /// <summary>Whether the guard is in this module and every method of it has a shape the patch handles (for <c>check</c>).</summary>
    public static void Verify(ModuleDefinition module)
    {
        var bootstrap = module.Types.FirstOrDefault(t => t.Namespace == GuardNamespace && t.Name == BootstrapType);
        if (bootstrap == null) return;
        foreach (var method in bootstrap.Methods.Where(m => m.HasBody && !(m.IsConstructor && m.IsStatic))) Shape(method);
    }

    /// <summary>void: nothing; otherwise the first argument, which must be of the result's type.</summary>
    private static void PassThrough(MethodDefinition method)
    {
        var shape = Shape(method);
        Replace(method, il => { if (shape) il.Emit(method.IsStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1); il.Emit(OpCodes.Ret); });
    }

    /// <returns>True when the method returns its first argument.</returns>
    private static bool Shape(MethodDefinition method)
    {
        if (method.ReturnType.MetadataType == MetadataType.Void) return false;
        var first = method.Parameters.FirstOrDefault();
        if (first == null || first.ParameterType.FullName != method.ReturnType.FullName)
            throw new InvalidOperationException($"{method.DeclaringType.FullName}.{method.Name}: the origin guard has a shape this patcher does not know "
                + "(a newer client build); open an issue with .ro3-update/installed-release.txt");
        return true;
    }

    private static void Replace(MethodDefinition method, Action<ILProcessor> emit)
    {
        var body = method.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        body.InitLocals = false;
        emit(body.GetILProcessor());
    }

    /// <summary>
    /// A private method or field that a method outside its type (and its nesting) uses throws MethodAccessException /
    /// FieldAccessException in Unity's Mono; it becomes assembly-private (a nested type is reachable from the enclosing
    /// one, not the other way round). Only members of this module are touched.
    /// </summary>
    internal static int OpenPrivateAccess(ModuleDefinition module)
    {
        int opened = 0;
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case MethodReference mr when Own(mr.DeclaringType, module) && mr.Resolve() is { IsPrivate: true } target && !Reaches(type, target.DeclaringType):
                        target.IsPrivate = false; target.IsAssembly = true; opened++;
                        break;
                    case FieldReference fr when Own(fr.DeclaringType, module) && fr.Resolve() is { IsPrivate: true } field && !Reaches(type, field.DeclaringType):
                        field.IsPrivate = false; field.IsAssembly = true; opened++;
                        break;
                }
            }
        }
        return opened;
    }

    private static bool Own(TypeReference type, ModuleDefinition module) => type.Scope == module || type.Module == module && type is TypeDefinition;

    /// <summary>Whether code of <paramref name="caller"/> may use a private member of <paramref name="owner"/>: the same type, or one nested (at any depth) in it.</summary>
    private static bool Reaches(TypeDefinition caller, TypeDefinition owner)
    {
        for (var t = caller; t != null; t = t.DeclaringType) if (t == owner) return true;
        return false;
    }
}
