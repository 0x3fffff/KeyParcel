$ErrorActionPreference = 'Stop'
$folder = Join-Path $PSScriptRoot ('build/verify-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
}
$page = Join-Path $PSScriptRoot 'Phone.html'
$rimeLua = Join-Path $PSScriptRoot 'Rime/lua/keyparcel_input.lua'
$exe = Join-Path $folder 'Verify.exe'
$sources = 'Desktop.cs','WindowsInput.cs','InputModes.cs','MaestroKeyboard.cs','RimeInput.cs','RimeSetup.cs','MobileQr.cs','MobileServer.cs','QuickText.cs','Verify.cs' |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
$arguments = @('/nologo', '/main:KeyParcel.Verify', '/codepage:65001', "/out:$exe",
    "/resource:$page,KeyParcel.Phone.html", "/resource:$rimeLua,KeyParcel.Rime.lua", '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll', '/reference:System.Core.dll', '/reference:System.Runtime.Serialization.dll')
& $compiler @arguments @sources
if ($LASTEXITCODE) { throw 'Verification build failed.' }
& $exe $folder
if ($LASTEXITCODE) { throw 'Verification failed.' }
Write-Output $folder
