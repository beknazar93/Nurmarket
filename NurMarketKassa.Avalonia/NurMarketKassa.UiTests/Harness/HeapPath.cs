using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-07 (память владельца, Б-J1): диагностика «кто держит объект» без внешних средств. Обход кучи по полям через отражение от статических полей сборок
/// приложения, Avalonia и CoreLib (там — очередь таймеров и пула потоков); возвращает цепочку «статическое поле → … → объект». Слабые ссылки и дескрипторы не проходятся
/// (их не видно полям) — как и у сборщика, они объект не удерживают. Стеки потоков не обходятся (зависшая задача, ждущая в стеке, цепочки не даст). Вызывается ТОЛЬКО
/// когда тест уже красный (чтобы сообщение называло виновника), поэтому скорость не важна.</summary>
public static class HeapPath
{
    private sealed record Edge(object? Parent, string Label);

    private static readonly string[] RootAssemblyPrefixes = ["NurMarketKassa", "Avalonia", "System.Private.CoreLib", "Microsoft.Extensions", "CommunityToolkit", "SkiaSharp", "HarfBuzzSharp"];

    private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new();

    /// <summary>Цепочка от статического корня до <paramref name="target"/> или текст «не найдена».</summary>
    public static string Find(object target, int maxNodes = 12_000_000)
    {
        var visited = new Dictionary<object, Edge>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();

        void Push(object? value, object? parent, string label)
        {
            if (value is null || value is string || value is Type || value.GetType().IsPrimitive || value.GetType().IsPointer || visited.ContainsKey(value))
                return;
            visited[value] = new Edge(parent, label);
            queue.Enqueue(value);
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name ?? "";
            if (!RootAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                continue;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
            catch (Exception) { continue; }
            foreach (var type in types)
            {
                if (type.IsGenericTypeDefinition || type.ContainsGenericParameters)
                    continue;
                FieldInfo[] fields;
                try { fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch (Exception) { continue; }
                foreach (var f in fields)
                {
                    if (f.IsLiteral || f.FieldType.IsPointer || f.FieldType.IsByRef || (f.FieldType.IsValueType && f.FieldType.IsPrimitive)
                        || f.GetCustomAttribute<ThreadStaticAttribute>() is not null)
                        continue;
                    object? value;
                    try { value = f.GetValue(null); }
                    catch (Exception) { continue; }
                    Push(value, null, $"static {type.FullName}.{f.Name}");
                }
            }
        }

        var found = false;
        var processed = 0;
        while (queue.Count > 0 && processed < maxNodes)
        {
            var o = queue.Dequeue();
            processed++;
            if (ReferenceEquals(o, target))
            {
                found = true;
                break;
            }

            var t = o.GetType();
            if (t.Namespace is { } ns && (ns.StartsWith("System.Reflection", StringComparison.Ordinal) || ns.StartsWith("System.Runtime.Intrinsics", StringComparison.Ordinal)))
                continue;
            if (t.Name is "WeakReference" or "ConditionalWeakTable`2" or "DependentHandle")
                continue;
            if (t.IsArray)
            {
                var elem = t.GetElementType()!;
                if (elem.IsPrimitive || elem.IsPointer)
                    continue;
                if (o is Array arr)
                {
                    var i = 0;
                    foreach (var item in arr)
                    {
                        if (item is not null)
                            Push(item, o, $"[{i}]");
                        i++;
                        if (i > 2_000_000)
                            break;
                    }
                }
                continue;
            }

            foreach (var f in Fields(t))
            {
                object? value;
                try { value = f.GetValue(o); }
                catch (Exception) { continue; }
                Push(value, o, $"{f.DeclaringType?.Name}.{f.Name}");
            }
        }

        if (!found)
            return $"цепочка не найдена (обойдено {processed} объектов; держит стек потока, дескриптор или поле вне просмотренных сборок)";

        var chain = new List<string>();
        object? cur = target;
        while (cur is not null && visited.TryGetValue(cur, out var edge))
        {
            chain.Add($"{Describe(cur)}  <- {edge.Label}");
            cur = edge.Parent;
        }

        chain.Reverse();
        var sb = new StringBuilder();
        sb.AppendLine($"цепочка от статического корня ({processed} объектов обойдено):");
        foreach (var line in chain)
            sb.AppendLine("  " + line);
        return sb.ToString();
    }

    private static string Describe(object o)
    {
        var t = o.GetType();
        return t.IsArray ? $"{t.GetElementType()?.Name}[{((Array)o).Length}]" : t.FullName ?? t.Name;
    }

    private static FieldInfo[] Fields(Type t)
    {
        if (FieldCache.TryGetValue(t, out var cached))
            return cached;
        var list = new List<FieldInfo>();
        for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var ft = f.FieldType;
                if (ft.IsPointer || ft.IsByRef || ft == typeof(IntPtr) || ft == typeof(UIntPtr) || (ft.IsPrimitive) || ft.IsEnum)
                    continue;
                list.Add(f);
            }
        }

        return FieldCache[t] = list.ToArray();
    }
}
