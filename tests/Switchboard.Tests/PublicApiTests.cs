// Public API snapshot: PublicApi.approved.txt, next to this file, lists every public and protected type
// and member of Switchboard, and this test fails with a line diff when the library no longer matches it,
// so every change to the surface MediatR users migrate against is a reviewed decision. To accept an
// intended change, run `SWITCHBOARD_ACCEPT_API=1 dotnet test` (or copy the PublicApi.received.txt written
// on failure over the approved file), then commit PublicApi.approved.txt and note the change in CHANGELOG.md.
// One file serves all target frameworks: the description is identical on each.
// Not rendered: nullability inside constraints and base types, `T?` on a type parameter that may be null
// anyway (reflection reports it the same as `T`), and attributes other than those listed below.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Switchboard.Tests;

public sealed class PublicApiTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const string CompilerServices = "System.Runtime.CompilerServices.";
    private static readonly string[] RenderedAttributes = ["System.ObsoleteAttribute", "System.FlagsAttribute", "System.Diagnostics.CodeAnalysis.ExperimentalAttribute",
        "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute", "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute"];
    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void", [typeof(object)] = "object", [typeof(string)] = "string", [typeof(bool)] = "bool", [typeof(char)] = "char",
        [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint",
        [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(nint)] = "nint", [typeof(nuint)] = "nuint", [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal",
    };
    // Other operators keep their metadata name (`operator op_Addition`), which is just as exact.
    private static readonly Dictionary<string, string> Operators = new()
        { ["op_Equality"] = "==", ["op_Inequality"] = "!=", ["op_LessThan"] = "<", ["op_GreaterThan"] = ">", ["op_LessThanOrEqual"] = "<=", ["op_GreaterThanOrEqual"] = ">=" };

    private readonly NullabilityInfoContext _nullability = new();

    [Fact]
    public void Public_api_matches_the_approved_snapshot()
    {
        var directory = TestProjectDirectory();
        var approvedPath = Path.Combine(directory, "PublicApi.approved.txt");
        var receivedPath = Path.Combine(directory, "PublicApi.received.txt");
        var received = Describe(typeof(Mediator).Assembly);
        var approved = File.Exists(approvedPath) ? File.ReadAllText(approvedPath).Replace("\r\n", "\n") : "";
        if (received == approved) { File.Delete(receivedPath); return; }

        if (Environment.GetEnvironmentVariable("SWITCHBOARD_ACCEPT_API") == "1") { WriteAtomically(approvedPath, received); return; }
        WriteAtomically(receivedPath, received);
        Assert.Fail($"The public API of Switchboard no longer matches {approvedPath}.\n" + Diff(approved, received) +
            "\nIf this change is intended, accept it and commit the updated file:\n    SWITCHBOARD_ACCEPT_API=1 dotnet test\n" +
            $"or copy {receivedPath} over it. Note in CHANGELOG.md whether it breaks callers or implementers.");
    }

    [Fact]
    public void Public_api_description_is_stable_across_runs() =>
        Assert.Equal(Describe(typeof(Mediator).Assembly), new PublicApiTests().Describe(typeof(Mediator).Assembly));

    private string Describe(Assembly assembly) => string.Join("\n", assembly.GetTypes()
        .Where(t => IsVisible(t) && !t.IsDefined(typeof(CompilerGeneratedAttribute)))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .Select(t => Header(t) + "\n" + string.Concat(Members(t).Select(m => "    " + m + "\n"))));

    private static bool IsVisible(Type t) => t.IsPublic || ((t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem) && IsVisible(t.DeclaringType!));

    private static string? Visibility(MethodBase? m) => m == null ? null : m.IsPublic ? "public" : m.IsFamilyOrAssembly ? "protected internal" : m.IsFamily ? "protected" : null;

    private string Header(Type t)
    {
        var visibility = t.IsPublic || t.IsNestedPublic ? "public" : t.IsNestedFamORAssem ? "protected internal" : "protected";
        var generics = t.IsGenericTypeDefinition ? t.GetGenericArguments().Where(p => p.DeclaringType == t).ToArray() : [];
        if (t.IsSubclassOf(typeof(MulticastDelegate)))
        {
            var invoke = t.GetMethod("Invoke")!;
            return $"{Attributes(t)}{visibility} delegate {Name(invoke.ReturnType, _nullability.Create(invoke.ReturnParameter))} {Name(t, declaration: true)}({Parameters(invoke)}){Constraints(generics)}";
        }
        var kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType
            ? (t.IsDefined(typeof(IsReadOnlyAttribute)) ? "readonly " : "") + (t.IsByRefLike ? "ref " : "") + (t.GetMethod("PrintMembers", Declared)?.IsDefined(typeof(CompilerGeneratedAttribute)) == true ? "record struct" : "struct")
            : (t.IsAbstract && t.IsSealed ? "static " : t.IsAbstract ? "abstract " : t.IsSealed ? "sealed " : "") + (t.GetMethod("<Clone>$") != null ? "record" : "class");
        var bases = t.IsEnum ? [Name(Enum.GetUnderlyingType(t))] : t.IsValueType || t.BaseType is null || t.BaseType == typeof(object) ? new List<string>() : [Name(t.BaseType)];
        bases.AddRange(t.GetInterfaces().Where(i => IsVisible(i) && t.BaseType?.GetInterfaces().Contains(i) != true).Select(i => Name(i)).Order(StringComparer.Ordinal));
        return $"{Attributes(t)}{visibility} {kind} {Name(t, declaration: true)}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : "")}{Constraints(generics)}";
    }

    private IEnumerable<string> Members(Type t)
    {
        if (t.IsSubclassOf(typeof(MulticastDelegate))) return [];
        var members = t.GetMembers(Declared).Where(m => !m.IsDefined(typeof(CompilerGeneratedAttribute)) && !m.Name.Contains('<')).Select(m => m switch
        {
            FieldInfo f when t.IsEnum => f.IsStatic ? (0, f.Name, $"{Attributes(f)}{f.Name} = {Literal(f.GetRawConstantValue(), Enum.GetUnderlyingType(t))}") : default,
            FieldInfo f when f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly => (0, f.Name, Field(f)),
            ConstructorInfo c when Visibility(c) is { } v && !c.IsStatic => (1, c.Name, $"{Attributes(c)}{v} {t.Name.Split('`')[0]}({Parameters(c)})"),
            PropertyInfo p when Property(p) is { } s => (2, p.Name, s),
            EventInfo e when Visibility(e.AddMethod) is { } v => (3, e.Name, $"{Attributes(e)}{v} {Modifiers(e.AddMethod!)}event {Name(e.EventHandlerType!, _nullability.Create(e))} {e.Name}"),
            MethodInfo mi when Visibility(mi) is { } v && (!mi.IsSpecialName || mi.Name.StartsWith("op_", StringComparison.Ordinal)) => (4, mi.Name, Method(mi, v)),
            _ => default,
        });
        return members.Where(m => m.Item3 != null).OrderBy(m => m.Item1).ThenBy(m => m.Item2, StringComparer.Ordinal).ThenBy(m => m.Item3, StringComparer.Ordinal).Select(m => m.Item3);
    }

    private string Field(FieldInfo f)
    {
        // A decimal constant compiles to a static readonly field carrying its value in [DecimalConstant].
        var constant = f.IsLiteral ? f.GetRawConstantValue() : f.GetCustomAttribute<DecimalConstantAttribute>()?.Value;
        var modifiers = constant != null || f.IsLiteral ? "const " : (f.IsStatic ? "static " : "") + (f.IsInitOnly ? "readonly " : "");
        var visibility = f.IsPublic ? "public " : f.IsFamily ? "protected " : "protected internal ";
        return $"{Attributes(f)}{visibility}{modifiers}{Name(f.FieldType, _nullability.Create(f))} {f.Name}{(modifiers == "const " ? " = " + Literal(constant, f.FieldType) : "")}";
    }

    private string Method(MethodInfo m, string visibility)
    {
        var returns = Name(m.ReturnType, _nullability.Create(m.ReturnParameter));
        var name = m.Name is "op_Implicit" or "op_Explicit" ? $"{m.Name[3..].ToLowerInvariant()} operator {returns}"
            : m.IsSpecialName && m.Name.StartsWith("op_", StringComparison.Ordinal) ? $"{returns} operator {Operators.GetValueOrDefault(m.Name, m.Name)}" : $"{returns} {m.Name}";
        var generics = m.IsGenericMethodDefinition ? m.GetGenericArguments() : [];
        var typeParameters = generics.Length > 0 ? "<" + string.Join(", ", generics.Select(g => g.Name)) + ">" : "";
        return $"{Attributes(m)}{visibility} {Modifiers(m)}{name}{typeParameters}({Parameters(m)}){Constraints(generics)}";
    }

    private string? Property(PropertyInfo p)
    {
        var accessors = new[] { (Method: p.GetMethod, Keyword: "get"), (Method: p.SetMethod, Keyword: p.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit") == true ? "init" : "set") }
            .Where(a => Visibility(a.Method) != null).ToArray();
        if (accessors.Length == 0) return null;
        var main = accessors.MaxBy(a => Array.IndexOf(["protected", "protected internal", "public"], Visibility(a.Method))).Method!;
        var name = p.GetIndexParameters().Length > 0 ? $"this[{string.Join(", ", p.GetIndexParameters().Select(Parameter))}]" : p.Name;
        var body = string.Join(" ", accessors.Select(a => (Visibility(a.Method) == Visibility(main) ? "" : Visibility(a.Method) + " ") + a.Keyword + ";"));
        return $"{Attributes(p)}{Visibility(main)} {(p.IsDefined(typeof(RequiredMemberAttribute)) ? "required " : "")}{Modifiers(main)}{Name(p.PropertyType, _nullability.Create(p))} {name} {{ {body} }}";
    }

    private static string Modifiers(MethodInfo m)
    {
        var isOverride = m.GetBaseDefinition().DeclaringType != m.DeclaringType;
        var modifier = m.DeclaringType!.IsInterface ? (m.IsAbstract ? (m.IsStatic ? "abstract" : "") : m.IsVirtual ? "virtual" : "")
            : m.IsAbstract ? (isOverride ? "abstract override" : "abstract")
            : !m.IsVirtual ? "" : isOverride ? (m.IsFinal ? "sealed override" : "override") : m.IsFinal ? "" : "virtual";
        return (m.IsStatic ? "static " : "") + (modifier.Length > 0 ? modifier + " " : "");
    }

    private string Parameters(MethodBase m) => string.Join(", ", m.GetParameters().Select(Parameter));

    private string Parameter(ParameterInfo p)
    {
        var modifier = p.Position == 0 && p.Member.IsDefined(typeof(ExtensionAttribute)) ? "this " : "";
        if (p.IsDefined(typeof(ParamArrayAttribute)) || HasAttribute(p.CustomAttributes, CompilerServices + "ParamCollectionAttribute")) modifier += "params ";
        if (p.ParameterType.IsByRef) modifier += p.IsOut ? "out " : HasAttribute(p.CustomAttributes, CompilerServices + "IsReadOnlyAttribute") ? "in "
            : HasAttribute(p.CustomAttributes, CompilerServices + "RequiresLocationAttribute") ? "ref readonly " : "ref ";
        var hasDefault = p.HasDefaultValue && p.DefaultValue is not (DBNull or Missing);
        return $"{modifier}{Name(p.ParameterType, _nullability.Create(p))} {p.Name}{(hasDefault ? " = " + Literal(p.DefaultValue, p.ParameterType) : "")}";
    }

    private static string Constraints(Type[] generics) => string.Concat(generics.Select(p =>
    {
        var flags = p.GenericParameterAttributes;
        var types = p.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).Select(c => Name(c)).Order(StringComparer.Ordinal);
        var nullable = NullableFlag(p);
        var parts = new List<string>();
        if (flags.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)) parts.Add(HasAttribute(p.CustomAttributes, CompilerServices + "IsUnmanagedAttribute") ? "unmanaged" : "struct");
        else if (flags.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)) parts.Add(nullable == 2 ? "class?" : "class");
        else if (nullable == 1 && !types.Any()) parts.Add("notnull");
        parts.AddRange(types);
        if (flags.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) && !flags.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)) parts.Add("new()");
        if (((int)flags & 0x20) != 0) parts.Add("allows ref struct"); // GenericParameterAttributes.AllowByRefLike, .NET 9+
        return parts.Count == 0 ? "" : $" where {p.Name} : {string.Join(", ", parts)}";
    }));

    // The compiler's own annotation of a type parameter: 1 = not null (notnull, class), 2 = maybe null, 0 = oblivious.
    private static byte NullableFlag(Type genericParameter)
    {
        static byte? Read(IEnumerable<CustomAttributeData> attributes, string name) =>
            attributes.FirstOrDefault(a => a.AttributeType.FullName == CompilerServices + name)?.ConstructorArguments[0].Value as byte?;
        var flag = Read(genericParameter.CustomAttributes, "NullableAttribute") ?? (genericParameter.DeclaringMethod is { } m ? Read(m.CustomAttributes, "NullableContextAttribute") : null);
        for (var t = genericParameter.DeclaringType; flag == null && t != null; t = t.DeclaringType) flag = Read(t.CustomAttributes, "NullableContextAttribute");
        return flag ?? 0;
    }

    private static string Name(Type t, NullabilityInfo? n = null, bool declaration = false)
    {
        if (t.IsByRef) return Name(t.GetElementType()!, n);
        // An unannotated T that may be null is reported as nullable, so only mark type parameters that exclude null.
        var mark = n is { ReadState: NullabilityState.Nullable } or { WriteState: NullabilityState.Nullable } && !t.IsValueType && (!t.IsGenericParameter || NullableFlag(t) == 1) ? "?" : "";
        if (t.IsArray) return Name(t.GetElementType()!, n?.ElementType) + "[" + new string(',', t.GetArrayRank() - 1) + "]" + mark;
        if (t.IsPointer) return Name(t.GetElementType()!) + "*";
        if (t.IsGenericParameter) return (declaration ? t.GenericParameterAttributes.HasFlag(GenericParameterAttributes.Covariant) ? "out " : t.GenericParameterAttributes.HasFlag(GenericParameterAttributes.Contravariant) ? "in " : "" : "") + t.Name + mark;
        if (Nullable.GetUnderlyingType(t) is { } underlying) return Name(underlying, n) + "?"; // n already describes the underlying type's arguments
        if (Keywords.TryGetValue(t, out var keyword)) return keyword + mark;
        return Qualified(t, t.GetGenericArguments().Select((a, i) => Name(a, n?.GenericTypeArguments.ElementAtOrDefault(i), declaration)).ToArray()) + mark;
    }

    // A nested type's generic arguments start with its declaring type's; each level shows only its own.
    private static string Qualified(Type t, string[] arguments)
    {
        var inherited = t.DeclaringType?.GetGenericArguments().Length ?? 0;
        var name = t.Name.Split('`')[0] + (arguments.Length > inherited ? "<" + string.Join(", ", arguments[inherited..]) + ">" : "");
        var prefix = t.DeclaringType is { } outer ? Qualified(outer, arguments[..inherited]) : t.Namespace ?? "";
        return prefix.Length > 0 ? prefix + "." + name : name;
    }

    private static string Literal(object? value, Type type)
    {
        if (type.IsByRef) type = type.GetElementType()!;
        if (value is null) return type.IsValueType && Nullable.GetUnderlyingType(type) == null ? "default" : "null";
        if (type.IsEnum) return Enum.IsDefined(type, Enum.ToObject(type, value)) ? $"{Name(type)}.{Enum.GetName(type, value)}"
            : $"({Name(type)}){Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture)}";
        return value switch
        {
            string s => "\"" + string.Concat(s.Select(Escape)) + "\"",
            char c => "'" + Escape(c) + "'",
            bool b => b ? "true" : "false",
            Type t => $"typeof({Name(t)})",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), // numbers; round-trippable since .NET Core 3.0
            _ => value.ToString()!,
        };
        static string Escape(char c) => c switch { '"' => "\\\"", '\'' => "\\'", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ when char.IsControl(c) => $"\\u{(int)c:X4}", _ => c.ToString() };
    }

    private static bool HasAttribute(IEnumerable<CustomAttributeData> attributes, string fullName) => attributes.Any(a => a.AttributeType.FullName == fullName);

    private static string Attributes(MemberInfo member) => string.Concat(member.CustomAttributes
        .Where(a => RenderedAttributes.Contains(a.AttributeType.FullName))
        .Select(a => (Name: a.AttributeType.FullName![..^"Attribute".Length], Arguments: string.Join(", ", a.ConstructorArguments.Select(arg => Literal(arg.Value, arg.ArgumentType))
            .Concat(a.NamedArguments.Select(arg => $"{arg.MemberName} = {Literal(arg.TypedValue.Value, arg.TypedValue.ArgumentType)}")))))
        // The compiler marks constructors of types with `required` members [Obsolete] for old compilers; that one is noise.
        .Where(a => !a.Arguments.StartsWith("\"Constructors of types with required members", StringComparison.Ordinal))
        .Select(a => a.Arguments.Length > 0 ? $"[{a.Name}({a.Arguments})] " : $"[{a.Name}] ")
        .Order(StringComparer.Ordinal));

    // Lines added and removed (a longest-common-subsequence diff), each under the type it belongs to.
    private static string Diff(string approved, string received)
    {
        string[] a = approved.Split('\n'), b = received.Split('\n');
        var common = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                common[i, j] = a[i] == b[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
        static string? TypeOf(string line, string? current) => line.Length > 0 && line[0] != ' ' ? line : current;
        var output = new StringBuilder();
        string? oldType = null, newType = null, shownType = null;
        int additions = 0, removals = 0;
        for (int i = 0, j = 0; i < a.Length || j < b.Length;)
        {
            if (i < a.Length && j < b.Length && a[i] == b[j]) { oldType = TypeOf(a[i++], oldType); newType = TypeOf(b[j++], newType); continue; }
            var removed = i < a.Length && (j == b.Length || common[i + 1, j] >= common[i, j + 1]);
            var line = removed ? a[i++] : b[j++];
            var type = removed ? oldType = TypeOf(line, oldType) : newType = TypeOf(line, newType);
            if (line.Length == 0) continue;
            if (line[0] == ' ' && type != shownType) output.Append("\n  in ").Append(type).Append('\n');
            shownType = type;
            output.Append(removed ? "  - " : "  + ").Append(line.TrimStart()).Append('\n');
            if (removed) removals++; else additions++;
        }
        return additions + removals == 0 ? "Only blank lines differ.\n" : $"{additions} line(s) added (+), {removals} removed (-):\n{output}";
    }

    private static string TestProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            foreach (var candidate in new[] { directory.FullName, Path.Combine(directory.FullName, "tests", "Switchboard.Tests") })
                if (File.Exists(Path.Combine(candidate, "Switchboard.Tests.csproj"))) return candidate;
        throw new InvalidOperationException($"Could not find tests/Switchboard.Tests above {AppContext.BaseDirectory}.");
    }

    // Each target framework runs this test in its own process at the same time, so never leave a half-written file.
    private static void WriteAtomically(string path, string contents)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }
}
