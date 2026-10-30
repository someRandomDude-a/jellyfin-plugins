# Bounded HTTP for tool bootstrap. A single cancellation deadline covers DNS,
# connection, headers and the ENTIRE response body, including slow trickles.
function Invoke-ToolHttp {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][uri]$Uri,
        [string]$Destination = '',
        [hashtable]$Headers = @{},
        [ValidateRange(1,900)][int]$TimeoutSeconds = 30,
        [ValidateRange(1,2147483647)][long]$MaxBytes = 8388608,
        [switch]$Quiet
    )
    if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Tool bootstrap requires PowerShell 7 or newer' }
    if ($Uri.Scheme -ne 'https' -and !($Uri.Scheme -eq 'http' -and $Uri.IsLoopback)) { throw 'Tool downloads require HTTPS' }
    if (!$Quiet) { Write-Host "Fetching $Name (hard deadline: $TimeoutSeconds seconds)" }
    $handler = [Net.Http.SocketsHttpHandler]::new()
    $handler.ConnectTimeout = [TimeSpan]::FromSeconds([Math]::Min(15,$TimeoutSeconds))
    $handler.MaxAutomaticRedirections = 5
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $deadline = [Threading.CancellationTokenSource]::new($TimeoutSeconds * 1000)
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$Uri)
    $response = $null; $source = $null; $output = $null
    try {
        foreach ($key in $Headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($key,[string]$Headers[$key]) }
        $response = $client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token).GetAwaiter().GetResult()
        if (!$response.IsSuccessStatusCode) { throw "HTTP $([int]$response.StatusCode) fetching $Name" }
        if ($response.Content.Headers.ContentLength -gt $MaxBytes) { throw "Response exceeds size limit for $Name" }
        $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $output = if ($Destination) { [IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read) } else { [IO.MemoryStream]::new() }
        $buffer = [byte[]]::new(81920); [long]$received = 0
        while (($count = $source.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) {
            $received += $count
            if ($received -gt $MaxBytes) { throw "Response exceeds size limit for $Name" }
            [void]$output.WriteAsync($buffer,0,$count,$deadline.Token).GetAwaiter().GetResult()
        }
        if (!$Quiet) { Write-Host "Received $Name ($received bytes)" }
        if (!$Destination) { return [Text.Encoding]::UTF8.GetString($output.ToArray()) }
    } catch {
        if ($deadline.IsCancellationRequested) { throw "Timed out fetching $Name after $TimeoutSeconds seconds; no fallback. Partial downloads are preserved and must not be executed." }
        if ($response -and !$response.IsSuccessStatusCode) { throw "HTTP $([int]$response.StatusCode) fetching $Name; no fallback" }
        if ($_.Exception.Message -like 'Response exceeds size limit*') { throw }
        throw "Failed fetching $Name; no fallback. Partial downloads are preserved and must not be executed."
    } finally {
        if ($output) { $output.Dispose() }; if ($source) { $source.Dispose() }
        if ($response) { $response.Dispose() }; $request.Dispose(); $deadline.Dispose(); $client.Dispose()
    }
}
