<#
.SYNOPSIS
  Builds the container image in Azure and deploys Chart Analyzer to Azure Container Apps.

.DESCRIPTION
  Safe to run again: each run builds a new image and updates the app in place.

  One-time setup. Secrets live in this project's user secrets, never in files:
    dotnet user-secrets set Telegram:BotToken "<token from @BotFather>"
    dotnet user-secrets set Telegram:ChatId <your chat id>
  The SQL admin password is generated on the first run and stored there too.
#>
param(
    [string]$Subscription = $env:CHARTANALYZER_SUBSCRIPTION,   # empty = current az account
    [string]$ResourceGroup = 'rg-chartanalyzer',
    [string]$Location = 'northeurope',                                # not a US region: Binance blocks the US
    [string]$SqlLocation = 'uksouth',                                 # North/West Europe weren't accepting new SQL servers
    [string]$TimeZone = 'Europe/London'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-Location $PSScriptRoot

function Get-UserSecrets {
    $secrets = @{}
    foreach ($line in dotnet user-secrets list) {
        if ($line -match '^(.+?) = (.*)$') { $secrets[$Matches[1]] = $Matches[2] }
    }
    return $secrets
}

$secrets = Get-UserSecrets
foreach ($name in 'Telegram:BotToken', 'Telegram:ChatId') {
    if (-not $secrets[$name]) { throw "User secret '$name' is missing. See the setup steps at the top of this script." }
}
if (-not $secrets['Deploy:SqlAdminPassword']) {
    # Letters and digits only, so it's safe inside a connection string.
    $chars = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'
    $password = 'Aa1' + -join (1..29 | ForEach-Object { $chars[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($chars.Length)] })
    dotnet user-secrets set Deploy:SqlAdminPassword $password | Out-Null
    $secrets['Deploy:SqlAdminPassword'] = $password
}

Write-Host "==> Subscription and resource providers"
if ($Subscription) { az account set --subscription $Subscription }
foreach ($ns in 'Microsoft.App', 'Microsoft.ContainerRegistry', 'Microsoft.OperationalInsights', 'Microsoft.Sql', 'Microsoft.ManagedIdentity') {
    az provider register --namespace $ns --wait --output none
}
az group create --name $ResourceGroup --location $Location --output none

Write-Host "==> Container registry"
$registry = az deployment group create --resource-group $ResourceGroup --template-file infra/registry.bicep `
    --query properties.outputs --output json | ConvertFrom-Json

Write-Host "==> Building image in Azure"
$tag = Get-Date -Format 'yyyyMMddHHmmss'
$image = "$($registry.loginServer.value)/chart-analyzer:$tag"
az acr build --registry $registry.name.value --image "chart-analyzer:$tag" . --output none

Write-Host "==> Deploying app, database and logs"
az deployment group create --resource-group $ResourceGroup --template-file infra/main.bicep --output none --parameters `
    registryName=$($registry.name.value) `
    image=$image `
    sqlAdminPassword=$($secrets['Deploy:SqlAdminPassword']) `
    telegramBotToken=$($secrets['Telegram:BotToken']) `
    telegramChatId=$($secrets['Telegram:ChatId']) `
    timeZone=$TimeZone `
    sqlLocation=$SqlLocation

Write-Host "==> Done. Deployed $image"
Write-Host "Follow the logs with:"
Write-Host "  az containerapp logs show -g $ResourceGroup -n ca-chartanalyzer --follow"
