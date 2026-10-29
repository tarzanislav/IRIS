<# : IRIS - prehlad hardveru. Spusti dvojklikom; v prikazovom riadku aj s nazvom: iris-hw.bat notebook
@echo off
setlocal
set "IRIS_HW_FILE=%~f0"
set "IRIS_HW_NAME=%~1"
if not defined IRIS_HW_PS (where pwsh >nul 2>nul && set "IRIS_HW_PS=pwsh" || set "IRIS_HW_PS=powershell")
%IRIS_HW_PS% -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Expression ([System.IO.File]::ReadAllText($env:IRIS_HW_FILE, [System.Text.Encoding]::UTF8))"
set "IRIS_HW_EXIT=%ERRORLEVEL%"
if "%~1"=="" pause
exit /b %IRIS_HW_EXIT%
#>
# The lines above are the batch part (for PowerShell they are a comment): the file runs itself in PowerShell.
# Zistí parametre počítača pre IRIS: procesor (a či vie AVX2 / AVX-512), pamäť a jej rýchlosť, grafickú kartu, disky
# a systém. Nezbiera nič osobné: žiadne meno počítača ani používateľa, sériové čísla, sieťové adresy ani kľúče.
# Výsledok uloží do iris-hw-<názov>.txt vedľa tohto súboru a skopíruje ho do schránky. Nič neinštaluje a nič neposiela.

$ErrorActionPreference = 'SilentlyContinue'
$Nazov = $env:IRIS_HW_NAME
if (-not $Nazov) {
    Write-Host 'IRIS - prehľad hardvéru' -ForegroundColor Cyan
    $Nazov = Read-Host 'Ako nazveš tento počítač? (napr. stolovy, notebook, kancelaria-1)'
}
$name = ($Nazov -replace '[^\p{L}\p{Nd}_-]', '-').Trim('-')
if (-not $name) { $name = 'pc' }
Write-Host 'Zisťujem parametre a meriam rýchlosť pamäte (asi 10 sekúnd)...'

function Round-GB([double]$bytes) { [math]::Round($bytes / 1GB, 1) }

# --- processor and its vector instructions (what the IRIS kernels use: AVX2 + FMA, later AVX-512 / VNNI)
$cpus = @(Get-CimInstance Win32_Processor)
$cpu = $cpus | Select-Object -First 1
$features = [ordered]@{}
if ($PSVersionTable.PSVersion.Major -ge 7) {
    foreach ($f in 'Avx', 'Avx2', 'Fma', 'Avx512F', 'Avx512BW', 'AvxVnni') {
        $type = "System.Runtime.Intrinsics.X86.$f" -as [type]
        $features[$f] = [bool]($type -and $type::IsSupported)
    }
} else {
    Add-Type -Namespace IrisHw -Name Kernel -MemberDefinition '[DllImport("kernel32.dll")] public static extern bool IsProcessorFeaturePresent(uint feature);'
    $features['Avx'] = [IrisHw.Kernel]::IsProcessorFeaturePresent(39)
    $features['Avx2'] = [IrisHw.Kernel]::IsProcessorFeaturePresent(40)
    $features['Avx512F'] = [IrisHw.Kernel]::IsProcessorFeaturePresent(41)
    $features['note'] = 'Windows PowerShell 5.1: FMA and VNNI not detectable here'
}

# --- memory, and how fast it can be read (the output layer of the model reads 30 MB per word, so this sets the speed)
$system = Get-CimInstance Win32_ComputerSystem
$modules = @(Get-CimInstance Win32_PhysicalMemory)
$memoryType = switch (($modules | Select-Object -First 1).SMBIOSMemoryType) {
    20 { 'DDR' } 21 { 'DDR2' } 24 { 'DDR3' } 26 { 'DDR4' } 34 { 'DDR5' } 35 { 'LPDDR5' } 30 { 'LPDDR4' } default { 'unknown' }
}
$speeds = $modules | ForEach-Object { if ($_.ConfiguredClockSpeed) { $_.ConfiguredClockSpeed } else { $_.Speed } }
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

public static class IrisHwMemory
{
    // GB/s of reading 256 MB (more than any processor cache) on the given number of threads; the best of three rounds
    public static double Read(int threads)
    {
        long[] data = new long[32 * 1024 * 1024];
        for (int i = 0; i < data.Length; i += 512) { data[i] = i; }
        double best = 0;
        long sink = 0;
        object gate = new object();
        for (int round = 0; round < 4; round++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                long sum = Sum(data, (int)((long)data.Length * t / threads), (int)((long)data.Length * (t + 1) / threads));
                lock (gate) { sink += sum; }
            });
            double gbs = data.Length * 8.0 / watch.Elapsed.TotalSeconds / 1e9;
            if (round > 0 && gbs > best) { best = gbs; }
        }
        return sink == 1 ? 0 : Math.Round(best, 1);
    }

    // optimized at once (512 = AggressiveOptimization; .NET Framework of Windows PowerShell 5.1 ignores the flag)
    [MethodImpl((MethodImplOptions)512)]
    private static long Sum(long[] data, int from, int to)
    {
        long a = 0, b = 0, c = 0, d = 0;
        for (int i = from; i + 3 < to; i += 4) { a += data[i]; b += data[i + 1]; c += data[i + 2]; d += data[i + 3]; }
        return a + b + c + d;
    }
}
'@
$threadsAll = [Environment]::ProcessorCount
$readOne = [IrisHwMemory]::Read(1)
$readAll = [IrisHwMemory]::Read($threadsAll)

# --- graphics: the real memory size is in the registry (Win32_VideoController stops at 4 GB)
$vram = @{}
$classKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
Get-ChildItem $classKey | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath
    $size = $p.'HardwareInformation.qwMemorySize'
    if (-not $size) { $size = $p.'HardwareInformation.MemorySize' }
    if ($p.DriverDesc -and $size) { $vram[$p.DriverDesc] = [double]$size }
}
$gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object {
    $bytes = if ($vram.ContainsKey($_.Name)) { $vram[$_.Name] } else { [double]$_.AdapterRAM }
    [ordered]@{ name = $_.Name; memory_gb = Round-GB $bytes; driver = $_.DriverVersion }
})
$nvidia = $null
$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($smi) {
    $nvidia = (& $smi.Source --query-gpu=name,memory.total,driver_version,compute_cap --format=csv,noheader 2>$null) -join ' | '
}

# --- disks (fixed drives and the kind of physical disks)
$drives = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
    [ordered]@{ drive = $_.DeviceID; size_gb = Round-GB $_.Size; free_gb = Round-GB $_.FreeSpace }
})
$physical = @(Get-PhysicalDisk | ForEach-Object {
    [ordered]@{ media = "$($_.MediaType)"; bus = "$($_.BusType)"; size_gb = Round-GB $_.Size }
})

# --- system, power, .NET
$os = Get-CimInstance Win32_OperatingSystem
$battery = [bool](Get-CimInstance Win32_Battery)
$plan = (powercfg /getactivescheme 2>$null) -replace '^.*\((.*)\).*$', '$1'
$dotnet = @()
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCommand) {
    $dotnet = @(& $dotnetCommand.Source --list-runtimes 2>$null | Where-Object { $_ -like 'Microsoft.NETCore.App*' } | ForEach-Object { ($_ -split ' ')[1] })
}

$kind = switch ($system.PCSystemType) { 1 { 'desktop' } 2 { 'laptop' } 3 { 'workstation' } default { 'other' } }

$report = [ordered]@{
    iris_hw = 2
    name = $name
    kind = $kind
    cpu = [ordered]@{
        name = ($cpu.Name -replace '\s+', ' ').Trim()
        sockets = $cpus.Count
        cores = [int]($cpus | Measure-Object NumberOfCores -Sum).Sum
        threads = [int]($cpus | Measure-Object NumberOfLogicalProcessors -Sum).Sum
        max_mhz = $cpu.MaxClockSpeed
        l2_kb = $cpu.L2CacheSize
        l3_kb = $cpu.L3CacheSize
        features = $features
    }
    memory = [ordered]@{
        total_gb = Round-GB $system.TotalPhysicalMemory
        modules = $modules.Count
        type = $memoryType
        mhz = ($speeds | Sort-Object -Unique) -join ','
        read_gbs_1_thread = $readOne
        read_gbs_all_threads = $readAll
    }
    gpus = $gpus
    nvidia_smi = $nvidia
    drives = $drives
    physical_disks = $physical
    os = [ordered]@{ name = $os.Caption; version = $os.Version; architecture = $os.OSArchitecture }
    battery = $battery
    power_plan = $plan
    dotnet_runtimes = $dotnet
    powershell = "$($PSVersionTable.PSVersion)"
}

$json = $report | ConvertTo-Json -Depth 5
$folder = Split-Path -Parent $env:IRIS_HW_FILE
$file = Join-Path $folder "iris-hw-$name.txt"
try {
    [System.IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding $false))
} catch {
    $file = Join-Path ([Environment]::GetFolderPath('Desktop')) "iris-hw-$name.txt"
    [System.IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding $false))
}
$copied = $true
try { Set-Clipboard -Value $json -ErrorAction Stop } catch { $copied = $false }

Write-Host ''
Write-Host "Hotovo: $file" -ForegroundColor Green
if ($copied) {
    Write-Host 'Výsledok je v schránke - vlož ho do rozhovoru s Claudom (Ctrl+V).' -ForegroundColor Green
} else {
    Write-Host 'Otvor tento súbor a jeho obsah vlož do rozhovoru s Claudom.'
}
