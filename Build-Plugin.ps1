$ErrorActionPreference = 'Stop'

$gameDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'Mental Omega'
if (!(Test-Path -LiteralPath $gameDirectory -PathType Container)) {
    throw "Game directory not found: $gameDirectory"
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ('SA_LocalIdentity-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($staging)
$stagedDll = Join-Path $staging 'SA_LocalIdentity.dll'
# The published EXE and DLL have the same file stem, but need different managed assembly names.
$stagedExe = Join-Path $staging 'SA_LocalIdentity.Host.exe'

Push-Location -LiteralPath $PSScriptRoot
try {
    $dotnet = (Get-Command dotnet.exe -CommandType Application).Source
    $sdkVersion = & $dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the active .NET SDK.' }

    $compiler = Join-Path (Split-Path -Parent $dotnet) "sdk\$sdkVersion\Roslyn\bincore\csc.dll"
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $references = 'mscorlib.dll', 'System.dll', 'System.Core.dll' | ForEach-Object {
        '/reference:' + (Join-Path $framework $_)
    }
    $options = '/nologo', '/noconfig', '/nostdlib+', '/platform:anycpu', '/optimize+', '/warnaserror+'

    & $dotnet $compiler @options @references /target:library ("/out:$stagedDll") IdentityRelay.cs
    if ($LASTEXITCODE -ne 0) { throw 'Could not build SA_LocalIdentity.dll.' }

    $uiReferences = 'System.Drawing.dll', 'System.Windows.Forms.dll' | ForEach-Object {
        '/reference:' + (Join-Path $framework $_)
    }
    & $dotnet $compiler @options @references @uiReferences /target:winexe ("/reference:$stagedDll") ("/out:$stagedExe") Program.cs CnCNetLogMonitor.cs IdentityPreferences.cs HostsRedirector.cs
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the Windows Forms host.' }

    foreach ($destination in $PSScriptRoot, $gameDirectory) {
        Copy-Item -LiteralPath $stagedDll -Destination (Join-Path $destination 'SA_LocalIdentity.dll') -Force
        Copy-Item -LiteralPath $stagedExe -Destination (Join-Path $destination 'SA_LocalIdentity.exe') -Force
    }
} finally {
    Pop-Location
    [IO.File]::Delete($stagedDll)
    [IO.File]::Delete($stagedExe)
    [IO.Directory]::Delete($staging)
}
