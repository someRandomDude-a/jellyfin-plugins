param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$Sha256)
$ErrorActionPreference = 'Stop'
if ((Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Sha256) { throw 'Security asset checksum mismatch' }
$archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entries = @($archive.Entries | Where-Object Name -CEQ 'Jellyfin.Plugin.TwoFactorAuth.dll')
    if ($entries.Count -ne 1) { throw 'Security archive must contain exactly one plugin assembly' }
    $stream = $entries[0].Open()
    $bytes = [IO.MemoryStream]::new()
    try { $stream.CopyTo($bytes) } finally { $stream.Dispose() }
    $bytes.Position = 0
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($bytes)
    try {
        # Metadata-only inspection. Never load/execute a downloaded assembly.
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $definition = $metadata.GetAssemblyDefinition()
        if ($metadata.GetString($definition.Name) -cne 'Jellyfin.Plugin.TwoFactorAuth') { throw 'Unexpected Security assembly identity' }
        $definition.Version.ToString()
    } finally { $pe.Dispose(); $bytes.Dispose() }
} finally { $archive.Dispose() }
