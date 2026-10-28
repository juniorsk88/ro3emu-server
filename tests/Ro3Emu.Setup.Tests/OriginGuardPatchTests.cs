using System.Collections;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// The patch for the publisher's signed-origins guard (release 140075): a synthetic module with the same shape
/// (RO3.SealedLoginFallback.RegionalBootstrap forwarding to a runtime, and a manager that takes the address of a private
/// method of its nested coroutine class), so the test needs no game files.
/// </summary>
public class OriginGuardPatchTests
{
    private static (ModuleDefinition Module, TypeDefinition Manager, MethodDefinition Failed) Build(bool withGuard = true, bool unknownShape = false)
    {
        var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Fake.Network", new Version(1, 0)), "Fake.Network", ModuleKind.Dll);
        var module = assembly.MainModule;
        var str = module.TypeSystem.String; var obj = module.TypeSystem.Object; var vd = module.TypeSystem.Void;
        var enumerator = module.ImportReference(typeof(IEnumerator)); var action = module.ImportReference(typeof(Action));

        TypeDefinition NewType(string ns, string name, TypeAttributes attr = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed)
        {
            var t = new TypeDefinition(ns, name, attr, obj);
            module.Types.Add(t);
            return t;
        }
        MethodDefinition Static(TypeDefinition t, string name, TypeReference ret, params TypeReference[] ps)
        {
            var m = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, ret);
            for (int i = 0; i < ps.Length; i++) m.Parameters.Add(new ParameterDefinition("p" + i, ParameterAttributes.None, ps[i]));
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldstr, "original body"); il.Emit(OpCodes.Pop);
            if (ret.MetadataType != MetadataType.Void) { il.Emit(OpCodes.Ldnull); }
            il.Emit(OpCodes.Ret);
            t.Methods.Add(m);
            return m;
        }

        var manager = NewType("", "HttpNetworkManager", TypeAttributes.Public);
        var nested = new TypeDefinition("", "<Coroutine>d__31", TypeAttributes.NestedPrivate | TypeAttributes.Sealed, obj);
        manager.NestedTypes.Add(nested);
        var failed = new MethodDefinition("SealedFallback_SelectionFailed", MethodAttributes.Private | MethodAttributes.HideBySig, vd);
        failed.Body.GetILProcessor().Emit(OpCodes.Ret);
        nested.Methods.Add(failed);
        var start = new MethodDefinition("Start", MethodAttributes.Private | MethodAttributes.HideBySig, vd);
        var il2 = start.Body.GetILProcessor();
        il2.Emit(OpCodes.Ldnull); il2.Emit(OpCodes.Ldftn, failed); il2.Emit(OpCodes.Newobj, module.ImportReference(typeof(Action).GetConstructors()[0]));
        il2.Emit(OpCodes.Pop); il2.Emit(OpCodes.Ret);
        manager.Methods.Add(start);

        if (withGuard)
        {
            var ns = OriginGuardPatch.GuardNamespace;
            var bootstrap = NewType(ns, "RegionalBootstrap");
            var cctor = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, vd);
            cctor.Body.GetILProcessor().Emit(OpCodes.Ldstr, "init"); cctor.Body.GetILProcessor().Emit(OpCodes.Pop); cctor.Body.GetILProcessor().Emit(OpCodes.Ret);
            bootstrap.Methods.Add(cctor);
            Static(bootstrap, "RewriteUrl", str, str);
            Static(bootstrap, "NormalizeForGuard", str, str);
            Static(bootstrap, "WrapLogin", enumerator, enumerator, action);
            Static(bootstrap, "ConfigureLoginRequest", vd, obj);
            if (unknownShape) Static(bootstrap, "Mystery", str, module.TypeSystem.Int32);
            var runtime = NewType(ns, "SealedFallbackRuntime");
            Static(runtime, "RedactDiagnostic", str, str);
            Static(runtime, "Initialize", vd, str);   // not a Redact*: left alone
        }
        return (module, manager, failed);
    }

    private static string Ops(MethodDefinition m) => string.Join(" ", m.Body.Instructions.Select(i => i.OpCode.Name));

    [Fact]
    public void BootstrapBecomesPassThroughAndThePrivateMethodIsOpened()
    {
        var (module, manager, failed) = Build();
        var text = OriginGuardPatch.Patch(module);
        Assert.Contains("bypassed", text);

        var bootstrap = module.Types.Single(t => t.Name == "RegionalBootstrap");
        Assert.Equal("ret", Ops(bootstrap.Methods.Single(m => m.IsConstructor && m.IsStatic)));
        Assert.Equal("ldarg.0 ret", Ops(bootstrap.Methods.Single(m => m.Name == "RewriteUrl")));
        Assert.Equal("ldarg.0 ret", Ops(bootstrap.Methods.Single(m => m.Name == "NormalizeForGuard")));
        Assert.Equal("ldarg.0 ret", Ops(bootstrap.Methods.Single(m => m.Name == "WrapLogin")));
        Assert.Equal("ret", Ops(bootstrap.Methods.Single(m => m.Name == "ConfigureLoginRequest")));

        var runtime = module.Types.Single(t => t.Name == "SealedFallbackRuntime");
        Assert.Equal("ldarg.0 ret", Ops(runtime.Methods.Single(m => m.Name == "RedactDiagnostic")));
        Assert.Contains("ldstr", Ops(runtime.Methods.Single(m => m.Name == "Initialize")));

        Assert.False(failed.IsPrivate);
        Assert.True(failed.IsAssembly);

        // The patched module still writes and reads back (what the client loads).
        var stream = new MemoryStream();
        module.Assembly.Write(stream);
        stream.Position = 0;
        using var back = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { InMemory = true });
        Assert.NotNull(back.MainModule.GetType(OriginGuardPatch.GuardNamespace, "RegionalBootstrap"));
    }

    [Fact]
    public void MembersUsedFromTheirOwnTypeOrNestingStayPrivate()
    {
        var (module, manager, _) = Build();
        // A private field of the manager used by the manager's own nested type is legal and is not opened.
        var field = new FieldDefinition("owned", FieldAttributes.Private, module.TypeSystem.Int32);
        manager.Fields.Add(field);
        var nested = manager.NestedTypes[0];
        var use = new MethodDefinition("UseOuter", MethodAttributes.Private, module.TypeSystem.Void);
        var il = use.Body.GetILProcessor();
        il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Pop); il.Emit(OpCodes.Ret);
        nested.Methods.Add(use);
        OriginGuardPatch.Patch(module);
        Assert.True(field.IsPrivate);
    }

    [Fact]
    public void ClientWithoutTheGuardIsLeftAlone()
    {
        var (module, _, failed) = Build(withGuard: false);
        Assert.Equal("origin guard: not in this client", OriginGuardPatch.Patch(module));
        Assert.True(failed.IsPrivate);
    }

    [Fact]
    public void AnUnknownShapeIsRefusedNotGuessed()
    {
        var (module, _, _) = Build(unknownShape: true);
        var e = Assert.Throws<InvalidOperationException>(() => OriginGuardPatch.Patch(module));
        Assert.Contains("shape this patcher does not know", e.Message);
        var (module2, _, _) = Build(unknownShape: true);
        Assert.Throws<InvalidOperationException>(() => OriginGuardPatch.Verify(module2));
    }
}
