/*
    RecordFlow – SQL Server backup script (self-hosted SQL Server / SQL Server on a VM).

    Azure SQL Database is backed up automatically (point-in-time restore, 7–35 days, plus optional
    long-term retention) – configure retention in the Azure portal instead of running this script.

    Recommended schedule (SQL Server Agent):
      • FULL      nightly
      • DIFF      every 6 hours
      • LOG       every 15 minutes   (database must use the FULL recovery model)
    Copy backups off the server (e.g. Azure Blob Storage with immutability) and test restores monthly.

    Only permanent data lives in this database (accounts, configuration, orders, finalized records,
    audit logs, data-protection keys). Uploaded CSV files are never stored, so they are never in backups.
*/

DECLARE @db sysname = N'RecordFlow';
DECLARE @dir nvarchar(260) = N'D:\Backups\RecordFlow\';      -- change to your backup volume
DECLARE @stamp nvarchar(20) = FORMAT(SYSUTCDATETIME(), 'yyyyMMdd_HHmmss');
DECLARE @file nvarchar(400);

-- One-time: ALTER DATABASE [RecordFlow] SET RECOVERY FULL;

-- Full backup (compressed, checksummed, encrypted with a server certificate)
SET @file = @dir + @db + N'_FULL_' + @stamp + N'.bak';
BACKUP DATABASE @db TO DISK = @file
    WITH COMPRESSION, CHECKSUM, INIT,
         ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = RecordFlowBackupCert),
         STATS = 10;

RESTORE VERIFYONLY FROM DISK = @file WITH CHECKSUM;

-- Transaction log backup (schedule separately, e.g. every 15 minutes)
-- SET @file = @dir + @db + N'_LOG_' + @stamp + N'.trn';
-- BACKUP LOG @db TO DISK = @file WITH COMPRESSION, CHECKSUM,
--     ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = RecordFlowBackupCert);

/*
    One-time setup for encrypted backups (store the certificate backup and password in a vault –
    without them, encrypted backups cannot be restored):

    USE master;
    CREATE MASTER KEY ENCRYPTION BY PASSWORD = '<strong password>';
    CREATE CERTIFICATE RecordFlowBackupCert WITH SUBJECT = 'RecordFlow backup encryption';
    BACKUP CERTIFICATE RecordFlowBackupCert TO FILE = 'D:\Keys\RecordFlowBackupCert.cer'
        WITH PRIVATE KEY (FILE = 'D:\Keys\RecordFlowBackupCert.pvk', ENCRYPTION BY PASSWORD = '<strong password>');

    Restore example:
    RESTORE DATABASE [RecordFlow] FROM DISK = N'D:\Backups\RecordFlow\RecordFlow_FULL_20260101_020000.bak'
        WITH NORECOVERY, REPLACE;
    RESTORE LOG [RecordFlow] FROM DISK = N'...trn' WITH RECOVERY;
*/
