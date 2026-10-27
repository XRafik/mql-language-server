using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Analysis;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using Newtonsoft.Json.Linq;
using Serilog;

using OmniSharp.Extensions.LanguageServer.Server;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;

using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Serialization;

using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.WorkDone;

namespace MqlLanguageServer
{
    /// <summary>
    /// Entry point for the MQL Language Server
    ///
    /// Phase 3.7: Complete LSP Server with stdio connection and all handlers
    /// </summary>
    class Program
    {
        // Captured in OnInitialize (WI-01/D6) and consumed after LanguageServer.From
        // completes, so the initialize response is never delayed by the scan.
        private static List<string> workspaceFoldersSnapshot = new();

        // Issue #116: OmniSharp 0.19.9 routes each textDocument/didOpen|didChange|
        // didClose notification to EXACTLY ONE of two handlers registered for the
        // same method — the custom handler (real logic) or the built-in
        // TextDocumentSync handler registered by OnTextDocumentSync below (whose
        // lambdas previously did nothing). Which one wins is decided per process
        // by the router, so in roughly half of the sessions the notification was
        // silently swallowed: the document was never indexed and cross-file
        // textDocument/definition returned null forever. The lambdas below now
        // delegate to these DI-resolved custom handlers, so routing no longer
        // matters. They are populated in OnInitialized (the container exists by
        // then) and remain null for a notification that somehow arrives before
        // initialization completes.
        private static DidOpenTextDocumentHandler? didOpenTextDocumentHandler;
        private static DidChangeTextDocumentHandler? didChangeTextDocumentHandler;
        private static DidCloseTextDocumentHandler? didCloseTextDocumentHandler;

        /// <summary>
        /// Extracts the <c>systemIncludeRoots</c> string array from the
        /// initialize request's InitializationOptions. The OmniSharp 0.19.9
        /// property is typed <see cref="object"/>; the wire serializer hands us
        /// a Newtonsoft <see cref="JObject"/>, but any other shape (or a
        /// missing/malformed option) yields an empty list — fail open.
        /// </summary>
        private static IReadOnlyList<string> ParseSystemIncludeRoots(object? initializationOptions)
        {
            var roots = new List<string>();
            if (initializationOptions is not JObject options)
            {
                return roots;
            }

            if (options["systemIncludeRoots"] is not JArray array)
            {
                return roots;
            }

            foreach (var item in array)
            {
                if (item is JValue { Value: string root } && !string.IsNullOrWhiteSpace(root))
                {
                    roots.Add(root);
                }
            }

            return roots;
        }

        /// <summary>
        /// Reads the build date embedded as assembly metadata ("BuildDate") by the
        /// 'StampBuildDate' MSBuild target. Falls back to "unknown" when the metadata
        /// is missing (e.g. assemblies produced before the target existed).
        /// </summary>
        private static string GetBuildDate()
        {
            var value = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildDate")
                ?.Value;

            return string.IsNullOrEmpty(value) ? "unknown" : value;
        }

        // Issue #116: synchronous forwarders for the built-in TextDocumentSync
        // lambdas. The handlers' Handle methods complete synchronously
        // (Task.FromResult over a synchronous core), and their HandleSync entry
        // point is that same core, so no Task needs to be awaited or blocked on
        // here. The null guard covers a notification that races initialization:
        // the custom handler cannot be resolved yet, and the defense layer in
        // LanguageAwareHandlerBase covers the request path, so dropping is safe.
        private static void ForwardDidOpen(DidOpenTextDocumentParams request)
        {
            var handler = didOpenTextDocumentHandler;
            if (handler is null)
            {
                Log.Warning("didOpen received before handler resolution; dropping (issue #116)");
                return;
            }

            handler.HandleSync(request, CancellationToken.None);
        }

        private static void ForwardDidChange(DidChangeTextDocumentParams request)
        {
            var handler = didChangeTextDocumentHandler;
            if (handler is null)
            {
                Log.Warning("didChange received before handler resolution; dropping (issue #116)");
                return;
            }

            handler.HandleSync(request, CancellationToken.None);
        }

        private static void ForwardDidClose(DidCloseTextDocumentParams request)
        {
            var handler = didCloseTextDocumentHandler;
            if (handler is null)
            {
                Log.Warning("didClose received before handler resolution; dropping (issue #116)");
                return;
            }

            handler.HandleSync(request, CancellationToken.None);
        }

        /// <summary>
        /// Issue #160: create a workDoneProgress observer for the startup
        /// workspace scan. Per the LSP spec the server may only send
        /// window/workDoneProgress/create (and then $/progress) when the
        /// client declared window.workDoneProgress — IsSupported reflects
        /// exactly that. The create round-trip is bounded so a client that
        /// declares the capability but never answers cannot delay the scan.
        /// Returns null when unsupported or on any failure: progress is
        /// observability, never a startup dependency.
        /// </summary>
        private static async Task<IWorkDoneObserver?> TryCreateScanProgressObserverAsync(ILanguageServer server)
        {
            try
            {
                var manager = server.WorkDoneManager;
                if (manager is null || !manager.IsSupported)
                {
                    Log.Information(
                        "Client did not declare window.workDoneProgress; workspace scan runs without $/progress (issue #160)");
                    return null;
                }

                // Numeric token: ProgressToken only serializes int/string tokens.
                // The value only has to be unique for this server instance.
                var observer = await manager.Create(
                    new ProgressToken(Random.Shared.NextInt64(1, long.MaxValue)),
                    new WorkDoneProgressBegin
                    {
                        Title = "Indexing MQL workspace",
                        Percentage = 0,
                        Message = "Starting workspace scan"
                    },
                    onError: _ => new WorkDoneProgressEnd { Message = "Workspace scan failed" },
                    onComplete: () => new WorkDoneProgressEnd { Message = "Workspace scan finished" },
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

                Log.Information("$/progress observer created for the workspace scan (issue #160)");
                return observer;
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "Could not create workDoneProgress for the workspace scan; continuing without progress (issue #160)");
                return null;
            }
        }

        static async Task<int> Main(string[] args)
        {
            // Build date is embedded as assembly metadata by the 'StampBuildDate'
            // MSBuild target (src/MqlLanguageServer.Server.csproj). It reflects the
            // UTC time of the last successful compile of this assembly.
            var buildDateStr = GetBuildDate();

            // Check for --version flag first.
            // Version comes from the assembly (issue #22), same value shipped in the package.
            var versionStr = ServerVersion.Version;
            if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
            {
                Console.WriteLine($"MQL Language Server {versionStr}");
                Console.WriteLine($"Build Date: {buildDateStr}");
                return 0;
            }

            // Configure Serilog for structured logging
            // IMPORTANT: Write to stderr to avoid polluting JSON-RPC stdout
            //
            // Issue #21c: the file sink used to write into the process working
            // directory (often the workspace root) with unbounded daily-file
            // retention. Logs now go to the OS-appropriate per-user log
            // directory, created up front if missing, with capped retention
            // (7 daily files, 20 MB each) and shared mode so several editor
            // windows can run against the same directory.
            LogPaths.EnsureLogDirectoryExists();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console(
                    standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    LogPaths.GetLogFilePath(),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 20 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true)
                .CreateLogger();

            try
            {
                Log.Information("=================================================");
                Log.Information("Starting MQL Language Server");
                Log.Information($"Version: {versionStr}");
                Log.Information($"Build Date: {buildDateStr}");
                Log.Information("Phase 3.7: Complete LSP Server Implementation");
                Log.Information("=================================================");

                // Issue #91: OmniSharp 0.19.9's [JsonConverter] on the abstract
                // RelatedDocumentDiagnosticReport throws NotImplementedException
                // from WriteJson, so textDocument/diagnostic responses could never
                // be serialized — the exception hit the OutputHandler loop, whose
                // only recourse is a TRACE-level log plus disposing the output
                // pipeline permanently (silent hang + permanent server wedge).
                // Settings-level converters do NOT take precedence over the
                // attribute (it is baked into the CONTRACT converter), so the fix
                // subclasses LspSerializer and rewrites the contract via a wrapping
                // contract resolver (MqlLspSerializer).
                var lspSerializer = new MqlLspSerializer();

                // Use 'From' instead of 'Create': it is asynchronous and
                // guarantees the server initializes correctly.
                var server = await LanguageServer.From(options =>
                {
                    _ = options
                        //.WithInput(Console.OpenStandardInput())
                        .WithInput(new DisconnectAwareStream(Console.OpenStandardInput()))
                        .WithOutput(Console.OpenStandardOutput())
                        .WithSerializer(lspSerializer)
                        .WithLoggerFactory(LoggerFactory.Create(builder => builder.AddSerilog()))
                        .WithServices(services =>
                        {
                            // Keep only our own services here.
                            services.AddTransient<Mql4AntlrParser>();
                            services.AddTransient<Mql5AntlrParser>();
                            // Issue #160: replace the library's pre-initialization
                            // receiver. LspServerReceiver rejects batched-with-
                            // initialize requests with a swallowed -32002 and drops
                            // their notifications; QueuingReceiver dispatches them
                            // instead. Registering for the SAME service types the
                            // library's RegisterMany would claim (and first: its
                            // registration uses IfAlreadyRegistered.Keep) routes the
                            // connection's IReceiver resolution to the subclass.
                            services.AddSingleton<Lsp.Server.QueuingReceiver>();
                            services.AddSingleton<OmniSharp.Extensions.JsonRpc.IReceiver>(sp => sp.GetRequiredService<Lsp.Server.QueuingReceiver>());
                            services.AddSingleton<OmniSharp.Extensions.JsonRpc.Receiver>(sp => sp.GetRequiredService<Lsp.Server.QueuingReceiver>());
                            services.AddSingleton<LspServerReceiver>(sp => sp.GetRequiredService<Lsp.Server.QueuingReceiver>());
                            services.AddSingleton<OpenDocumentStore>();
                            // Issue #22 wiring: the DI singleton MUST be the static
                            // GlobalSymbolIndex.Instance — DryIo otherwise creates a
                            // SECOND index (the class's private parameterless ctor is
                            // usable under the container's non-public-constructor rule)
                            // and the accessor-based handlers (didOpen, definition,
                            // references, rename, workspace/symbol...) end up sharing
                            // a different index than the handlers that construct
                            // GlobalSymbolIndexAccessor's parameterless overload
                            // (hover, documentHighlight, declaration...) — two live
                            // indexes: include-declared symbols visible to some
                            // handlers and invisible to the rest (the residual
                            // "go_to_definition returns the enclosing function" the
                            // rc.1 battery observed in agent-lsp; its hover→
                            // workspace-symbol fuzzy fallback turns the empty index
                            // hover into the wrong answer).
                            services.AddSingleton<GlobalSymbolIndex>(_ => GlobalSymbolIndex.Instance);
                            // Issue #25c: handlers receive the shared index
                            // through this accessor instead of touching the
                            // static GlobalSymbolIndex.Instance singleton.
                            services.AddSingleton<GlobalSymbolIndexAccessor>();
                            services.AddSingleton<MetricsCollector>();
                            services.AddSingleton<MqlLspServer>();
                            services.AddSingleton<MqlLanguageService>();
                            services.AddSingleton<WorkspaceIndexer>();
                            // Issue #91: push-model diagnostics (publishDiagnostics after
                            // didOpen/didChange). The ILanguageServer dependency is Func-wrapped
                            // inside (see DiagnosticPublisher) to avoid the startup
                            // resolution cycle.
                            services.AddSingleton<DiagnosticPublisher>();

                            // Issue #32 (D3): DI-constructed so the builtin
                            // registries reach SemanticAnalyzer rules (the
                            // lazy `new SemanticAnalyzer()` fallback in
                            // DiagnosticHandler has null builtins).
                            services.AddSingleton<SemanticAnalyzer>();

                            // Per-language built-in registries (IMqlBuiltins[]) are injected into handlers.
                            services.AddSingleton<IMqlBuiltins, Mql4BuiltinsAdapter>();
                            services.AddSingleton<IMqlBuiltins, Mql5Builtins>();

                            // Do NOT register handler singletons here:
                            // .WithHandler<T>() registers them in the DI
                            // container automatically.
                        })
                        // Register the handlers. The library detects their
                        // interfaces and fills in the Capabilities
                        // (hoverProvider: true, etc.) for you.
                        .WithHandler<DocumentSymbolHandler>()
                        .WithHandler<DefinitionHandler>()
                        .WithHandler<ReferencesHandler>()
                        .WithHandler<CompletionHandler>()
                        .WithHandler<HoverHandler>()
                        .WithHandler<RenameHandler>()
                        .WithHandler<SignatureHelpHandler>()
                        .WithHandler<FoldingRangeHandler>()
                        .WithHandler<SelectionRangeHandler>()
                        .WithHandler<DocumentHighlightHandler>()
                        .WithHandler<DocumentFormattingHandler>()
                        .WithHandler<RangeFormattingHandler>()
                        .WithHandler<TypeDefinitionHandler>()
                        .WithHandler<CodeActionHandler>()
                        .WithHandler<CodeActionResolveHandler>()
                        .WithHandler<DeclarationHandler>()
                        .WithHandler<ImplementationHandler>()
                        .WithHandler<WorkspaceSymbolHandler>()
                        // Issue #96: LSP 3.17 call hierarchy — the capability is
                        // derived from the prepare handler's registration.
                        .WithHandler<CallHierarchyHandler>()
                        // Issue #123: LSP 3.17 type hierarchy — same pattern,
                        // declared unconditionally (per #95/#96/#120).
                        .WithHandler<TypeHierarchyHandler>()
                        .WithHandler<DiagnosticHandler>()
                        // Issue #30: color swatches. colorProvider capability
                        // derives automatically from DocumentColorHandler's
                        // registration (D4) — no manual capability edits.
                        .WithHandler<DocumentColorHandler>()
                        .WithHandler<ColorPresentationHandler>()

                        // Text-document sync handlers. didSave is intentionally
                        // NOT registered: this server uses pull diagnostics
                        // (DiagnosticHandler implements IDocumentDiagnosticHandler),
                        // and didOpen/didChange keep the parse model and symbol
                        // index current, so there is no didSave-only work to do.
                        .WithHandler<DidOpenTextDocumentHandler>()
                        .WithHandler<DidCloseTextDocumentHandler>()
                        .WithHandler<DidChangeTextDocumentHandler>()
                        // Report the real version in the LSP initialize response
                        // (ServerInfo). The value is derived from the assembly, which
                        // the MSBuild SDK stamps from <Version> in the csproj — the
                        // same value the CI tag↔csproj gate enforces (issue #22).
                        .WithServerInfo(new ServerInfo
                        {
                            Name = Constants.Server.Name,
                            Version = ServerVersion.Version
                        })
                        .OnInitialize((server, request, token) =>
                        {
                            Log.Information("MQL Language Server initialized for client: {ClientName}", request.ClientInfo?.Name ?? "unknown");

                            // WI-01/D6: capture workspace folders during OnInitialize;
                            // the scan starts AFTER InitializeResult is computed so the
                            // response is never delayed (WI-02). Fire-and-forget.
                            workspaceFoldersSnapshot = new List<string>();
                            var foldersContainer = request.WorkspaceFolders;
                            if (foldersContainer is not null)
                            {
                                foreach (var folder in foldersContainer)
                                {
                                    var path = folder.Uri.ToUri().AbsolutePath;
                                    if (!string.IsNullOrEmpty(path))
                                    {
                                        workspaceFoldersSnapshot.Add(path);
                                    }
                                }
                            }

                            // Issue #95: legacy clients (and the agent probes from the
                            // issue reports) declare rootUri WITHOUT workspaceFolders —
                            // for them the workspace scan never started at all, so NO
                            // file was ever scan-indexed and workspace/symbol only saw
                            // didOpen'ed documents. rootUri is the pre-3.4 way of
                            // declaring the single workspace root: honor it as one.
                            if (workspaceFoldersSnapshot.Count == 0 && request.RootUri is not null)
                            {
                                var rootPath = request.RootUri.ToUri().AbsolutePath;
                                if (!string.IsNullOrEmpty(rootPath) && System.IO.Directory.Exists(rootPath))
                                {
                                    workspaceFoldersSnapshot.Add(rootPath);
                                    Log.Information("Workspace root from rootUri (no workspaceFolders declared): {RootPath}", rootPath);
                                }
                            }

                            // Issue #18: register the declared roots for the central
                            // read guard in SourceFileReader. From this point on,
                            // every client-driven disk read must be inside one of
                            // them (fail-open only while the set is empty).
                            WorkspaceRoots.Set(workspaceFoldersSnapshot);

                            // Issue #200: expose the declared workspace folders to the
                            // single include-resolution service so a quoted
                            // parent-relative include between sibling folders of the
                            // same workspace (e.g. `..\Common\util.mqh` from
                            // `Include\Trade.mqh`) is admitted by the containment
                            // gate and reaches the include closure — mirroring the
                            // #186 system-root rule. Wired to the same snapshot that
                            // seeds the central read guard above.
                            IncludePathResolver.SetWorkspaceRootsProvider(() => WorkspaceRoots.Current);

                            // System include roots (MQL4/MQL5 Standard Library):
                            // two explicit sources, in precedence order (issue
                            // #179 S2): the `systemIncludeRoots: ["...MQL5/Include", ...]`
                            // initialization option wins when non-empty; otherwise the
                            // union of the .mql-lsp.json files at each workspace-folder
                            // root (systemIncludeRoots key, relative entries resolved
                            // against the config file's directory, read once per
                            // session — no watcher). Angle-bracket includes
                            // (<Trade\Trade.mqh>) resolve against the effective roots;
                            // resolved headers are lazily parsed and indexed by
                            // didOpen. Fail open: neither source (or malformed
                            // configuration) keeps today's behavior exactly.
                            try
                            {
                                var systemRoots = ParseSystemIncludeRoots(request.InitializationOptions);
                                var effectiveRoots = ProjectConfiguration.ResolveEffectiveRoots(
                                    systemRoots, workspaceFoldersSnapshot);
                                SystemIncludeRoots.Set(effectiveRoots);
                                IncludePathResolver.SetSystemIncludeRootsProvider(() => SystemIncludeRoots.Current);
                                if (effectiveRoots.Count > 0)
                                {
                                    Log.Information("System include roots configured: {SystemRootCount} root(s)", effectiveRoots.Count);
                                }
                            }
                            catch (Exception systemRootsEx)
                            {
                                SystemIncludeRoots.Clear();
                                Log.Warning(systemRootsEx,
                                    "Could not read the systemIncludeRoots initialization option; continuing without system include roots.");
                            }

                            // Issue #93 (fix A): open the receiver gate NOW instead of
                            // at the end of the initialize handling.
                            //
                            // OmniSharp 0.19.9's LspServerReceiver.GetRequests rejects
                            // every incoming message except initialize/initialized with a
                            // ServerNotInitialized (-32002) error until IReceiver.Initialized()
                            // is called — which the library only does at the END of the
                            // initialize request handling (after capability registration).
                            // Worse, the -32002 response is then swallowed by
                            // LspServerOutputFilter ("will be sent later" — never sent),
                            // so requests pipelined by clients during the initialize
                            // handling window (agent MCP relays commonly pipeline
                            // didOpen + queries right after initialize, without waiting
                            // for the response) are silently dropped: no reply, no error,
                            // no log on the wire. Opening the gate here — at the START of
                            // the initialize handling — collapses that window: every
                            // message arriving after initialize handling begins is routed
                            // to the real handlers (already constructed and registered at
                            // server build time) and answered normally. Messages batched
                            // in the same OS pipe read as the initialize request itself
                            // (i.e. written before the server even starts processing)
                            // remain out of reach — those clients are pre-spec anyway.
                            try
                            {
                                // Resolve the concrete receiver the connection was built
                                // with (the LspServerReceiver singleton). Verified by
                                // A/B probe: the same instance is resolved via IReceiver.
                                server.Services
                                    .GetRequiredService<OmniSharp.Extensions.LanguageServer.Server.LspServerReceiver>()
                                    .Initialized();
                                Log.Information("Receiver gate opened early (issue #93): incoming requests are routed immediately");
                            }
                            catch (Exception gateEx)
                            {
                                // Defense-in-depth only: without this, behavior falls back
                                // to the library's own gate (opens right before the
                                // initialize response is sent).
                                Log.Warning(gateEx, "Could not open the receiver gate early; falling back to the library default");
                            }

                            // Issue #93 (fix B): pay the one-time parse-pipeline costs
                            // (ANTLR ATN deserialization + JIT) on a background thread,
                            // concurrent with the initialize round-trip, instead of on the
                            // first didOpen — which on a cold machine stalls OmniSharp's
                            // serial request queue for the whole cold-start duration.
                            _ = Task.Run(ParserWarmup.WarmUp);

                            // The InitializeResult for this handler's return type is
                            // built by the library from options.ServerInfo (set below
                            // via WithServerInfo, issue #22); this delegate's return
                            // value is ignored by OmniSharp 0.19.9 (Task-returning
                            // delegate), so capabilities are derived from the
                            // registered handlers.
                            return Task.CompletedTask;
                        })

                        // Issue #91: LspSerializer.SetClientCapabilities runs during
                        // initialize handling (before the OnInitialize delegates above)
                        // and its private Reset() REPLACES the ContractResolver on both
                        // the settings and the serializer — silently discarding the
                        // diagnostic-report contract wrapper installed by MqlLspSerializer.
                        // Re-apply it here: OnInitialized runs after SetClientCapabilities
                        // and before the first client request can be answered.
                        .OnInitialized((server, request, result, token) =>
                        {
                            lspSerializer.ReapplyDiagnosticContractResolver();
                            Log.Information("Diagnostic report contract resolver re-applied (issue #91)");

                            // Issue #116: resolve the real text-document-sync handlers
                            // once the container is built so the built-in
                            // OnTextDocumentSync lambdas can delegate to them. See the
                            // field declarations above for the one-of-two routing
                            // explanation.
                            didOpenTextDocumentHandler = server.Services.GetRequiredService<DidOpenTextDocumentHandler>();
                            didChangeTextDocumentHandler = server.Services.GetRequiredService<DidChangeTextDocumentHandler>();
                            didCloseTextDocumentHandler = server.Services.GetRequiredService<DidCloseTextDocumentHandler>();
                            Log.Information("Text-document sync forwarders resolved (issue #116)");

                            // Issue #95: always declare workspaceSymbolProvider.
                            //
                            // OmniSharp 0.19.9 computes ServerCapabilities through the
                            // registration-options converters, whose descriptor lookup
                            // consults the CLIENT's declared workspace.symbol capability:
                            // a client that does not declare it gets no
                            // workspaceSymbolProvider key at all ("null" on the wire) —
                            // and a client that trusts the null disables workspace
                            // symbol search even though this server can always answer.
                            // Per the LSP spec the SERVER may declare the provider
                            // regardless of the client capability. The OnInitialized
                            // delegates run AFTER ReadServerCapabilities built the
                            // result and BEFORE the response is sent, so the public
                            // property assignment here is the deterministic hook.
                            result.Capabilities.WorkspaceSymbolProvider =
                                new BooleanOr<WorkspaceSymbolRegistrationOptions.StaticOptions>(
                                    new WorkspaceSymbolRegistrationOptions.StaticOptions
                                    {
                                        ResolveProvider = false,
                                        WorkDoneProgress = false
                                    });
                            Log.Information("workspaceSymbolProvider declared unconditionally (issue #95)");

                            // Issue #96: same client-conditional mechanism — the
                            // callHierarchy capability key is omitted for clients that
                            // do not declare workspace.symbol; the server implements
                            // it, so declare it unconditionally.
                            result.Capabilities.CallHierarchyProvider =
                                new BooleanOr<CallHierarchyRegistrationOptions.StaticOptions>(
                                    new CallHierarchyRegistrationOptions.StaticOptions
                                    {
                                        WorkDoneProgress = false
                                    });
                            Log.Information("callHierarchyProvider declared unconditionally (issue #96)");

                            // Issue #123: LSP 3.17 type hierarchy — same pattern,
                            // declared unconditionally (per #95/#96/#120). OmniSharp's
                            // automatic capability derivation covers callHierarchy but
                            // not typeHierarchy, so it is declared explicitly.
                            result.Capabilities.TypeHierarchyProvider =
                                new BooleanOr<TypeHierarchyRegistrationOptions.StaticOptions>(
                                    new TypeHierarchyRegistrationOptions.StaticOptions
                                    {
                                        WorkDoneProgress = false
                                    });
                            Log.Information("typeHierarchyProvider declared unconditionally (issue #123)");

                            // Issue #120: same client-conditional mechanism — the
                            // diagnostic capability is omitted for clients that do
                            // not declare textDocument.diagnostic, although the
                            // server implements the full pull model
                            // (DiagnosticHandler implements IDocumentDiagnosticHandler
                            // and answers textDocument/diagnostic). Spec-conformant
                            // clients gate pull on the declared capability, so they
                            // silently lose it. Declare it unconditionally, with the
                            // produces for the capability payload (the static
                            // options carry the diagnostic flags; the document
                            // selector stays on the handler's registration
                            // options): no inter-file dependencies, no
                            // workspace-wide diagnostics.
                            result.Capabilities.DiagnosticProvider =
                                new DiagnosticsRegistrationOptions.StaticOptions
                                {
                                    InterFileDependencies = false,
                                    WorkspaceDiagnostics = false
                                };
                            Log.Information("diagnosticProvider declared unconditionally (issue #120)");
                            return Task.CompletedTask;
                        })

                        // This is fine for forcing the sync configuration. The
                        // registration itself (Full sync + attributes provider) is
                        // required to declare the capability; the lambdas are the
                        // built-in handler's notification callbacks. Issue #116:
                        // delegate them to the custom handlers instead of leaving
                        // them empty, so both registered handlers run the real
                        // logic and OmniSharp's one-of-two routing cannot swallow a
                        // document notification.
                        .OnTextDocumentSync(
                            TextDocumentSyncKind.Full,
                            uri => new TextDocumentAttributes(uri, LanguageDetection.GetLanguageIdFromUri(uri.ToUri())),
                            ForwardDidOpen,
                            // Positional order matches the OmniSharp 0.19.9 overload:
                            // open, close, change, save.
                            ForwardDidClose,
                            ForwardDidChange,
                            // didSave is intentionally unhandled: the server uses pull
                            // diagnostics and didOpen/didChange keep the model current.
                            _ => { },
                            new TextDocumentSyncRegistrationOptions())
                        
                            ;
                });

                Log.Information("Language Server started and listening on stdio...");

                // WI-01/D6: start the workspace scan after initialization so the
                // initialize response was never delayed (WI-02). Fire-and-forget.
                try
                {
                    var workspaceFolders = workspaceFoldersSnapshot;
                    if (workspaceFolders.Count > 0)
                    {
                        // Issue #95: surface the scan lifecycle to the client via
                        // window/logMessage (started / finished / failed) so the
                        // three states — in progress, finished without results,
                        // failed silently — are distinguishable. Best-effort.
                        var indexer = server.GetRequiredService<WorkspaceIndexer>();
                        indexer.ScanNotifier = (level, message) =>
                        {
                            try
                            {
                                var messageType = level switch
                                {
                                    LogLevel.Error => MessageType.Error,
                                    LogLevel.Warning => MessageType.Warning,
                                    _ => MessageType.Info
                                };
                                server.Window.SendNotification(new LogMessageParams
                                {
                                    Type = messageType,
                                    Message = message
                                });
                            }
                            catch
                            {
                                // Never break the scan for an observability emit.
                            }
                        };

                        // Issue #160: surface the scan lifecycle via $/progress
                        // (workDoneProgress) so clients have an event-driven
                        // readiness signal — they no longer need fixed timeouts
                        // or busy-poll probes to know when indexing completed.
                        // Spec-gated: only for clients that declared
                        // window.workDoneProgress (IsSupported checks it).
                        var progressObserver = await TryCreateScanProgressObserverAsync(server);
                        if (progressObserver is not null)
                        {
                            indexer.ScanProgressNotifier = (percentage, message) =>
                            {
                                try
                                {
                                    progressObserver.OnNext(message, percentage, null);
                                }
                                catch
                                {
                                    // Never break the scan for an observability emit.
                                }
                            };
                        }

                        indexer.StartIndexing(
                            workspaceFolders,
                            onScanCompleted: () =>
                            {
                                if (progressObserver is null) return;
                                try
                                {
                                    progressObserver.OnCompleted();
                                }
                                catch
                                {
                                    // Never break shutdown for an observability emit.
                                }
                            });
                        Log.Information("Workspace scan started for {FolderCount} folder(s)", workspaceFolders.Count);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to start workspace scan; server continues without it.");
                }

                // Wait for the server to exit.
                await server.WaitForExit;

                // Cancel the workspace scan on shutdown (D6).
                try
                {
                    server.GetRequiredService<WorkspaceIndexer>().StopIndexing();
                }
                catch (ObjectDisposedException)
                {
                    // server already disposed during shutdown — nothing to cancel
                }

                // Log metrics summary before shutdown
                var metrics = MetricsCollector.Instance.GetSummaryReport();
                Log.Information("\n{MetricsSummary}", metrics);

                Log.Information("MQL Language Server shutting down...");


                return 0;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "MQL Language Server terminated unexpectedly");
                return 1;
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }
        }
    }
}