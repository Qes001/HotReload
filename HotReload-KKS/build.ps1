param(
    [string]$GameDir = '',
    [switch]$BootOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# 同一份 KKS payload 适用于下面所有安装（Sideloader 19.6 / 20.0 结构一致）
$knownInstalls = @(
    'E:\game\GDAkk\KoikatuSunshine',
    'E:\game\GDAkk\Koikatsu Sunshine EX BetterRepack R12'
)

if (-not $GameDir) {
    foreach ($candidate in $knownInstalls) {
        if (Test-Path (Join-Path $candidate 'KoikatsuSunshine_Data\Managed\mscorlib.dll')) {
            $GameDir = $candidate
            break
        }
    }
    if (-not $GameDir) {
        throw '没找到 KKS 游戏根目录，请用 -GameDir 指定（包含 KoikatsuSunshine_Data 的那个目录）'
    }
}

$managed    = Join-Path $GameDir 'KoikatsuSunshine_Data\Managed'
$core       = Join-Path $GameDir 'BepInEx\core'
$pluginsDir = Join-Path $GameDir 'BepInEx\plugins'
$payloadDir = Join-Path $GameDir 'BepInEx\hotreload'

# 用系统自带的 csc + 游戏自带的引用程序集编译，不需要装 .NET SDK
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe: $csc" }

# KKS 是 Unity 2019 的模块化程序集，UnityEngine.dll 只是门面，必须按模块引用
$refs = @(
    (Join-Path $managed 'mscorlib.dll'),
    (Join-Path $managed 'System.dll'),
    (Join-Path $managed 'System.Core.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managed 'UnityEngine.InputLegacyModule.dll'),
    (Join-Path $managed 'UnityEngine.TextRenderingModule.dll'),
    (Join-Path $managed 'UnityEngine.UIModule.dll'),
    (Join-Path $managed 'UnityEngine.AssetBundleModule.dll'),
    (Join-Path $managed 'UnityEngine.UI.dll'),
    (Join-Path $managed 'Assembly-CSharp.dll'),
    (Join-Path $core    'BepInEx.dll'),
    (Join-Path $core    '0Harmony.dll')
)

foreach ($r in $refs) {
    if (-not (Test-Path $r)) { throw "缺少引用: $r" }
}

$refArgs = @()
foreach ($r in $refs) { $refArgs += ('/r:' + $r) }

# 部署目录：和游戏根目录同构，能直观看到每个文件该放哪
$deployRoot    = Join-Path $root 'deploy'
$deployPlugins = Join-Path $deployRoot 'BepInEx\plugins'
$deployPayload = Join-Path $deployRoot 'BepInEx\hotreload'

New-Item -ItemType Directory -Force -Path $deployPlugins, $deployPayload, $payloadDir | Out-Null

# --- Boot：改了壳本身才需要重编 ---
Write-Host '== 编译 Boot ==' -ForegroundColor Cyan
$bootDll = Join-Path $deployPlugins 'HotReload.Boot.dll'
& $csc /nologo /noconfig /nostdlib+ /target:library ("/out:" + $bootDll) `
    $refArgs (Join-Path $root 'Boot\Boot.cs')
if ($LASTEXITCODE -ne 0) { throw 'Boot 编译失败' }

# --- Payload：日常迭代只跑这一段 ---
$payloadDll = Join-Path $deployPayload 'HotReload.Payload.dll'
if (-not $BootOnly) {
    Write-Host '== 编译 Payload ==' -ForegroundColor Cyan

    # 构建时间戳：插件启动时会打进日志，用来确认游戏里跑的是哪一次编译
    $stampFile = Join-Path $root 'Payload\BuildStamp.g.cs'
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    @"
namespace HotReload.Payload
{
    internal static class BuildStamp
    {
        public const string Value = "$stamp";
    }
}
"@ | Set-Content -LiteralPath $stampFile -Encoding UTF8

    $sources = @(Get-ChildItem (Join-Path $root 'Payload') -Filter '*.cs' -Recurse | ForEach-Object { $_.FullName })

    & $csc /nologo /noconfig /nostdlib+ /target:library ("/out:" + $payloadDll) `
        $refArgs $sources
    if ($LASTEXITCODE -ne 0) { throw 'Payload 编译失败' }
}

# --- 同步到所有已知 KKS 安装 ---
$targets = @($GameDir)
foreach ($extra in $knownInstalls) {
    if ((Test-Path (Join-Path $extra 'BepInEx\core\BepInEx.dll')) -and ($targets -notcontains $extra)) {
        $targets += $extra
    }
}

foreach ($target in $targets) {
    $tp = Join-Path $target 'BepInEx\plugins'
    $tl = Join-Path $target 'BepInEx\hotreload'
    New-Item -ItemType Directory -Force -Path $tp, $tl | Out-Null

    Copy-Item -LiteralPath $bootDll -Destination $tp -Force
    Write-Host ("Boot    -> " + (Join-Path $tp 'HotReload.Boot.dll'))

    if (-not $BootOnly) {
        Copy-Item -LiteralPath $payloadDll -Destination $tl -Force
        Write-Host ("Payload -> " + (Join-Path $tl 'HotReload.Payload.dll'))
    }
}

if (-not $BootOnly) {
    Write-Host '游戏运行中也能覆盖，payload 变化会自动重载（壳的 AutoReload=true）' -ForegroundColor Green
}
