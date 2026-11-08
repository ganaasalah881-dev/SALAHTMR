# تعليمات البناء المستقل
المتطلبات: Windows 10/11 x64 لتشغيل WPF، .NET 8 SDK، وNuGet لأول Restore. لا توجد قاعدة إنتاج أو إعداد اتصال مضمن.
```powershell
dotnet restore DateERP.sln
dotnet build DateERP.sln --configuration Release
dotnet test tests/DatesErp.Tests/DatesErp.Tests.csproj --configuration Release
```
المشاريع المساعدة خارج الحل:
```powershell
dotnet build tools/OpenProjectAAA/OpenProjectAAA.csproj -c Release
dotnet build tools/SourceDrawProof/SourceDrawProof.csproj -c Release
dotnet build tools/UpdateBotAAA/UpdateBotAAA.csproj -c Release
```
شغّل Desktop على Windows بعد إنشاء قاعدة محلية وإعداد اعتماد محلي آمن. لا تستخدم بيانات إنتاج أو النسخة المنقحة للنشر المباشر. سجلات البناء والاختبارات وTRX تحت updates/source-audit_v1.50.123/.
