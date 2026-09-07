# Auto Packup

Windows .NET 8 自动快照备份服务。默认备份 `D:\BaiduNetdiskDownload`，每 30 分钟运行一次，保留最近 3 份成功快照。管理端监听 `http://127.0.0.1:5087`。

## 构建与运行

安装 .NET 8 SDK 后执行：

```powershell
dotnet restore
dotnet run
```

VSS 需要管理员权限。服务账户必须能读取源目录并写入备份目录。

## 注册 Windows 服务

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish
sc.exe create AutoPackup binPath= "D:\auto-packup\publish\AutoPackup.exe" start= auto
sc.exe start AutoPackup
```

停止/卸载：

```powershell
sc.exe stop AutoPackup
sc.exe delete AutoPackup
```
