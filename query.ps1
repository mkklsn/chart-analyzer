<#
.SYNOPSIS
  Runs a read-only query against the deployed Chart Analyzer database.

.DESCRIPTION
  Connects as claude_reader, which can only read tables (db_datareader), so nothing can be changed by accident.
  Each run makes sure this machine's public IP is allowed through the SQL firewall (rule "dev-<computer name>"),
  so it keeps working when the home IP changes.

  One-time setup (already done): run with -Setup. It creates the reader user with a generated password,
  stored in this project's user secrets as Query:ReaderPassword. It connects once as the SQL admin to do that.

.EXAMPLE
  ./query.ps1 "SELECT TOP 20 * FROM Alerts ORDER BY SentAt DESC"
#>
param(
    [Parameter(Position = 0)][string]$Sql,
    [switch]$Setup,
    [string]$Subscription = $env:CHARTANALYZER_SUBSCRIPTION,   # empty = current az account
    [string]$ResourceGroup = 'rg-chartanalyzer',
    [string]$Database = 'chartanalyzer'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-Location $PSScriptRoot

if (-not $Setup -and -not $Sql) { throw 'Pass a query, e.g. ./query.ps1 "SELECT TOP 20 * FROM Alerts ORDER BY SentAt DESC"' }

function Get-UserSecrets {
    $secrets = @{}
    foreach ($line in dotnet user-secrets list) {
        if ($line -match '^(.+?) = (.*)$') { $secrets[$Matches[1]] = $Matches[2] }
    }
    return $secrets
}

if ($Subscription) { az account set --subscription $Subscription }
$server = az sql server list -g $ResourceGroup --query "[0].{name:name,fqdn:fullyQualifiedDomainName}" -o json | ConvertFrom-Json

# Allow this machine's current IP; updates the rule in place when the IP changes.
$ip = (Invoke-RestMethod https://api.ipify.org).Trim()
$rule = "dev-$($env:COMPUTERNAME.ToLower())"
$existing = az sql server firewall-rule list -g $ResourceGroup -s $server.name --query "[?name=='$rule'].startIpAddress" -o tsv
if ($existing -ne $ip) {
    az sql server firewall-rule create -g $ResourceGroup -s $server.name -n $rule --start-ip-address $ip --end-ip-address $ip --output none
    Write-Host "Firewall rule $rule set to $ip" -ForegroundColor DarkGray
}

$secrets = Get-UserSecrets

if ($Setup) {
    if (-not $secrets['Query:ReaderPassword']) {
        # Letters and digits only, so it's safe inside SQL and connection strings.
        $chars = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'
        $password = 'Aa1' + -join (1..29 | ForEach-Object { $chars[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($chars.Length)] })
        dotnet user-secrets set Query:ReaderPassword $password | Out-Null
        $secrets['Query:ReaderPassword'] = $password
    }
    $create = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'claude_reader')
    CREATE USER claude_reader WITH PASSWORD = '$($secrets['Query:ReaderPassword'])';
ALTER ROLE db_datareader ADD MEMBER claude_reader;
"@
    $env:SQLCMDPASSWORD = $secrets['Deploy:SqlAdminPassword']
    try { sqlcmd -S $server.fqdn -d $Database -U chartadmin -N -b -l 60 -Q $create }
    finally { Remove-Item Env:SQLCMDPASSWORD }
    Write-Host 'Read-only user claude_reader is ready.'
    if (-not $Sql) { return }
}

# Password via environment variable so it never appears in the command line.
$env:SQLCMDPASSWORD = $secrets['Query:ReaderPassword']
try { sqlcmd -S $server.fqdn -d $Database -U claude_reader -N -b -l 60 -W -s '|' -Q "SET NOCOUNT ON; $Sql" }
finally { Remove-Item Env:SQLCMDPASSWORD }
