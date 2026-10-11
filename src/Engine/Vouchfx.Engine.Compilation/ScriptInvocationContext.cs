using System.Collections;
using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Engine.Compilation;

/// <summary>Supplies invocation cancellation through the existing globals factory ABI.</summary>
internal static class ScriptInvocationContext
{
    internal const string CancellationServiceKey = "__vouchfx_run_cancellation";

    internal static ScriptGlobalVariables Create(ScriptGlobalVariables globals, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (globals.Services.ContainsKey(CancellationServiceKey))
            throw new InvalidOperationException("The script service map contains a reserved engine invocation key.");
        if (!cancellationToken.CanBeCanceled)
            return globals;

        // Delegate caller lookups and live values while supplying only the engine token.
        // Neither caller dictionaries nor the reported Vars surface are modified.
        var services = new InvocationServices(globals.Services, cancellationToken);
        return new ScriptGlobalVariables(globals.Vars, services,
            globals.Secrets, globals.Webhooks, globals.Traces, globals.StepEvents, globals.Security);
    }

    private sealed class InvocationServices : IReadOnlyDictionary<string, object>
    {
        private readonly IReadOnlyDictionary<string, object> _original;
        private readonly object _cancellationToken;

        internal InvocationServices(IReadOnlyDictionary<string, object> original, CancellationToken cancellationToken)
        {
            _original = original;
            _cancellationToken = cancellationToken;
        }

        public object this[string key] => IsEngineKey(key) ? _cancellationToken : _original[key];
        public int Count => _original.Count + (_original.Keys.Any(IsEngineKey) ? 0 : 1);
        public IEnumerable<string> Keys => this.Select(pair => pair.Key);
        public IEnumerable<object> Values => this.Select(pair => pair.Value);
        public bool ContainsKey(string key) => IsEngineKey(key) || _original.ContainsKey(key);

        public bool TryGetValue(string key, out object value)
        {
            if (!IsEngineKey(key))
                return _original.TryGetValue(key, out value!);
            value = _cancellationToken;
            return true;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (var pair in _original)
            {
                if (!IsEngineKey(pair.Key))
                    yield return pair;
            }
            yield return new KeyValuePair<string, object>(CancellationServiceKey, _cancellationToken);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private static bool IsEngineKey(string key) => string.Equals(key, CancellationServiceKey, StringComparison.Ordinal);
    }
}
