using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Runs a BenchmarkDotNet class once, the way BenchmarkDotNet would, without BenchmarkDotNet: the first
/// value of each <c>[Params]</c>, <c>[GlobalSetup]</c>, then per <c>[Benchmark]</c> its iteration setup,
/// one invocation and its iteration cleanup, and finally <c>[GlobalCleanup]</c>. Proves each scenario
/// still builds and runs; it measures nothing. Being async, it reverts any <c>Activity.Current</c> a
/// benchmark leaves behind when it returns.
/// </summary>
internal static class BenchmarkSmokeRunner
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    internal static async Task RunOnceAsync(Type benchmarkClass)
    {
        var instance = Activator.CreateInstance(benchmarkClass)!;
        ApplyFirstParams(instance);
        await InvokeTaggedAsync<GlobalSetupAttribute>(instance);
        try
        {
            foreach (var benchmark in MethodsTagged<BenchmarkAttribute>(benchmarkClass))
            {
                await InvokeTaggedAsync<IterationSetupAttribute>(instance);
                await AwaitResultAsync(Invoke(benchmark, instance));
                await InvokeTaggedAsync<IterationCleanupAttribute>(instance);
            }
        }
        finally
        {
            await InvokeTaggedAsync<GlobalCleanupAttribute>(instance);
        }
    }

    private static void ApplyFirstParams(object instance)
    {
        foreach (var property in instance.GetType().GetProperties(PublicInstance))
        {
            if (property.GetCustomAttribute<ParamsAttribute>() is { Values.Length: > 0 } parameters)
            {
                property.SetValue(instance, parameters.Values[0]);
            }
        }
    }

    private static async Task InvokeTaggedAsync<TAttribute>(object instance)
        where TAttribute : Attribute
    {
        foreach (var method in MethodsTagged<TAttribute>(instance.GetType()))
        {
            await AwaitResultAsync(Invoke(method, instance));
        }
    }

    private static async Task AwaitResultAsync(object? result)
    {
        switch (result)
        {
            case Task task:
                await task;
                break;
            case ValueTask valueTask:
                await valueTask;
                break;
            case not null when result.GetType().IsGenericType && result.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>):
                await (Task)result.GetType().GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(result, null)!;
                break;
        }
    }

    private static IEnumerable<MethodInfo> MethodsTagged<TAttribute>(Type benchmarkClass)
        where TAttribute : Attribute =>
        benchmarkClass.GetMethods(PublicInstance).Where(method => method.IsDefined(typeof(TAttribute), inherit: true));

    private static object? Invoke(MethodInfo method, object instance) =>
        method.Invoke(instance, BindingFlags.DoNotWrapExceptions, binder: null, parameters: null, culture: null);
}
