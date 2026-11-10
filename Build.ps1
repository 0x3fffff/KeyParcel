param(
    [string]$Output = (Join-Path $PSScriptRoot 'dist/1.4.1/KeyParcel.exe'),
    [string]$HIDMaestroSource,
    [string]$DotNet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework compiler was not found.' }
$Output = [IO.Path]::GetFullPath($Output)
$outputDir = Split-Path $Output -Parent
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
if ($HIDMaestroSource) {
    $upstream = (Resolve-Path -LiteralPath $HIDMaestroSource).Path
    $sdk = Join-Path $upstream 'HIDMaestro.Core.dll'
    if ((Get-FileHash -LiteralPath $sdk).Hash -ne 'F13E32591404DB0F418AF9265B8E240F71257297CFAE5A41DCD696FD87E9B4D5') {
        throw 'Use the official HIDMaestro 2.1.0 SDK from its GitHub release.'
    }
    $project = Join-Path $PSScriptRoot 'VirtualKeyboard/KeyParcel.Keyboard.csproj'
    & $DotNet publish $project -c Release --self-contained true -o (Join-Path $outputDir 'keyboard') "-p:HIDMaestroSource=$upstream" -p:NuGetAudit=false
    if ($LASTEXITCODE) { throw 'Virtual keyboard build failed.' }
}
$page = Join-Path $PSScriptRoot 'Phone.html'
$icon = Join-Path $PSScriptRoot 'KeyParcel.ico'
$manifest = Join-Path $PSScriptRoot 'KeyParcel.manifest'
$sources = 'Desktop.cs','WindowsInput.cs','InputModes.cs','MaestroKeyboard.cs','MobileQr.cs','MobileServer.cs','QuickText.cs' |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    "/out:$Output", "/resource:$page,KeyParcel.Phone.html", "/win32icon:$icon", "/win32manifest:$manifest",
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Core.dll',
    '/reference:System.Runtime.Serialization.dll')
& $compiler @arguments @sources
if ($LASTEXITCODE) { throw 'Compilation failed.' }
Get-Item -LiteralPath $Output
