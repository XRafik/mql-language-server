using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Serialization;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// LspSerializer subclass that replaces OmniSharp 0.19.9's broken diagnostic
/// report contract converters (issue #91).
///
/// <para>The abstract <see cref="RelatedDocumentDiagnosticReport"/> carries a
/// <c>[JsonConverter]</c> whose <c>WriteJson</c> throws
/// <see cref="NotImplementedException"/>, so pull diagnostics responses could
/// never be serialized — the exception hit the OutputHandler loop, whose only
/// recourse is a TRACE-level log plus disposing the output pipeline
/// permanently (the silent hang + permanent server wedge of issue #91).</para>
///
/// <para>Settings-level converters do NOT win here: Newtonsoft resolves the
/// type's attribute to a <b>contract</b> converter (via the contract
/// resolver), and the contract converter takes precedence over
/// <c>JsonSerializerSettings.Converters</c>. The only in-process extension
/// point is the contract resolver itself — this subclass wraps the resolver
/// the base class creates and overwrites the converter on the diagnostic
/// report contracts with <see cref="RelatedDocumentDiagnosticReportConverter"/>,
/// which serializes the two concrete report shapes the server emits per the
/// LSP 3.17 wire format.</para>
/// </summary>
public sealed class MqlLspSerializer : LspSerializer
{
    private readonly RelatedDocumentDiagnosticReportConverter _reportConverter = new();

    protected override JsonSerializerSettings CreateSerializerSettings()
    {
        var settings = base.CreateSerializerSettings();
        settings.ContractResolver = new DiagnosticReportContractResolver(settings.ContractResolver!, _reportConverter);
        return settings;
    }

    protected override JsonSerializer CreateSerializer()
    {
        var serializer = base.CreateSerializer();
        serializer.ContractResolver = new DiagnosticReportContractResolver(serializer.ContractResolver!, _reportConverter);
        return serializer;
    }

    /// <summary>
    /// Re-apply the diagnostic-report contract wrapper.
    ///
    /// <para>MUST be called after <c>LspSerializer.SetClientCapabilities</c>
    /// runs (it does during initialize handling, and its private <c>Reset()</c>
    /// REPLACES the ContractResolver on both the settings and the serializer,
    /// silently discarding the wrapper installed by the overrides above).
    /// The OnInitialized hook in Program runs after that point — call this from
    /// there, before the first client request can be answered.</para>
    /// </summary>
    public void ReapplyDiagnosticContractResolver()
    {
        Settings.ContractResolver = new DiagnosticReportContractResolver(Settings.ContractResolver!, _reportConverter);
        JsonSerializer.ContractResolver = new DiagnosticReportContractResolver(JsonSerializer.ContractResolver!, _reportConverter);
    }

    /// <summary>
    /// Delegating resolver that rewrites the (broken, NotImplementedException)
    /// contract converter for the diagnostic report types after the base
    /// resolver has applied the attribute. Everything else passes through
    /// untouched, preserving OmniSharp's LSP serialization semantics
    /// (camelCase, optional properties, enum-string kinds, ...).
    /// </summary>
    private sealed class DiagnosticReportContractResolver : IContractResolver
    {
        private readonly IContractResolver _inner;
        private readonly RelatedDocumentDiagnosticReportConverter _converter;

        public DiagnosticReportContractResolver(IContractResolver inner, RelatedDocumentDiagnosticReportConverter converter)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        }

        public JsonContract ResolveContract(Type type)
        {
            var contract = _inner.ResolveContract(type) ?? throw new InvalidOperationException(
                $"The wrapped contract resolver returned no contract for type {type.FullName}.");
            if (typeof(RelatedDocumentDiagnosticReport).IsAssignableFrom(type))
            {
                // Overwrite the attribute-derived contract converter (which
                // throws NotImplementedException) with the working one. Contracts
                // are cached by the inner resolver per resolver instance, so the
                // rewrite is stable for the lifetime of this serializer.
                contract.Converter = _converter;
            }

            return contract;
        }
    }
}