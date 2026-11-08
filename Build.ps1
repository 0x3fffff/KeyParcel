param([string]$Output = (Join-Path $PSScriptRoot 'dist/1.3.1/KeyParcel.exe'))

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework compiler was not found.' }
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path (Split-Path $Output -Parent) | Out-Null
$page = Join-Path $PSScriptRoot 'Phone.html'
$icon = Join-Path $PSScriptRoot 'KeyParcel.ico'
$manifest = Join-Path $PSScriptRoot 'KeyParcel.manifest'
$sources = 'Desktop.cs','WindowsInput.cs','MobileQr.cs','MobileServer.cs' |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    "/out:$Output", "/resource:$page,KeyParcel.Phone.html", "/win32icon:$icon", "/win32manifest:$manifest",
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Core.dll')
& $compiler @arguments @sources
if ($LASTEXITCODE) { throw 'Compilation failed.' }
Get-Item -LiteralPath $Output
