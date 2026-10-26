using System;
using System.Collections.Generic;
using MqlLanguageServer.Builtins;

namespace MqlLanguageServer.Mql4.Builtins;

/// <summary>
/// MQL4 built-in functions and variables registry (issue #136).
/// Documentation-driven: entries load lazily from the embedded golden list
/// (<c>data/builtins/mql4.json</c>), which is parity-tested against this
/// registry in CI. Adding names is a change to the data file, never to this
/// class — the former hand-curated, incident-patched initializers
/// (#46, #109, #126) are gone.
/// OPTIMIZATION: lazy loading preserved for improved startup performance.
/// </summary>
public static class Mql4Builtins
{
    // Lazy-initialized dictionaries for built-in functions and variables.
    // This defers initialization until first access, improving startup time.
    private static readonly Lazy<Dictionary<string, string>> LazyBuiltInFunctions =
        new(() => BuiltinGoldenData.Load("mql4").Functions);

    private static readonly Lazy<Dictionary<string, string>> LazyBuiltInVariables =
        new(() => BuiltinGoldenData.Load("mql4").Variables);

    /// <summary>
    /// OPTIMIZATION: Public accessor for BuiltInFunctions (lazy-loaded)
    /// </summary>
    public static Dictionary<string, string> BuiltInFunctions => LazyBuiltInFunctions.Value;

    /// <summary>
    /// OPTIMIZATION: Public accessor for BuiltInVariables (lazy-loaded)
    /// </summary>
    public static Dictionary<string, string> BuiltInVariables => LazyBuiltInVariables.Value;

    /// <summary>
    /// Check if a name is a built-in function
    /// </summary>
    public static bool IsBuiltinFunction(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return BuiltInFunctions.ContainsKey(name);
    }

    /// <summary>
    /// Check if a name is a built-in variable
    /// </summary>
    public static bool IsBuiltinVariable(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return BuiltInVariables.ContainsKey(name);
    }

    /// <summary>
    /// Check if a name is a built-in (function or variable)
    /// </summary>
    public static bool IsBuiltin(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return IsBuiltinFunction(name) || IsBuiltinVariable(name);
    }

    /// <summary>
    /// Get builtin function signature
    /// </summary>
    public static string? GetBuiltinFunctionSignature(string name)
    {
        return BuiltInFunctions.TryGetValue(name, out var signature) ? signature : null;
    }

    /// <summary>
    /// Get builtin variable description
    /// </summary>
    public static string? GetBuiltinVariableDescription(string name)
    {
        return BuiltInVariables.TryGetValue(name, out var description) ? description : null;
    }
}
