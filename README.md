# Auto Packup

A Windows .NET 8 service that backs up `D:\BaiduNetdiskDownload` to standalone ZIP64 archives, with a local Web console at `http://127.0.0.1:5087`.

## Fast Compressed Backups

- Default interval: 30 minutes. Default compressor: 7-Zip 26.03, ZIP/Deflate level 1, four threads (limited to available CPUs). Thread count and fast/balanced mode are configurable in the Web console.
- Includes crash dumps by default to preserve the original all-files behavior. The `Include crash dumps` checkbox can exclude `.mdmp`/`.hprof` paths from future archives; this never deletes source files. The manifest records whether these files were included. Large Java memory dumps can dominate backup time.
- Reads from a temporary VSS snapshot while source applications continue running. The archiver runs below normal process priority. CPU and disk I/O are still used.
- Streams directly into a `.zip.partial` file without first copying the uncompressed tree. Native 7-Zip compresses files in parallel and uses UTF-8 ZIP filenames.
- Checks the file/directory inventory and runs a full 7-Zip CRC test before flushing and publishing the ZIP. The Web console shows compression/verification progress, elapsed time, original bytes and compressed size.
- Keeps the newest three successful ZIP archives. Failed or interrupted tasks never delete successful archives to make room. An abnormal-stop recovery candidate is kept within these three slots while that recovery session is active.
- Releases only its temporary VSS after the archive is published. Completed ZIPs remain readable after restart even if VSS is unavailable. Source-disk failure still requires a backup on another disk to recover.
- A small adjacent `.zip.json` manifest records source, timestamps and counts. Interrupted DB commits can be recovered from a published ZIP plus manifest. Partial ZIPs are not advertised as valid backups.

Old directory archives and persistent VSS snapshots remain accessible and are not automatically deleted during migration. Existing ZIP archives participate in the three-archive rotation. Junctions and symbolic links in the source fail the backup explicitly rather than following data outside the frozen volume.

VSS provides **crash consistency**, not application transaction coordination. The snapshot includes database journals/WAL present inside the source. Database-specific recovery and a restore test are still necessary; the service does not silently stop your database to take a backup.

## Install And Run

Install the .NET 8 SDK and run these commands from an elevated PowerShell:

```powershell
.\scripts\Install-CompressionTool.ps1
dotnet test tests\AutoPackup.Tests.csproj -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o publish
sc.exe create AutoPackup binPath= "D:\auto-packup\publish\AutoPackup.exe" start= auto
sc.exe start AutoPackup
```

The installer script downloads official portable 7-Zip assets, verifies pinned SHA-256 checksums, and puts the executable and license in `tools/7zip`. They are copied into the build/publish output. No system-wide 7-Zip installation is needed.

For an existing installation, stop `AutoPackup` before publishing over its executable, check the publish exit code, then start the service. Preserve `publish/data` and `publish/backups`. The SQLite schema upgrades without dropping history or settings.

```powershell
Stop-Service AutoPackup
dotnet publish -c Release -r win-x64 --self-contained true -o publish
Start-Service AutoPackup
```

VSS requires administrative privileges. The installed service uses LocalSystem. The backup directory must be outside the source directory and have working space for a new archive; restore staging additionally requires the full uncompressed size. The default backup directory is `publish/backups` for the deployed service.

## Restore

Use **Restore** to extract an entire ZIP or selected file/directory into a new destination. ZIP files can also be extracted independently with 7-Zip if the management database is unavailable.

Use **Full restore original directory** only after stopping all applications using the source directory. It first stages the complete backup, then renames the existing source to `.before-full-restore-*` and swaps in the staged directory. The existing directory is retained for rollback. Windows will refuse the directory switch if a terminal or another process still holds a handle in that tree.

Legacy VSS file replacement remains available for old snapshots. Prefer staging and verifying database restores before replacement. Compression format changes do not make previously damaged database contents valid.

## Verification

Tests exercise native ZIP creation/CRC checking, Chinese filenames, empty directories, selective and full restore, cancellation, retention, preservation after compression/VSS failures, interrupted runs, final-file/DB reconciliation, and legacy VSS device renumbering. No test overwrites the production source.
