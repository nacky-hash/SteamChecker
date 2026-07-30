using System.Diagnostics;
using System.Reflection;
using Xunit;

// ---------------------------------------------------------------------------
// 依存ゼロのテストランナー。
// [Fact] / [Theory] + [InlineData] が付いた public メソッドを反射で拾って実行する。
// ---------------------------------------------------------------------------

var filter = args.FirstOrDefault(a => !a.StartsWith('-'));
var verbose = args.Contains("--verbose") || args.Contains("-v");

var assembly = Assembly.GetExecutingAssembly();
var passed = 0;
var failed = 0;
var skipped = 0;
var failures = new List<(string Name, Exception Error)>();
var stopwatch = Stopwatch.StartNew();

var testClasses = assembly.GetTypes()
    .Where(t => t is { IsClass: true, IsAbstract: false })
    .Where(t => t.GetMethods().Any(HasTestAttribute))
    .OrderBy(t => t.Name, StringComparer.Ordinal);

foreach (var type in testClasses)
{
    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(HasTestAttribute)
        .OrderBy(m => m.Name, StringComparer.Ordinal)
        .ToList();

    if (methods.Count == 0) continue;

    var printedHeader = false;

    foreach (var method in methods)
    {
        foreach (var (arguments, label) in ExpandCases(method))
        {
            var displayName = $"{type.Name}.{method.Name}{label}";

            if (filter is not null &&
                !displayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!printedHeader)
            {
                Console.WriteLine();
                Console.WriteLine($"  {type.Name}");
                printedHeader = true;
            }

            var skipReason = GetSkipReason(method);
            if (skipReason is not null)
            {
                skipped++;
                WriteLine(ConsoleColor.DarkYellow, $"    - {method.Name}{label}  (skip: {skipReason})");
                continue;
            }

            try
            {
                var instance = Activator.CreateInstance(type);
                var result = method.Invoke(instance, arguments);

                if (result is Task task) task.GetAwaiter().GetResult();

                if (instance is IDisposable disposable) disposable.Dispose();

                passed++;
                if (verbose) WriteLine(ConsoleColor.Green, $"    + {method.Name}{label}");
            }
            catch (Exception ex)
            {
                var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                failed++;
                failures.Add((displayName, actual));
                WriteLine(ConsoleColor.Red, $"    x {method.Name}{label}");
                WriteLine(ConsoleColor.DarkRed, $"        {actual.Message}");
            }
        }
    }
}

stopwatch.Stop();

Console.WriteLine();
Console.WriteLine(new string('-', 60));

if (failures.Count > 0)
{
    WriteLine(ConsoleColor.Red, "失敗したテスト:");
    foreach (var (name, error) in failures)
    {
        WriteLine(ConsoleColor.Red, $"  {name}");
        WriteLine(ConsoleColor.DarkGray, $"    {error.GetType().Name}: {error.Message}");

        if (error is not XunitAssertException && error.StackTrace is { } trace)
        {
            var line = trace.Split('\n').FirstOrDefault(l => l.Contains(".cs:line"));
            if (line is not null) WriteLine(ConsoleColor.DarkGray, $"    {line.Trim()}");
        }
    }

    Console.WriteLine();
}

var summaryColor = failed == 0 ? ConsoleColor.Green : ConsoleColor.Red;
WriteLine(summaryColor,
    $"合計 {passed + failed + skipped} 件 / 成功 {passed} / 失敗 {failed} / スキップ {skipped}  ({stopwatch.ElapsedMilliseconds} ms)");

return failed == 0 ? 0 : 1;

static bool HasTestAttribute(MethodInfo m)
    => m.GetCustomAttribute<FactAttribute>() is not null
       || m.GetCustomAttribute<TheoryAttribute>() is not null;

static string? GetSkipReason(MethodInfo m)
    => m.GetCustomAttribute<FactAttribute>()?.Skip
       ?? m.GetCustomAttribute<TheoryAttribute>()?.Skip;

static IEnumerable<(object?[] Arguments, string Label)> ExpandCases(MethodInfo method)
{
    var inlineData = method.GetCustomAttributes<InlineDataAttribute>().ToList();

    if (inlineData.Count == 0)
    {
        yield return ([], string.Empty);
        yield break;
    }

    foreach (var data in inlineData)
    {
        var label = "(" + string.Join(", ", data.Data.Select(d => d?.ToString() ?? "null")) + ")";
        yield return (CoerceArguments(method, data.Data), label);
    }
}

static object?[] CoerceArguments(MethodInfo method, object?[] data)
{
    var parameters = method.GetParameters();
    var result = new object?[data.Length];

    for (var i = 0; i < data.Length && i < parameters.Length; i++)
    {
        var target = parameters[i].ParameterType;
        var value = data[i];

        if (value is null || target.IsInstanceOfType(value))
        {
            result[i] = value;
            continue;
        }

        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        result[i] = underlying.IsEnum
            ? Enum.ToObject(underlying, value)
            : Convert.ChangeType(value, underlying);
    }

    return result;
}

static void WriteLine(ConsoleColor color, string text)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = previous;
}
