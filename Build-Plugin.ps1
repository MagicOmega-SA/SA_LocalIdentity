$ErrorActionPreference = 'Stop'

$staging = Join-Path ([IO.Path]::GetTempPath()) ('SA_LocalIdentity-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($staging)
$outputFiles = [ordered]@{}
$stagedFiles = @()

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
    $options = '/nologo', '/noconfig', '/nostdlib+', '/platform:anycpu', '/optimize+', '/warnaserror+', '/deterministic+'
    $uiReferences = 'System.Drawing.dll', 'System.Windows.Forms.dll' | ForEach-Object {
        '/reference:' + (Join-Path $framework $_)
    }
    foreach ($name in 'SA_LocalIdentity', 'SA_LocalIdentity.nolog') {
        $defines = @()
        if ($name -eq 'SA_LocalIdentity') { $defines += '/define:SA_LOG' }

        $stagedDll = Join-Path $staging "$name.dll"
        # The host and its library need distinct managed assembly names.
        $stagedExe = Join-Path $staging "$name.Host.exe"
        $stagedFiles += $stagedDll, $stagedExe
        & $dotnet $compiler @options @references @defines /target:library ("/out:$stagedDll") IdentityRelay.cs GameFileHash.cs
        if ($LASTEXITCODE -ne 0) { throw "Could not build $name.dll." }
        & $dotnet $compiler @options @references @uiReferences @defines /target:winexe ("/reference:$stagedDll") ("/out:$stagedExe") Program.cs CnCNetLogMonitor.cs IdentityPreferences.cs HostsRedirector.cs CnCNetTarget.cs
        if ($LASTEXITCODE -ne 0) { throw "Could not build $name.exe." }

        $outputFiles["$name.dll"] = $stagedDll
        $outputFiles["$name.exe"] = $stagedExe
    }

    foreach ($fileName in $outputFiles.Keys) {
        Copy-Item -LiteralPath $outputFiles[$fileName] -Destination (Join-Path $PSScriptRoot $fileName) -Force
    }
} finally {
    Pop-Location
    foreach ($filePath in $stagedFiles) { [IO.File]::Delete($filePath) }
    [IO.Directory]::Delete($staging)
}
