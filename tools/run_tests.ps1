# Bakkum test runner: runs the built-in test suites WITHOUT opening the program window.
#
#   powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1 -Dir C:\bakkum-test
#   powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1 -Dir C:\bakkum-test -Suites net,ai
#
# - Each suite runs in its own PowerShell process that loads build\Bakkum.exe through reflection.
#   No new Bakkum.exe process starts, and the app's settings and logs go to a temporary folder
#   (deleted afterwards) instead of %APPDATA%\Bakkum.
# - -Dir is a folder made only for tests. Suites write result files into it.
#   Never use a folder that holds real school or student files.
# - The default suites make their own test files (patterned video, beeps, invented documents) and need
#   nothing in -Dir: video, record, proxyquick, phone, update, look (styles and the usage signal, fake server on this PC).
# - net is the AI connection test (/selftest-net): OpenRouter and ChatGPT sign-in, refresh and sign-out,
#   and Claude Code, against fake servers on this PC (tools\mock_ai.py, tools\mock_claude.py).
#   Nothing goes to the internet. Needs Python 3 with the cryptography package (pip install cryptography).
# - These read sample files that must be put in -Dir first (the owner hands them over as a test-materials
#   folder): photo, pdf, doc, format, media, ai (clear.png, scan.pdf, tbl.pdf), whisper / whisperturbo
#   (whisper_onnx\ model files and whisper_test\ recording).
# - Need more than files: hancom and lecturehancom (Hancom Office), camreal (turns the real webcam on),
#   ai (Python 3; it talks to tools\mock_claude.py, a fake Claude Code), whisperturbo (about 3 GB of
#   memory), proxy (slow: makes a 2-minute 1080p video), office (MS Office or Hancom Office: makes its own
#   old .xls/.cell/.ppt files and converts them to .xlsx/.csv/.pptx; no sample files needed).
# - Suites run one after another, never at the same time (video suites are heavy on the GPU).
# - The full output of each suite is saved as <Dir>\result_<suite>.txt (UTF-8).
param(
    [string[]]$Suites = @("video,record,proxyquick,phone,update,look"),
    [string]$Dir = "",
    [string]$Exe = "",
    [string]$Inner = "",
    [string]$Data = "",
    [int]$Port = 0
)
$root = Split-Path -Parent $PSScriptRoot
if ($Exe -eq "") { $Exe = Join-Path $root "build\Bakkum.exe" }

if ($Inner -ne "") {
    # One suite inside this process.
    $ErrorActionPreference = 'Stop'
    try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
    # The tests find tools\mock_claude.py from the current folder (the process one, not only PowerShell's).
    Set-Location $root
    [Environment]::CurrentDirectory = $root
    $asm = [Reflection.Assembly]::LoadFrom($Exe)
    $mgr = New-Object System.AppDomainManager
    [System.AppDomainManager].GetField('m_entryAssembly', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($mgr, $asm)
    [AppDomain].GetField('_domainManager', [Reflection.BindingFlags]'Instance,NonPublic').SetValue([AppDomain]::CurrentDomain, $mgr)
    New-Item -ItemType Directory -Force $Data | Out-Null
    $asm.GetType('Bakkum.AppInfo').GetField('DataDirOverride', [Reflection.BindingFlags]'Static,Public,NonPublic').SetValue($null, $Data)
    if ($Inner -eq "net") {
        # SelfTest keeps its own temporary settings folder and only accepts 127.0.0.1 addresses.
        $run = $asm.GetType('Bakkum.SelfTest').GetMethod('Run', [Reflection.BindingFlags]'Static,Public,NonPublic')
        $code = $run.Invoke($null, @(, [string[]]@('/selftest-net', [string]$Port)))
    }
    else {
        $run = $asm.GetType('Bakkum.ToolTest').GetMethod('Run', [Reflection.BindingFlags]'Static,Public,NonPublic')
        $code = $run.Invoke($null, @(, [string[]]@('/selftest-tools', $Dir, $Inner)))
    }
    Write-Output ('exit ' + $code)
    [Console]::Out.Flush()
    # Like Bakkum's own Main: end at once. A process that used the Windows PDF engine can spin in its exit cleanup.
    [System.Diagnostics.Process]::GetCurrentProcess().Kill()
}

# Starts tools\mock_ai.py on a free 127.0.0.1 port and waits until it answers. Returns the process, or $null.
function Start-FakeServer([ref]$portOut) {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    $p = $l.LocalEndpoint.Port
    $l.Stop()
    $script = Join-Path $root "tools\mock_ai.py"
    try {
        $proc = Start-Process python -ArgumentList @("-I", ('"' + $script + '"'), $p) -WindowStyle Hidden -PassThru
    }
    catch {
        return $null
    }
    for ($i = 0; $i -lt 50; $i++) {
        if ($proc.HasExited) { return $null }
        try {
            $c = New-Object System.Net.Sockets.TcpClient
            $c.Connect("127.0.0.1", $p)
            $c.Close()
            $portOut.Value = $p
            return $proc
        }
        catch {
            Start-Sleep -Milliseconds 200
        }
    }
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    return $null
}

if (-not (Test-Path $Exe)) {
    Write-Output ("Build first (powershell -ExecutionPolicy Bypass -File .\build.ps1): " + $Exe)
    exit 2
}
if ($Dir -eq "") {
    Write-Output "Give a test folder, for example: -Dir C:\bakkum-test"
    exit 2
}
New-Item -ItemType Directory -Force $Dir | Out-Null
$Dir = (Resolve-Path $Dir).Path
$Exe = (Resolve-Path $Exe).Path
$oldEncoding = $null
try { $oldEncoding = [Console]::OutputEncoding; [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
$bad = 0
try {
    foreach ($suite in (($Suites -join ",").Split(","))) {
        $suite = $suite.Trim().ToLowerInvariant()
        if ($suite -eq "") { continue }
        $server = $null
        $netPort = 0
        if ($suite -eq "net") {
            $server = Start-FakeServer ([ref]$netPort)
            if ($server -eq $null) {
                $bad++
                Write-Output "[net] the fake AI server (tools\mock_ai.py) did not start. Python 3 and its cryptography package are needed: pip install cryptography"
                continue
            }
        }
        $data = Join-Path $env:TEMP ("bakkum-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
        $out = Join-Path $Dir ("result_" + $suite + ".txt")
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            powershell -STA -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Inner $suite -Dir $Dir -Exe $Exe -Data $data -Port $netPort 2>$null | Out-File $out -Encoding utf8
        }
        finally {
            if ($server -ne $null) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
        }
        $sw.Stop()
        Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
        $lines = @(Get-Content $out -Encoding utf8)
        $end = -1
        for ($i = $lines.Count - 1; $i -ge 0; $i--) { if ($lines[$i] -match '^exit (\d+)$') { $end = $i; break } }
        $secs = [math]::Round($sw.Elapsed.TotalSeconds)
        if ($end -lt 1) {
            $bad++
            Write-Output ("[" + $suite + "] no result (" + $secs + " s) - see " + $out)
            continue
        }
        if ($lines[$end] -ne 'exit 0') { $bad++ }
        Write-Output ("[" + $suite + "] " + $lines[$end - 1] + "  (" + $secs + " s)")
        $lines | Where-Object { $_ -match '^\s+FAIL ' } | ForEach-Object { Write-Output $_ }
    }
}
finally {
    if ($oldEncoding -ne $null) { try { [Console]::OutputEncoding = $oldEncoding } catch { } }
}
if ($bad -gt 0) { exit 1 }
exit 0
