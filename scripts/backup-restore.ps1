[CmdletBinding()]
param(
    [string]$DatabaseName = 'lms',
    [string]$DatabaseUser = 'postgres',
    [string]$DatabaseHost = 'localhost',
    [int]$DatabasePort = 5432,
    [string]$BackupPath = '.codex-temp\lms-backup.dump',
    [string]$RestoreDatabaseName,
    [switch]$ExecuteRestore
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command pg_dump -ErrorAction SilentlyContinue)) { throw 'pg_dump is required on PATH.' }
if (-not (Get-Command pg_restore -ErrorAction SilentlyContinue)) { throw 'pg_restore is required on PATH.' }
if (-not (Get-Command psql -ErrorAction SilentlyContinue)) { throw 'psql is required on PATH.' }
$databasePassword = $env:LMS_DB_PASSWORD
if ([string]::IsNullOrWhiteSpace($databasePassword)) { throw 'Set LMS_DB_PASSWORD in the process environment; do not put the password in this script.' }

$env:PGPASSWORD = $databasePassword
try {
    if (-not $ExecuteRestore) {
        $backupDirectory = Split-Path -Parent $BackupPath
        if ($backupDirectory) { New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null }
        pg_dump --format=custom --file $BackupPath --host $DatabaseHost --port $DatabasePort --username $DatabaseUser --dbname $DatabaseName
        if (-not (Test-Path -LiteralPath $BackupPath)) { throw "Backup was not created at $BackupPath." }
        Write-Output "Backup created: $((Resolve-Path -LiteralPath $BackupPath).Path)"
        Write-Output 'Evidence: record the UTC time, database name, backup path, file size, and pg_dump exit code.'
        return
    }

    if ([string]::IsNullOrWhiteSpace($RestoreDatabaseName)) { throw 'RestoreDatabaseName is required with -ExecuteRestore.' }
    if (-not (Test-Path -LiteralPath $BackupPath)) { throw "Backup file not found: $BackupPath" }
    Write-Warning "This will replace objects in database '$RestoreDatabaseName' using the supplied backup. Run only against an approved restore target."
    pg_restore --clean --if-exists --no-owner --host $DatabaseHost --port $DatabasePort --username $DatabaseUser --dbname $RestoreDatabaseName $BackupPath
    $verification = psql --host $DatabaseHost --port $DatabasePort --username $DatabaseUser --dbname $RestoreDatabaseName --tuples-only --command "SELECT current_database(), EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory');"
    Write-Output "Restore verification: $verification"
    Write-Output 'Evidence: record the UTC time, restore target, backup checksum, pg_restore exit code, and verification output.'
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
