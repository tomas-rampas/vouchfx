// Vouchfx.Engine.Compilation — RoslynScriptCompiler (§5).
// Implements the compile-once / run-N-times memory model mandated by §5.
//
// INVARIANTS (non-negotiable, see CLAUDE.md §"Memory model"):
//   • CSharpScript.EvaluateAsync  — FORBIDDEN (leaks an uncollectable assembly per call).
//   • CSharpScript.RunAsync       — FORBIDDEN (same leak; wrapper around EvaluateAsync).
//   • Script<T>.RunAsync          — FORBIDDEN (same leak).
//   • Script<T>.EvaluateAsync     — FORBIDDEN (same leak).
//
// Correct path: CSharpScript.Create<T> → GetCompilation() → Emit(MemoryStream) ONCE
//               → per-run: new CollectibleScriptLoadContext → LoadFromStream → <Factory> → Unload().
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Engine.Compilation;

/// <summary>
/// Compiles a CSX source string into a reusable <see cref="CompiledScript"/> (compile-once)
/// and executes it in a fresh collectible <see cref="AssemblyLoadContext"/> per run so the
/// emitted assembly can be fully unloaded after each execution.
/// </summary>
/// <remarks>
/// <para>
/// This class is the central proof-of-concept for vouchfx's memory model (§5).
/// The defining risk is that naïvely using <c>CSharpScript.EvaluateAsync</c> leaks one
/// uncollectable <see cref="System.Reflection.Assembly"/> per call.  This implementation
/// proves that risk is solved:
/// <list type="number">
///   <item><description>
///     <see cref="CompileOnce"/> calls <c>CSharpScript.Create&lt;object&gt;</c> and
///     <c>GetCompilation().Emit(MemoryStream)</c> <em>exactly once</em>, producing a
///     portable-executable byte array (<see cref="CompiledScript.Image"/>).
///   </description></item>
///   <item><description>
///     <see cref="RunIsolatedAsync"/> loads those bytes into a <em>new</em>
///     <see cref="CollectibleScriptLoadContext"/> per call, invokes the Roslyn-generated
///     <c>&lt;Factory&gt;</c> method via reflection, and always calls
///     <see cref="AssemblyLoadContext.Unload"/> in a <c>finally</c> block — even on
///     exception.
///   </description></item>
///   <item><description>
///     Because <see cref="CollectibleScriptLoadContext.Load"/> returns
///     <see langword="null"/>, every dependency resolves against the Default context.
///     Only the tiny emitted submission assembly lives in the collectible context,
///     which is what enables <c>Unload()</c> to reclaim it.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="CompileOnce"/> is not thread-safe (it is expected
/// to be called once at startup).  <see cref="RunIsolatedAsync"/> is safe to call
/// concurrently from multiple threads; each invocation owns its own ALC and
/// <see cref="ScriptGlobalVariables"/> instance.
/// </para>
/// </remarks>
public static class RoslynScriptCompiler
{
    /// <summary>
    /// Compiles <paramref name="csxSource"/> into a portable-executable image exactly once.
    /// </summary>
    /// <param name="csxSource">
    /// The C# script body.  The script's global variables type is fixed to
    /// <see cref="ScriptGlobalVariables"/>; the script body may access its public
    /// members directly (e.g. <c>Vars["key"]</c>).
    /// </param>
    /// <param name="additionalOptions">
    /// Optional extra <see cref="ScriptOptions"/> to merge (e.g. additional assembly
    /// references, imports).  When <see langword="null"/>, only the default references
    /// required to resolve <see cref="ScriptGlobalVariables"/> are used.
    /// </param>
    /// <param name="additionalReferencePaths">
    /// Optional list of absolute paths to assemblies that should be added as
    /// <see cref="Microsoft.CodeAnalysis.MetadataReference"/> entries at compile time.
    /// Use this when the CSX body references types from provider or satellite assemblies
    /// that are not already on the Roslyn reference list.  At runtime, those assemblies
    /// must be made available either via the Default context or via
    /// <c>collectibleProbingPaths</c> on <see cref="RunIsolatedAsync"/>.
    /// </param>
    /// <returns>
    /// A <see cref="CompiledScript"/> whose <see cref="CompiledScript.Image"/> contains
    /// the emitted PE bytes.  The image is safe to store and pass to
    /// <see cref="RunIsolatedAsync"/> any number of times.
    /// </returns>
    /// <exception cref="ScriptCompilationException">
    /// Thrown when Roslyn reports one or more error-level diagnostics during emit.
    /// The <see cref="ScriptCompilationException.Diagnostics"/> property carries the
    /// full list for structured rendering.
    /// </exception>
    public static CompiledScript CompileOnce(
        string csxSource,
        ScriptOptions? additionalOptions = null,
        IReadOnlyList<string>? additionalReferencePaths = null)
    {
        ArgumentNullException.ThrowIfNull(csxSource);

        // Build ScriptOptions that include references for:
        //   • mscorlib / System.Private.CoreLib  (object, Task, …)
        //   • Vouchfx.Engine.Abstractions       (ScriptGlobalVariables)
        //   • any caller-supplied additionalReferencePaths
        // Additional caller-supplied options are merged on top.
        var options = BuildBaseOptions(additionalOptions, additionalReferencePaths);

        // CSharpScript.Create<object> — compile-time initialisation only.
        // NEVER call RunAsync / EvaluateAsync on this script or its state.
        var script = CSharpScript.Create<object>(
            csxSource,
            options,
            globalsType: typeof(ScriptGlobalVariables));

        var compilation = script.GetCompilation();

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        if (!emitResult.Success)
        {
            throw new ScriptCompilationException(emitResult.Diagnostics);
        }

        return new CompiledScript(ms.ToArray());
    }

    /// <summary>
    /// Loads <paramref name="compiled"/>'s image into a fresh collectible
    /// <see cref="AssemblyLoadContext"/>, invokes the emitted
    /// <c>&lt;Factory&gt;</c> delegate against <paramref name="globals"/>, awaits the
    /// result, and unconditionally unloads the context.
    /// </summary>
    /// <param name="compiled">
    /// The compiled image produced by <see cref="CompileOnce"/>.
    /// </param>
    /// <param name="globals">
    /// Per-run state.  The script body may read and mutate <see cref="ScriptGlobalVariables.Vars"/>;
    /// mutations are visible to the caller after the method returns.
    /// </param>
    /// <param name="runLabel">
    /// An optional human-readable label embedded in the ALC name for diagnostics
    /// (e.g. a step-id or iteration counter).
    /// </param>
    /// <param name="collectibleProbingPaths">
    /// Optional list of absolute paths to satellite assemblies that the script depends
    /// on at runtime and that should be loaded into the per-run collectible
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> so they unload with it.
    /// Assemblies whose simple name is already present in the Default context are
    /// automatically excluded (pin-avoidance rule — see
    /// <see cref="CollectibleScriptLoadContext"/>).
    /// Pass <see langword="null"/> when the script's only runtime dependencies are
    /// already in the Default context.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagated into the awaited script task.
    /// </param>
    /// <returns>
    /// The return value of the script expression, or <see langword="null"/> when the
    /// script body has no explicit return value.
    /// </returns>
    public static async Task<object?> RunIsolatedAsync(
        CompiledScript compiled,
        ScriptGlobalVariables globals,
        string runLabel = "run",
        IReadOnlyList<string>? collectibleProbingPaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(globals);

        var alc = new CollectibleScriptLoadContext($"script-{runLabel}", collectibleProbingPaths);
        try
        {
            var asm = LoadImage(alc, compiled.Image);
            return await InvokeFactoryAsync(asm, globals, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Always unload — even on exception.  This is the only path that
            // returns memory to baseline.
            alc.Unload();
        }
    }

    /// <summary>
    /// Loads <paramref name="compiled"/>'s image into a single new collectible
    /// <see cref="AssemblyLoadContext"/>, resolves the emitted
    /// <c>&lt;Factory&gt;</c> method, and binds it to a typed delegate — all exactly
    /// once.  The returned <see cref="LoadedScript"/> handle may then be used to invoke
    /// the script body any number of times with zero per-call reflection overhead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "load-once / invoke-N / unload-once" counterpart to
    /// <see cref="RunIsolatedAsync"/>'s "new-ALC-per-call" model:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="RunIsolatedAsync"/> — full per-scenario isolation; pays the
    ///     <c>LoadFromStream</c> + reflection cost once per invocation.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Load"/> + <see cref="LoadedScript.InvokeAsync"/> — optimal for
    ///     high-iteration load tests and the S01-B-03 memory harness where a single
    ///     scenario body must execute thousands of times inside one lifecycle.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The caller <b>must</b> dispose the returned <see cref="LoadedScript"/> when
    /// finished; <see cref="LoadedScript.Dispose"/> calls
    /// <see cref="AssemblyLoadContext.Unload"/> exactly once and nulls all delegate
    /// references so the collectible context can be fully reclaimed by the GC.
    /// </para>
    /// </remarks>
    /// <param name="compiled">
    /// The image produced by <see cref="CompileOnce"/>; its bytes are loaded
    /// into a fresh collectible ALC exactly once.
    /// </param>
    /// <param name="collectibleProbingPaths">
    /// Optional list of absolute paths to satellite assemblies that the script depends
    /// on at runtime and that should be loaded into the single collectible
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> so they unload when the
    /// <see cref="LoadedScript"/> is disposed.  Assemblies whose simple name is already
    /// present in the Default context are automatically excluded (pin-avoidance rule).
    /// Pass <see langword="null"/> when no satellite probing is required.
    /// </param>
    /// <returns>
    /// A <see cref="LoadedScript"/> that is ready for repeated invocation.
    /// </returns>
    public static LoadedScript Load(
        CompiledScript compiled,
        IReadOnlyList<string>? collectibleProbingPaths = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);

        var alc = new CollectibleScriptLoadContext("script-loaded", collectibleProbingPaths);
        try
        {
            var asm = LoadImage(alc, compiled.Image);

            var submissionType = asm.GetType(CompiledScript.SubmissionTypeName)
                ?? throw new InvalidOperationException(
                    $"Expected type '{CompiledScript.SubmissionTypeName}' was not found in the emitted assembly. " +
                    "This indicates a Roslyn scripting API change — verify against the pinned version (4.14.0).");

            var factoryMethod = submissionType.GetMethod(
                CompiledScript.FactoryMethodName,
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    $"Expected method '{CompiledScript.FactoryMethodName}' was not found on type " +
                    $"'{CompiledScript.SubmissionTypeName}'. " +
                    "This indicates a Roslyn scripting API change — verify against the pinned version (4.14.0).");

            // Bind the factory to a typed delegate ONCE so InvokeAsync pays no
            // per-call reflection cost.  The emitted factory signature is:
            //   public static Task<object> <Factory>(object[] submissionArray)
            // because CSharpScript.Create<object> is used.  At runtime,
            // Func<object?[], Task<object?>> has the same representation.
            var factory = (Func<object?[], Task<object?>>)
                factoryMethod.CreateDelegate(typeof(Func<object?[], Task<object?>>));

            return new LoadedScript(alc, factory);
        }
        catch
        {
            // If delegate construction fails, unload the ALC to avoid leaking it.
            alc.Unload();
            throw;
        }
    }

    /// <summary>
    /// Compiles an <see cref="AssembledScript"/> produced by
    /// <see cref="CsxAssembler.Assemble(IReadOnlyList{StepCompilePlan})"/> into a <see cref="CompiledScenario"/>
    /// that pairs the emitted image with the originating step identifiers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a thin convenience wrapper over <see cref="CompileOnce"/>; it adds
    /// no new pipeline logic.  Use it when the caller wants to carry the step-id
    /// list alongside the compiled image without maintaining a separate variable.
    /// If you already have a <see cref="CompiledScript"/> from a prior
    /// <see cref="CompileOnce"/> call, construct <see cref="CompiledScenario"/>
    /// directly rather than recompiling.
    /// </para>
    /// </remarks>
    /// <param name="assembled">
    /// The assembled script produced by <see cref="CsxAssembler.Assemble(IReadOnlyList{StepCompilePlan})"/>.
    /// </param>
    /// <param name="additionalReferencePaths">
    /// Optional list of absolute paths to assemblies that should be added as
    /// Roslyn metadata references at compile time.  See
    /// <see cref="CompileOnce"/> for full semantics.
    /// </param>
    /// <returns>
    /// A <see cref="CompiledScenario"/> whose <see cref="CompiledScenario.Compiled"/>
    /// image is ready for <see cref="RunIsolatedAsync"/> and whose
    /// <see cref="CompiledScenario.StepIds"/> mirrors
    /// <see cref="AssembledScript.StepIds"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="assembled"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ScriptCompilationException">
    /// Thrown when Roslyn reports one or more error-level diagnostics during emit.
    /// </exception>
    public static CompiledScenario CompileScenario(
        AssembledScript assembled,
        IReadOnlyList<string>? additionalReferencePaths = null)
    {
        ArgumentNullException.ThrowIfNull(assembled);

        var compiled = CompileOnce(assembled.CsxSource, null, additionalReferencePaths);
        return new CompiledScenario(compiled, assembled.StepIds);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static ScriptOptions BuildBaseOptions(
        ScriptOptions? additional,
        IReadOnlyList<string>? additionalReferencePaths = null)
    {
        // Resolve BCL assembly paths from the trusted-platform-assemblies (TPA) list.
        // This is safe under single-file publish where Assembly.Location returns ""
        // (which would cause Path.GetDirectoryName to return null and NRE downstream).
        var tpaRefs = BuildTpaReferences();

        // Vouchfx.Engine.Abstractions must be referenced so the script body can
        // access ScriptGlobalVariables members.  We resolve it via its assembly
        // object, which is always loaded in the Default context.
        var abstractionsLocation = typeof(ScriptGlobalVariables).Assembly.Location;
        if (string.IsNullOrEmpty(abstractionsLocation))
            throw new NotSupportedException(
                "Could not resolve the assembly location for Vouchfx.Engine.Abstractions. " +
                "This is unexpected under normal .NET 8 load contexts; ensure the assembly " +
                "is not loaded from a single-file bundle without extraction.");

        var refs = new List<Microsoft.CodeAnalysis.MetadataReference>(tpaRefs)
        {
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(abstractionsLocation),
        };

        // Add caller-supplied per-script reference paths (e.g. provider or satellite
        // assemblies referenced by the CSX body at compile time).  At runtime those
        // assemblies are made available either via the Default context or via the
        // collectibleProbingPaths parameter on RunIsolatedAsync / Load.
        if (additionalReferencePaths is not null)
        {
            foreach (var path in additionalReferencePaths)
            {
                if (!string.IsNullOrEmpty(path))
                    refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
            }
        }

        var baseOptions = ScriptOptions.Default.WithReferences(refs);

        return additional is null
            ? baseOptions
            : baseOptions.AddReferences(additional.MetadataReferences)
                         .AddImports(additional.Imports);
    }

    /// <summary>
    /// Builds Roslyn metadata references from the runtime's trusted-platform-assemblies
    /// (TPA) list, which is populated correctly even under single-file publish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under single-file publish <c>Assembly.Location</c> returns an empty string for
    /// bundled assemblies and <c>Path.GetDirectoryName("")!</c> yields <c>""</c> or
    /// throws, neither of which produces valid paths.  The TPA list, exposed via
    /// <c>AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")</c>, always contains
    /// absolute paths to the actual BCL files on disk (they are extracted to the temp
    /// directory on first run).
    /// </para>
    /// <para>
    /// We include only the subset of TPA entries that Roslyn needs to compile a simple
    /// script body: <c>System.Private.CoreLib</c>, <c>System.Runtime</c>, and
    /// <c>System.Collections</c>.  The filter uses simple file-name matching so the
    /// method is not sensitive to the exact runtime directory layout.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// Thrown when the TPA list is unavailable or when none of the required BCL
    /// assemblies can be found in it.
    /// </exception>
    private static List<Microsoft.CodeAnalysis.MetadataReference> BuildTpaReferences()
    {
        var tpaData = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(tpaData))
            throw new NotSupportedException(
                "The TRUSTED_PLATFORM_ASSEMBLIES AppContext data is unavailable. " +
                "This property is set by the .NET runtime for all standard hosting models. " +
                "If you are running in a custom host, populate it before calling CompileOnce.");

        // Split on the platform path separator (';' on Windows, ':' on Unix).
        var tpaPaths = tpaData.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // Names we must find (case-insensitive for cross-platform safety).
        // System.Text.RegularExpressions.dll is required because the Substitute_Helpers
        // CSX class (S04-B-03) uses fully-qualified Regex types, and that assembly is a
        // separate facade in .NET 8 that Roslyn does not pull in via System.Runtime alone.
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Text.RegularExpressions.dll",
        };

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in tpaPaths)
        {
            var fileName = Path.GetFileName(path);
            if (required.Contains(fileName) && !found.ContainsKey(fileName))
                found[fileName] = path;
        }

        var missing = required.Except(found.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            throw new NotSupportedException(
                $"The following required BCL assemblies could not be found in the " +
                $"TRUSTED_PLATFORM_ASSEMBLIES list: {string.Join(", ", missing)}. " +
                "Ensure the .NET 8 runtime is installed and the TPA list is complete.");

        var refs = new List<Microsoft.CodeAnalysis.MetadataReference>(found.Count);
        foreach (var path in found.Values)
            refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));

        return refs;
    }

    private static Assembly LoadImage(AssemblyLoadContext alc, byte[] image)
    {
        using var ms = new MemoryStream(image, writable: false);
        return alc.LoadFromStream(ms);
    }

    private static async Task<object?> InvokeFactoryAsync(
        Assembly submissionAssembly,
        ScriptGlobalVariables globals,
        CancellationToken cancellationToken)
    {
        // Roslyn emits a type named "Submission#0" with a public static method:
        //   public static Task<T> <Factory>(object[] submissionArray)
        // submissionArray[0] = globals instance
        // submissionArray[1] = previous submission result (null for the first/only submission)
        //
        // This convention was verified against Microsoft.CodeAnalysis.CSharp.Scripting 4.14.0
        // during the Sprint-1 PoC.
        //
        // We bind to a typed delegate ONCE per RunIsolatedAsync call (one reflection
        // look-up per isolated run) so there is no per-call MethodInfo.Invoke overhead
        // and no ContinueWith / reflection-based Result extraction.  The delegate is
        // scoped to this method frame; it is not held beyond the await, so the JIT can
        // collect it freely after this method returns.
        var submissionType = submissionAssembly.GetType(CompiledScript.SubmissionTypeName)
            ?? throw new InvalidOperationException(
                $"Expected type '{CompiledScript.SubmissionTypeName}' was not found in the emitted assembly. " +
                "This indicates a Roslyn scripting API change — verify against the pinned version (4.14.0).");

        var factoryMethod = submissionType.GetMethod(
            CompiledScript.FactoryMethodName,
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"Expected method '{CompiledScript.FactoryMethodName}' was not found on type " +
                $"'{CompiledScript.SubmissionTypeName}'. " +
                "This indicates a Roslyn scripting API change — verify against the pinned version (4.14.0).");

        // Bind once; no per-call reflection overhead from here on.
        var factory = (Func<object?[], Task<object?>>)
            factoryMethod.CreateDelegate(typeof(Func<object?[], Task<object?>>));

        cancellationToken.ThrowIfCancellationRequested();

        var submissionArray = new object?[] { globals, null };

        // Await directly — exceptions propagate naturally with their original stacks
        // intact, with no ContinueWith dance and no reflection-based Result extraction.
        return await factory(submissionArray).ConfigureAwait(false);
    }
}
