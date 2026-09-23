using System.Reflection;
using System.Reflection.Emit;
using Argonaut.Engine.Bytes;

namespace Argonaut.Tests;

/// <summary>
/// Holds the app to the layering in CLAUDE.md ("Project structure"): Shell → Features → Ui →
/// Engine, each referencing only the layers below it, and no types loose at the root of the two
/// shared layers.
///
/// References are read from compiled code rather than from <c>using</c> lines, so a fully
/// qualified name cannot slip past: every type a member's signature mentions, and every type,
/// method or field a method body's IL touches - which also covers lambdas and async state
/// machines, since the compiler nests those inside the type that wrote them.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly App = typeof(IByteSource).Assembly;

    [Fact]
    public void Engine_ReferencesNeitherTheUiLayersNorAvalonia()
        => AssertNoReferences("Argonaut.Engine",
            ["Argonaut.Ui", "Argonaut.Features", "Argonaut.Shell", "Argonaut.Diagnostics", "Avalonia"]);

    [Fact]
    public void Ui_ReferencesNoFeatureOrShell()
        => AssertNoReferences("Argonaut.Ui", ["Argonaut.Features", "Argonaut.Shell", "Argonaut.Diagnostics"]);

    [Fact]
    public void Features_DoNotReferenceTheShell()
        => AssertNoReferences("Argonaut.Features", ["Argonaut.Shell", "Argonaut.Diagnostics"]);

    [Fact]
    public void SharedLayers_HaveNoTypesAtTheirRoot()
    {
        var loose = App.GetTypes()
            .Where(t => t.Namespace is "Argonaut.Engine" or "Argonaut.Ui")
            .Where(t => !t.IsNested)
            .Select(t => t.FullName)
            .ToList();

        Assert.True(loose.Count == 0,
            "Put these in a subfolder named for their concern: " + string.Join(", ", loose));
    }

    private static void AssertNoReferences(string layer, string[] forbidden)
    {
        var violations = new SortedSet<string>();
        foreach (var type in App.GetTypes().Where(t => InNamespace(t.Namespace, layer)))
        {
            foreach (var referenced in ReferencedTypes(type))
            {
                string? ns = referenced.Namespace;
                if (forbidden.Any(f => InNamespace(ns, f)))
                    violations.Add($"{Outermost(type).FullName} -> {referenced.FullName}");
            }
        }

        Assert.True(violations.Count == 0,
            $"{layer} may only reference layers below it:\n" + string.Join("\n", violations));
    }

    private static bool InNamespace(string? ns, string root)
        => ns is not null && (ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal));

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer)
            type = outer;
        return type;
    }

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        var found = new HashSet<Type>();
        void Add(Type? t)
        {
            if (t is null || t.IsGenericParameter || !found.Add(t))
                return;
            if (t.HasElementType)
                Add(t.GetElementType());
            if (t.IsGenericType)
                foreach (var arg in t.GetGenericArguments())
                    Add(arg);
        }

        Add(type.BaseType);
        foreach (var i in type.GetInterfaces())
            Add(i);
        foreach (var field in type.GetFields(Declared))
            Add(field.FieldType);
        foreach (var property in type.GetProperties(Declared))
            Add(property.PropertyType);
        foreach (var e in type.GetEvents(Declared))
            Add(e.EventHandlerType);

        IEnumerable<MethodBase> methods = type.GetMethods(Declared);
        methods = methods.Concat(type.GetConstructors(Declared));
        foreach (var method in methods)
        {
            if (method is MethodInfo info)
                Add(info.ReturnType);
            foreach (var parameter in method.GetParameters())
                Add(parameter.ParameterType);
            foreach (var used in TypesUsedInBody(method))
                Add(used);
        }

        return found;
    }

    private static IEnumerable<Type> TypesUsedInBody(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            yield break;

        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } d ? d.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        int pos = 0;
        while (pos < il.Length)
        {
            OpCode op = il[pos] == 0xFE ? TwoByte[il[pos + 1]] : OneByte[il[pos]];
            pos += op.Size;

            if (op.OperandType is OperandType.InlineType or OperandType.InlineMethod
                or OperandType.InlineField or OperandType.InlineTok)
            {
                int token = BitConverter.ToInt32(il, pos);
                MemberInfo? member;
                try { member = method.Module.ResolveMember(token, typeArgs, methodArgs); }
                catch (ArgumentException) { member = null; }

                if (member is Type t)
                    yield return t;
                else if (member?.DeclaringType is { } owner)
                    yield return owner;
            }

            pos += OperandSize(op.OperandType, il, pos);
        }
    }

    private static int OperandSize(OperandType type, byte[] il, int pos) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
        _ => 4,
    };

    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    static ArchitectureTests()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)op.Value);
            if (op.Size == 1)
                OneByte[value] = op;
            else
                TwoByte[value & 0xFF] = op;
        }
    }
}
