<#
.SYNOPSIS
    Zistí parametre počítača pre IRIS: procesor (a či vie AVX2 / AVX-512), pamäť, grafickú kartu, disky a systém.

.DESCRIPTION
    Nezbiera nič osobné: žiadne meno počítača ani používateľa, sériové čísla, sieťové adresy ani kľúče.
    Výsledok uloží do súboru iris-hw-<Nazov>.txt vedľa skriptu a skopíruje ho do schránky – stačí ho vložiť do
    rozhovoru. Nič neinštaluje a nič neposiela.

.PARAMETER Nazov
    Ako tento typ počítača nazveš, napr. notebook, stolovy, kancelaria-a (písmená, číslice, pomlčka).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\iris-hw.ps1 -Nazov notebook
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Nazov
)

$ErrorActionPreference = 'SilentlyContinue'
$name = ($Nazov -replace '[^\p{L}\p{Nd}_-]', '-').Trim('-')
if (-not $name) { $name = 'pc' }

function Round-GB([double]$bytes) { [math]::Round($bytes / 1GB, 1) }

# --- processor and its vector instructions (what the IRIS kernels use: AVX2 + FMA, later AVX-512 / VNNI)
$cpus = @(Get-CimInstance Win32_Processor)
$cpu = $cpus | Select-Object -First 1
$features = [ordered]@{}
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $x86 = 'System.Runtime.Intrinsics.X86'
    foreach ($f in 'Avx', 'Avx2', 'Fma', 'Avx512F', 'Avx512BW', 'AvxVnni') {
        $type = "$x86.$f" -as [type]
        $features[$f] = [bool]($type -and $type::IsSupported)
    }
} else {
    Add-Type -Namespace IrisHw -Name Kernel -MemberDefinition '[DllImport("kernel32.dll")] public static extern bool IsProcessorFeaturePresent(uint feature);'
    $features['Avx'] = [IrisHw.Kernel]::IsProcessorFeaturePresent(39)
    $features['Avx2'] = [IrisHw.Kernel]::IsProcessorFeaturePresent(40)
    $features['Avx512F'] = [IrisHw.Kernel]::IsProcessorFeaturePresent(41)
    $features['note'] = 'Windows PowerShell 5.1: FMA and VNNI not detectable here'
}

# --- memory
$system = Get-CimInstance Win32_ComputerSystem
$modules = @(Get-CimInstance Win32_PhysicalMemory)
$memoryType = switch (($modules | Select-Object -First 1).SMBIOSMemoryType) {
    20 { 'DDR' } 21 { 'DDR2' } 24 { 'DDR3' } 26 { 'DDR4' } 34 { 'DDR5' } 35 { 'LPDDR5' } 30 { 'LPDDR4' } default { 'unknown' }
}
$speeds = $modules | ForEach-Object { if ($_.ConfiguredClockSpeed) { $_.ConfiguredClockSpeed } else { $_.Speed } }

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
    iris_hw = 1
    name = $name
    kind = $kind
    cpu = [ordered]@{
        name = ($cpu.Name -replace '\s+', ' ').Trim()
        sockets = $cpus.Count
        cores = ($cpus | Measure-Object NumberOfCores -Sum).Sum
        threads = ($cpus | Measure-Object NumberOfLogicalProcessors -Sum).Sum
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
    }
    gpus = $gpus
    nvidia_smi = $nvidia
    drives = $drives
    physical_disks = $physical
    os = [ordered]@{ name = $os.Caption; version = $os.Version; architecture = $os.OSArchitecture }
    battery = $battery
    power_plan = $plan
    dotnet_runtimes = $dotnet
}

$json = $report | ConvertTo-Json -Depth 5
$folder = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$file = Join-Path $folder "iris-hw-$name.txt"
[System.IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding $false))
$copied = $true
try { Set-Clipboard -Value $json -ErrorAction Stop } catch { $copied = $false }

Write-Host ''
Write-Host "Hotovo: $file" -ForegroundColor Green
if ($copied) {
    Write-Host 'Obsah je aj v schránke – vlož ho do rozhovoru (Ctrl+V).'
} else {
    Write-Host 'Otvor súbor a jeho obsah vlož do rozhovoru.'
}
