# Auto Packup

Windows .NET 8 backup service for `D:\BaiduNetdiskDownload`.

The default schedule is every 30 minutes. Each new backup is a persistent, crash-consistent Windows VSS snapshot. The service keeps the newest three successful VSS snapshots and releases older snapshots with their VSS IDs. It does not copy or compress the source tree during snapshot creation, so creating a backup is normally close to instantaneous and uses only VSS copy-on-write space.

The Web management page listens on `http://127.0.0.1:5087` and supports configuration, manual snapshots, VSS storage reporting, snapshot browsing, restore to a new directory, deletion, and logs. Existing ZIP and directory backups remain readable as legacy archives.

## Build and run

Install the .NET 8 SDK, then run as an administrator:

```powershell
dotnet restore
dotnet build -c Release
dotnet run
```

VSS requires administrator privileges. The service account must be able to read the source volume and access the configured backup data directory. VSS snapshots remain on the source volume and do not protect against source-disk failure.

## Tests

```powershell
dotnet test tests\AutoPackup.Tests.csproj -c Release
```

The test suite covers snapshot retention, restore, path traversal, restart reconciliation, deletion failures, VSS storage retry, and legacy ZIP compatibility.

## Register as a Windows service

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish
sc.exe create AutoPackup binPath= "D:\auto-packup\publish\AutoPackup.exe" start= auto
sc.exe description AutoPackup "Automatic VSS snapshot backups"
sc.exe start AutoPackup
```

Stop and remove the service:

```powershell
sc.exe stop AutoPackup
sc.exe delete AutoPackup
```
