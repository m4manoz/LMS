# Phase 12 backup and restore runbook

The PostgreSQL database is the source of truth. Backups must be created with `pg_dump` custom format and restored into an approved, isolated target before any production recovery decision.

## Backup

Set the password only in the current process environment, then run:

```powershell
$env:LMS_DB_PASSWORD = '<password from the managed secret store>'
& .\scripts\backup-restore.ps1 -BackupPath '.codex-temp\lms-backup-2026-09-30.dump'
```

Record the UTC timestamp, database name, backup path, file size, checksum, and command exit code. Store the dump outside the application host according to the organization’s backup policy.

## Restore rehearsal

Create a disposable PostgreSQL database and run the same script with an explicit target:

```powershell
& .\scripts\backup-restore.ps1 -BackupPath '.codex-temp\lms-backup-2026-09-30.dump' -RestoreDatabaseName 'lms_restore_rehearsal' -ExecuteRestore
```

The restore switch is intentionally explicit because `pg_restore --clean` removes conflicting objects in the named restore target. Never point it at a production database without an approved change record.

Verify `/health/ready`, the latest EF migration, tenant login, an authorized tenant report, an offline-device list, and the operations summary. Capture the restore duration, migration version, row-count spot checks, tenant-isolation result, and the operator/sign-off.

Production deployment should use a managed secret store for `LMS_DB_PASSWORD`, JWT signing keys, provider webhook secrets, and the shared `DataProtection:KeyStoragePath`; the local `appsettings.json` values are development-only.
