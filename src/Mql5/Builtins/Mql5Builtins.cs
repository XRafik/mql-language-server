using System;
using System.Collections.Generic;
using MqlLanguageServer.Builtins;
using MqlLanguageServer.Mql4.Builtins;

namespace MqlLanguageServer.Mql5.Builtins;

/// <summary>
/// MQL5 built-in functions and variables registry (issue #136).
/// Documentation-driven: entries load lazily from the embedded golden list
/// (<c>data/builtins/mql5.json</c>), which is parity-tested against this
/// registry in CI. Adding names is a change to the data file, never to this
/// class.
/// OPTIMIZATION: lazy loading preserved for improved startup performance.
/// </summary>
public class Mql5Builtins : IMqlBuiltins
{
    private static readonly Lazy<Dictionary<string, string>> LazyBuiltInFunctions =
        new(() => BuiltinGoldenData.Load("mql5").Functions);

    private static readonly Lazy<Dictionary<string, string>> LazyBuiltInVariables =
        new(() => BuiltinGoldenData.Load("mql5").Variables);

    public Dictionary<string, string> BuiltInFunctions => LazyBuiltInFunctions.Value;

    public Dictionary<string, string> BuiltInVariables => LazyBuiltInVariables.Value;

    public bool IsBuiltinFunction(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return BuiltInFunctions.ContainsKey(name);
    }

    public bool IsBuiltinVariable(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return BuiltInVariables.ContainsKey(name);
    }

    public bool IsBuiltin(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return IsBuiltinFunction(name) || IsBuiltinVariable(name);
    }

    public string? GetBuiltinFunctionSignature(string name)
    {
        return BuiltInFunctions.TryGetValue(name, out var signature) ? signature : null;
    }

    public string? GetBuiltinVariableDescription(string name)
    {
        return BuiltInVariables.TryGetValue(name, out var description) ? description : null;
    }
}
