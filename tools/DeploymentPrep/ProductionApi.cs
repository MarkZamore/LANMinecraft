using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;

sealed class ProductionApi
{
    readonly Assembly assembly;
    readonly AssemblyDependencyResolver resolver;
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public ProductionApi(string path)
    {
        resolver = new AssemblyDependencyResolver(path);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var dependency = resolver.ResolveAssemblyToPath(name);
            return dependency is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency);
        };
        assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    public Type Type(string name) => assembly.GetType("Minecraft." + name, throwOnError: true)!;
    public object New(string name, params object?[] args)
    {
        var constructor = Type(name).GetConstructors(Flags).Single(method => Fits(method.GetParameters(), args));
        return Invoke(() => constructor.Invoke(args))!;
    }
    public object? Call(object instance, string name, params object?[] args) =>
        Invoke(() => instance.GetType().GetMethods(Flags).Single(method => !method.IsStatic && method.Name == name &&
            Fits(method.GetParameters(), args)).Invoke(instance, args));
    public object? Static(string type, string name, params object?[] args) =>
        Invoke(() => Type(type).GetMethods(Flags).Single(method => method.IsStatic && method.Name == name &&
            Fits(method.GetParameters(), args)).Invoke(null, args));
    public object? Get(object instance, string name) => instance.GetType().GetProperty(name, Flags)!.GetValue(instance);
    public object? StaticGet(string type, string name) => Type(type).GetProperty(name, Flags)?.GetValue(null) ??
        Type(type).GetField(name, Flags)?.GetValue(null);
    public static IEnumerable<object> Items(object value) => ((System.Collections.IEnumerable)value).Cast<object>();
    public async Task<object?> Await(object task)
    {
        await ((Task)task).ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
    public Delegate SaveDelegate(object settingsService) => Delegate.CreateDelegate(
        typeof(Action<>).MakeGenericType(Type("AppSettings")), settingsService, settingsService.GetType().GetMethod("Save")!);
    static bool Fits(ParameterInfo[] parameters, object?[] args) => parameters.Length == args.Length &&
        parameters.Select((parameter, index) => args[index] is null
            ? !parameter.ParameterType.IsValueType || Nullable.GetUnderlyingType(parameter.ParameterType) is not null
            : parameter.ParameterType.IsInstanceOfType(args[index])).All(match => match);
    static object? Invoke(Func<object?> action)
    {
        try { return action(); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
