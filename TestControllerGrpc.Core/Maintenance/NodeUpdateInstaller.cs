using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Maintenance;

/// <summary>
/// Installs Windows updates on an agent by running WUApi on the node itself.
/// <para>
/// The script is sent inline as <c>-EncodedCommand</c> over the existing RunRemoteCommand path rather than
/// shipped as a file or a new RPC: both of those would mean rebuilding and redeploying every agent, and the
/// reboot operation already proves this path works. Orchestration stays here in C#; the WUApi calls stay in
/// PowerShell, matching <see cref="ScriptBackedVirtualizationProvider"/>.
/// </para>
/// <para>
/// <see cref="ActionResult"/> carries no stdout, so results come back over the dispatcher's OutputReceived
/// event. A sentinel-delimited JSON line makes that robust against a pipeline logging on the same agent
/// at the same moment.
/// </para>
/// </summary>
public sealed class NodeUpdateInstaller : INodeUpdateInstaller
{
    private const string Sentinel = "##TCWU##";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly IAppLogger _logger;

    public NodeUpdateInstaller(IAgentGrpcDispatcher dispatcher, IAppLogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Install can take a very long time; search and probe are quick.</summary>
    public TimeSpan InstallTimeout { get; init; } = TimeSpan.FromHours(3);

    public async Task<bool> IsInstallSupportedAsync(string nodeId, CancellationToken ct)
    {
        var payload = await RunAsync(nodeId, "Probe", ct).ConfigureAwait(false);
        return payload?.Ok == true && payload.Supported;
    }

    public async Task<UpdateSearchResult> SearchAsync(string nodeId, CancellationToken ct)
    {
        var payload = await RunAsync(nodeId, "Search", ct).ConfigureAwait(false);
        if (payload is null)
            return UpdateSearchResult.Failed($"No result from {nodeId}; the update script produced no output.");

        return payload.Ok
            ? new UpdateSearchResult(true, payload.Count, payload.Titles ?? [])
            : UpdateSearchResult.Failed(payload.Error ?? "Update search failed.");
    }

    public async Task<UpdateInstallResult> InstallAsync(string nodeId, CancellationToken ct)
    {
        var payload = await RunAsync(nodeId, "Install", ct).ConfigureAwait(false);
        if (payload is null)
            return UpdateInstallResult.Failed($"No result from {nodeId}; the update script produced no output.");

        return payload.Ok
            ? new UpdateInstallResult(true, payload.Installed, payload.Failed, payload.RebootRequired)
            : UpdateInstallResult.Failed(payload.Error ?? "Update install failed.");
    }

    private async Task<WuPayload?> RunAsync(string nodeId, string verb, CancellationToken ct)
    {
        var captured = new List<string>();
        void OnOutput(string agent, string line, string _)
        {
            // Only this node's lines, and only the one line we care about: anything else on the wire is
            // another pipeline's output.
            if (string.Equals(agent, nodeId, StringComparison.OrdinalIgnoreCase) && line.Contains(Sentinel))
                lock (captured) captured.Add(line);
        }

        var action = new ActionConfig
        {
            Tag = $"wu-{verb.ToLowerInvariant()}",
            Type = ActionType.RunRemoteCommand,
            Command = "powershell",
            Parameters = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Encode(BuildScript(verb))}",
            AgentName = nodeId,
        };
        var ctx = new PipelineExecutionContext { SessionId = $"wu-{Guid.NewGuid():N}" };

        _dispatcher.OutputReceived += OnOutput;
        try
        {
            var result = await _dispatcher.ExecuteRemoteCommandAsync(action, ctx, ct).ConfigureAwait(false);
            if (!result.Success)
                _logger.Warn("WindowsUpdate", $"{nodeId} {verb} exited {result.ExitCode}: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            _logger.Error("WindowsUpdate", $"{nodeId} {verb} threw.", ex);
            return new WuPayload { Ok = false, Error = ex.Message };
        }
        finally
        {
            _dispatcher.OutputReceived -= OnOutput;
        }

        string[] lines;
        lock (captured) lines = [.. captured];
        return Parse(lines, nodeId, verb);
    }

    private WuPayload? Parse(IReadOnlyList<string> lines, string nodeId, string verb)
    {
        foreach (var line in lines)
        {
            var start = line.IndexOf(Sentinel, StringComparison.Ordinal);
            if (start < 0) continue;
            start += Sentinel.Length;

            var end = line.IndexOf(Sentinel, start, StringComparison.Ordinal);
            if (end < 0) continue;

            try
            {
                return JsonSerializer.Deserialize<WuPayload>(line[start..end], Json);
            }
            catch (JsonException ex)
            {
                _logger.Warn("WindowsUpdate", $"{nodeId} {verb}: unparseable result line - {ex.Message}");
            }
        }

        return null;
    }

    private static string Encode(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// Late-bound COM, exactly as WindowsUpdateDetector does it, so no interop assembly is needed. The search
    /// here is <c>Online = true</c>: the detector's cached search cannot see what the node has not yet been
    /// offered.
    /// </summary>
    private static string BuildScript(string verb) =>
        $$"""
        $ErrorActionPreference = 'Stop'
        # Progress and host-writes travel as PowerShell STREAMS, which a redirected host serialises into a
        # CLIXML blob on stderr - the whole envelope then lands in the execution log tagged [Error].
        $ProgressPreference = 'SilentlyContinue'
        $out = @{ ok = $false; supported = $false; count = 0; titles = @(); installed = 0; failed = 0; rebootRequired = $false; error = $null }
        try {
            $verb = '{{verb}}'
            $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
            $principal = New-Object Security.Principal.WindowsPrincipal($identity)
            $elevated  = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

            if ($verb -eq 'Probe') {
                $session = New-Object -ComObject Microsoft.Update.Session
                $null = $session.CreateUpdateInstaller()
                $out.supported = $elevated
                $out.ok = $true
            }
            else {
                if (-not $elevated) { throw 'Agent is not running elevated; updates cannot be installed.' }
                # Elevation is the capability, so record it here too: otherwise every Search/Install result
                # reports supported=false and reads like a capability failure.
                $out.supported = $true

                $session  = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                $searcher.Online = $true
                $found = $searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'")

                $out.count  = [int]$found.Updates.Count
                $out.titles = @($found.Updates | ForEach-Object { $_.Title })

                if ($verb -eq 'Search') { $out.ok = $true }
                else {
                    if ($out.count -eq 0) { $out.ok = $true }
                    else {
                        $batch = New-Object -ComObject Microsoft.Update.UpdateColl
                        foreach ($u in $found.Updates) {
                            if (-not $u.EulaAccepted) { $null = $u.AcceptEula() }
                            $null = $batch.Add($u)
                        }

                        $downloader = $session.CreateUpdateDownloader()
                        $downloader.Updates = $batch
                        $null = $downloader.Download()

                        $installer = $session.CreateUpdateInstaller()
                        $installer.Updates = $batch
                        $result = $installer.Install()

                        for ($i = 0; $i -lt $batch.Count; $i++) {
                            # 2 = succeeded, 3 = succeeded with errors; anything else did not install.
                            $code = $result.GetUpdateResult($i).ResultCode
                            if ($code -eq 2 -or $code -eq 3) { $out.installed++ } else { $out.failed++ }
                        }

                        $out.rebootRequired = [bool]$result.RebootRequired
                        $out.ok = ($out.failed -eq 0)
                        if ($out.failed -gt 0) { $out.error = "$($out.failed) update(s) failed to install." }
                    }
                }
            }
        }
        catch {
            $out.ok = $false
            $code = ''
            try { $code = ('0x{0:X8}' -f $_.Exception.HResult) } catch { }
            # "Exception from HRESULT: 0x80244007" on its own tells an operator nothing about where to look.
            # Both SOAP codes below point at the WSUS catalogue, NOT at this machine - measured on WARMGR
            # 2026-09-22, where the fault detail was InvalidParameters/parameters.OtherCachedUpdateIDs.
            $hint = switch ($code) {
                '0x80244007' { ' (WU_E_PT_SOAPCLIENT_SOAPFAULT - typically WSUS rejecting SyncUpdates because the cached-update-ID list is too large; fix on the WSUS server by declining superseded updates and running a cleanup, not on this node)' }
                '0x80244010' { ' (WU_E_PT_EXCEEDED_MAX_SERVER_TRIPS - the catalogue needs more round trips than the server allows; same WSUS cleanup applies)' }
                '0x8024402C' { ' (WU_E_PT_WINHTTP_NAME_NOT_RESOLVED - the update server name did not resolve)' }
                '0x80240438' { ' (no update server configured or reachable)' }
                '0x80072EE2' { ' (timed out reaching the update server)' }
                default      { '' }
            }
            $out.error = $_.Exception.Message + $hint
        }
        # Raw stdout, bypassing PowerShell's streams so the result is never CLIXML-wrapped.
        [Console]::Out.WriteLine('{{Sentinel}}' + ($out | ConvertTo-Json -Compress -Depth 4) + '{{Sentinel}}')
        """;

    private sealed class WuPayload
    {
        public bool Ok { get; set; }
        public bool Supported { get; set; }
        public int Count { get; set; }
        public List<string>? Titles { get; set; }
        public int Installed { get; set; }
        public int Failed { get; set; }
        public bool RebootRequired { get; set; }
        public string? Error { get; set; }
    }
}
