using System.Diagnostics;
using System.Reflection;

namespace Iris.Tests;

/// <summary>Marks a test: a public parameterless method returning void or Task.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class TestAttribute : Attribute;

/// <summary>Skips a test whose precondition is missing (e.g. a model only CI downloads).</summary>
internal sealed class SkipException(string reason) : Exception(reason);

internal sealed class AssertionException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{what ?? "value"}: expected <{expected}>, actual <{actual}>");
        }
    }

    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? what = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
        {
            throw new AssertionException($"{what ?? "sequence"}: expected [{string.Join(", ", e)}], actual [{string.Join(", ", a)}]");
        }
    }

    public static void Near(double expected, double actual, double tolerance, string? what = null)
    {
        if (!(Math.Abs(expected - actual) <= tolerance))
        {
            throw new AssertionException($"{what ?? "value"}: expected {expected} ± {tolerance}, actual {actual}");
        }
    }

    public static void Throws<TException>(Action action, string what)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new AssertionException($"{what}: expected {typeof(TException).Name}");
    }
}

/// <summary>Runs every [Test] of this assembly (arguments filter by name); exit code 1 when a test fails or none runs.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(m => args.Length == 0 || args.Any(a => $"{m.DeclaringType!.Name}.{m.Name}".Contains(a, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();
        int passed = 0, failed = 0, skipped = 0;
        var total = Stopwatch.StartNew();
        foreach (var test in tests)
        {
            var name = $"{test.DeclaringType!.Name}.{test.Name}";
            var watch = Stopwatch.StartNew();
            try
            {
                var instance = test.IsStatic ? null : Activator.CreateInstance(test.DeclaringType!);
                if (test.Invoke(instance, null) is Task task)
                {
                    await task;
                }

                passed++;
                Console.WriteLine($"PASS  {name} ({watch.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
                if (inner is SkipException skip)
                {
                    skipped++;
                    Console.WriteLine($"SKIP  {name}: {skip.Message}");
                }
                else
                {
                    failed++;
                    Console.WriteLine($"FAIL  {name}: {(inner is AssertionException ? inner.Message : inner.ToString())}");
                }
            }
        }

        Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped in {total.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 && tests.Count > 0 ? 0 : 1;
    }
}
