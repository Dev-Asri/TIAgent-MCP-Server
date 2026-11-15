<#
.SYNOPSIS
    TIAgent CLI - Command Line Interface for AI Agents and Developers
.EXAMPLE
    .\tiagent.ps1 status
    .\tiagent.ps1 send .\my_package.json
    .\tiagent.ps1 command "DB_V90 veri blogunu sil"
    .\tiagent.ps1 delete -Blocks "DB1,DB2"
    .\tiagent.ps1 gsd "v90"
#>
param(
    [Parameter(Position=0)]
    [string]$Command = "status",

    [Parameter(Position=1)]
    [string]$Argument = "",

    [string]$Blocks = "",
    [string]$Tags = "",
    [string]$Devices = "",
    [switch]$ClearMain = $false
)

$BaseUrl = "http://localhost:19520"

function Test-TIAgentOnline {
    try {
        $res = Invoke-RestMethod -Uri "$BaseUrl/api/status" -Method Get -TimeoutSec 2 -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

switch ($Command.ToLowerInvariant()) {
    "status" {
        try {
            $res = Invoke-RestMethod -Uri "$BaseUrl/api/status" -Method Get -TimeoutSec 10
            $res | ConvertTo-Json -Depth 4
        } catch {
            Write-Error "TIAgent port 19520 üzerinde çalışmıyor veya erişilemiyor: $_"
        }
    }

    "dbs" {
        try {
            $res = Invoke-RestMethod -Uri "$BaseUrl/api/dbs" -Method Get -TimeoutSec 10
            $res | ConvertTo-Json -Depth 4
        } catch {
            Write-Error "TIAgent DB listesi alınamadı: $_"
        }
    }

    "project" {
        try {
            $res = Invoke-RestMethod -Uri "$BaseUrl/api/project" -Method Get -TimeoutSec 10
            $res | ConvertTo-Json -Depth 4
        } catch {
            Write-Error "TIAgent proje genel bakışı alınamadı: $_"
        }
    }

    "send" {
        if (-not (Test-Path $Argument)) {
            Write-Error "Belirtilen paket dosyası bulunamadı: $Argument"
            return
        }
        $json = Get-Content $Argument -Raw -Encoding UTF8
        try {
            $res = Invoke-RestMethod -Uri "$BaseUrl/api/package" -Method Post -Body $json -ContentType "application/json; charset=utf-8" -TimeoutSec 180
            $res | ConvertTo-Json -Depth 4
        } catch {
            Write-Error "Paket gönderilirken hata oluştu: $_"
        }
    }

    "command" {
        if ([string]::IsNullOrWhiteSpace($Argument)) {
            Write-Error "Lütfen çalıştırılacak komut veya talimatı girin."
            return
        }
        $body = @{
            Action = "generate"
            Prompt = $Argument
            AutoApprove = $true
        } | ConvertTo-Json -Depth 3
        try {
            $res = Invoke-RestMethod -Uri "$BaseUrl/api/command" -Method Post -Body $body -ContentType "application/json; charset=utf-8" -TimeoutSec 60
            $res | ConvertTo-Json -Depth 4
        } catch {
            Write-Error "Komut gönderilirken hata: $_"
        }
    }

    "delete" {
        $bList = if ($Blocks) { @($Blocks.Split(',') | ForEach-Object { $_.Trim() }) } else { @() }
        $tList = if ($Tags) { @($Tags.Split(',') | ForEach-Object { $_.Trim() }) } else { @() }
        $dList = if ($Devices) { @($Devices.Split(',') | ForEach-Object { $_.Trim() }) } else { @() }
        $body = @{
            Action = "delete"
            BlocksToDelete = $bList
            TagTablesToDelete = $tList
            DevicesToDelete = $dList
            ClearMainNetworks = [bool]$ClearMain
        } | ConvertTo-Json -Depth 3
        try {
            $res = Invoke-RestMethod -Uri "$BaseUrl/api/delete" -Method Post -Body $body -ContentType "application/json; charset=utf-8" -TimeoutSec 60
            $res | ConvertTo-Json -Depth 4
        } catch {
            Write-Error "Silme komutu gönderilirken hata: $_"
        }
    }

    "gsd" {
        try {
            $q = [System.Uri]::EscapeDataString($Argument)
            $res = Invoke-RestMethod -Uri "$BaseUrl/api/gsd?q=$q" -Method Get -TimeoutSec 30
            $res | ConvertTo-Json -Depth 4
        } catch {
            Write-Error "GSDML sorgusu başarısız: $_"
        }
    }

    default {
        Write-Host "Kullanım: .\tiagent.ps1 [status | dbs | project | send <dosya.json> | command <metin> | delete -Blocks <adlar> | gsd <anahtar>]"
    }
}
