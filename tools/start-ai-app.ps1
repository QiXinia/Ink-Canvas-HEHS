# start-ai-app.ps1 — 启动本地 AI 网关，并用真实端点启动 InkCanvas。
# 用法：
#   pwsh -File tools/start-ai-app.ps1
#   pwsh -File tools/start-ai-app.ps1 -Endpoint http://127.0.0.1:8787/v1/assistant
#   pwsh -File tools/start-ai-app.ps1 -StartGateway $false
#
# 上游密钥只放在网关进程环境变量里，例如：
#   $env:AI_UPSTREAM_API_KEY = 'sk-xxx'
#   $env:AI_UPSTREAM_MODEL = 'deepseek-chat'
#   $env:AI_UPSTREAM_BASE_URL = 'https://api.deepseek.com/v1'
#   pwsh -File tools/start-ai-app.ps1
param(
    [string]$Endpoint = 'http://127.0.0.1:8787/v1/assistant',
    [string]$AppPath = 'D:\hw\Ink-Canvas-HEHS\artifacts\app\InkCanvas.exe',
    [bool]$StartGateway = $true,
    [string]$Model = $env:AI_UPSTREAM_MODEL
)

if (-not $Model) { $Model = 'gpt-4o-mini' }

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$gatewayScript = Join-Path $PSScriptRoot 'ai-gateway.mjs'

$endpointUri = [uri]$Endpoint
$gatewayPort = $endpointUri.Port
if ($gatewayPort -le 0) {
    $gatewayPort = 80
    if ($endpointUri.Scheme -eq 'https') { $gatewayPort = 443 }
}
$healthUrl = '{0}://{1}:{2}/health' -f $endpointUri.Scheme, $endpointUri.Host, $gatewayPort

function Get-GatewayHealth {
    try {
        $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2 -UseBasicParsing
        if ($response -and $response.ok -eq $true) { return $response }
        return $null
    } catch {
        return $null
    }
}

Write-Host "[AI] 端点: $Endpoint"
Write-Host "[AI] 模型: $Model"

$health = Get-GatewayHealth
if ($null -eq $health) {
    if (-not $StartGateway) {
        Write-Host "[AI] 网关未运行，且已指定 -StartGateway `$false。"
        Write-Host "[AI] 请先在仓库根目录手动运行: node tools/ai-gateway.mjs"
    } else {
        if (-not (Test-Path $gatewayScript)) {
            Write-Host "[AI] 未找到网关脚本: $gatewayScript"
            exit 1
        }

        Write-Host "[AI] 启动网关: node $gatewayScript"
        Start-Process -FilePath 'node' `
            -ArgumentList @($gatewayScript) `
            -WorkingDirectory $repoRoot `
            -WindowStyle Hidden | Out-Null

        for ($attempt = 1; $attempt -le 40; $attempt++) {
            Start-Sleep -Milliseconds 500
            $health = Get-GatewayHealth
            if ($null -ne $health) { break }
        }

        if ($null -eq $health) {
            Write-Host '[AI] 网关 20 秒内未就绪，请检查端口占用或直接用 node 运行网关查看日志。'
            exit 1
        }
    }
}

if ($null -ne $health) {
    Write-Host "[AI] 网关就绪: mode=$($health.mode) model=$($health.model)"
}

if (-not (Test-Path $AppPath)) {
    Write-Host "[AI] 未找到应用: $AppPath（仅打印启动命令，不实际启动）"
} else {
    # 环境变量作为兜底：应用尚未支持 --ai-endpoint/--ai-model 时仍能读到端点与模型。
    $env:AI_ENDPOINT = $Endpoint
    $env:AI_MODEL = $Model
    Start-Process -FilePath $AppPath `
        -ArgumentList @("--ai-endpoint=$Endpoint", "--ai-model=$Model") | Out-Null
    Write-Host "[AI] 已启动应用: $AppPath"
}

Write-Host ''
Write-Host '[AI] 可直接复制的启动命令：'
Write-Host "  node tools/ai-gateway.mjs"
Write-Host ("  & '{0}' --ai-endpoint={1} --ai-model={2}" -f $AppPath, $Endpoint, $Model)
Write-Host ''
Write-Host '[AI] 停止网关：在运行 node tools/ai-gateway.mjs 的窗口按 Ctrl+C。'