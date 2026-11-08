# تقرير تسليم المصدر والتدقيق الفني — DateERP 1.50.123

**وقت إنشاء حزمة التسليم:** 2026-11-08 01:28:56 +03 (+03، Asia/Aden)
**آخر تعديل مصدر مرصود:** 2026-11-08 00:40 (+03)، ملف اختبار القبول `FairDistributionAcceptanceTests.cs`.  
**نوع الحزمة:** ZIP مصدر كامل للمراجعة، لا حزمة تثبيت ولا نشر تنفيذي.

## بيانات النسخة والمصدر
- رقم الإصدار في `VERSION.txt` وDesktop: `1.50.123`.
- مؤشر الفرع في `.git/HEAD`: `arena/01a0ac34-offtt`.
- commit المعنون في `packed-refs`: `20fc110cf3536f9b747b18fe20aac428dc55aa9a`.
- **قيد مهم:** `git status` و`git rev-parse` يفشلان برسالة `fatal: not a git repository`. لذلك الفرع/الـcommit أعلاه بيانات مؤشرات فقط؛ لا أستطيع تأكيد أن الشجرة نظيفة أو أن تعديلات working tree ممثلة في commit. تم تضمين لقطة الشجرة الفعلية وقت الإنشاء لا `.git`.
- يحتوي المشروع على **16 ملف مشروع `.csproj`**؛ منها 13 ضمن `DateERP.sln` و3 مشاريع مساعدة خارج الحل. لم يُستبعد أي مشروع مصدر.

## سلامة المصدر والخصوصية
- حزمة ZIP تتضمن الحل وCore وApplication وInfrastructure وDesktop وEntities وPersistence وملفات التهيئة والتقارير وواجهات XAML والموارد والثيمات والاختبارات وملفات أدوات البناء والنشر وملاحظات الإصدارات والتوثيق.
- استُبعدت مخرجات `bin/obj` والحزم التنفيذية/أرشيفات الإصدارات السابقة ومجلد `.git` غير القابل للتحقق وملفات قواعد البيانات الثنائية. ملفات schema/entities/configurations المصدرية مضمنة. `mfgsystem_local.db` كان فارغاً (0 بايت) ولم يُضمّن.
- امتثالاً لشرط عدم إدراج كلمة مرور مكشوفة، استُبدلت **21 واقعة** للنص الافتراضي hard-coded `[REDACTED-DEFAULT-CREDENTIAL]` في نسخة التدقيق بعلامة `[REDACTED-DEFAULT-CREDENTIAL]`. لا يوجد اتصال إنتاجي أو قاعدة بيانات إنتاج. قيم كلمات المرور الأخرى في الاختبارات بيانات اختبار اصطناعية وليست بيانات اعتماد إنتاج.
- هذا يجعل الحزمة نسخة تدقيق **منقحة لهذا النص فقط** وليست بايت-مطابقة تماماً للـworking tree. لم يُحذف أي ملف اختبار فاشل.

## البناء والاختبارات — النتائج الفعلية
- بناء `DateERP.sln -c Release -p:EnableWindowsTargeting=true`: نجح على working tree، 0 أخطاء.
- المشاريع الثلاثة خارج الحل (`OpenProjectAAA`, `SourceDrawProof`, `UpdateBotAAA`): نجح بناء كل منها منفرداً، 0 أخطاء.
- أُخرج ZIP إلى مسار مؤقت واُستخرج من جديد. بناء الحل على النسخة المنقحة نجح بعد تشغيله تسلسلياً (`-m:1`, `UseSharedCompilation=false`) بـ0 أخطاء و0 تحذيرات؛ الاختبارات المركزة للطلبات/الاستلام المباشر نجحت 5/5. محاولة البناء المتوازي الأولى توقفت عند ترجمة اختبار واحد بـ`csc.dll exit 137` (ضغط موارد)، ثم نجح البناء التسلسلي.
- المجموعة الكاملة على working tree: **920 ناجحاً، 99 فاشلاً، 0 متجاوز، إجمالي 1019**. هذا ليس نجاحاً كاملاً ولا اعتماداً للإنتاج. السجل وTRX مرفقان كما هما.
- لا يوجد تحقق بصري لـWPF على Windows في هذه البيئة.

## اكتمال دورة العمل — تقرير قائم على الملفات والاختبارات لا على الوصف التسويقي

| الخطوة | أدلة المصدر/الاختبارات | التقييم الحالي |
|---|---|---|
| 1. استلام الخام والمعالجة والجاهزية | `ReceivingService`, `ReceivingTreatmentLifecycle`, `ReceivingTreatmentService`; اختبارات Receiving وRawTreatment | المسارات موجودة. اختبار `RawTreatmentScenarioTests.P7_Full_Traceability_Chain_Is_Intact` فشل في توقع مرجع الحركة؛ غير مثبت end-to-end. |
| 2. تخطيط الإنتاج والتوزيع العادل | `PlanningService`, `RawPlanningEngine`, `PlanningCapacityEvaluator`; `FairDistributionAcceptanceTests`, `PlanningFifoTests`, `PlanCheckB91Tests` | توجد قواعد/اختبارات، لكن حالات عدالة وطاقة وFIFO ونقص الخام فشلت؛ غير معتمد. |
| 3. حجز الخام عند الاعتماد وصرفه عند بدء التنفيذ | `ProductionOrderService`, `ProductionOrderService.Today`, تخصيصات `ProductionPlanItemAllocation`; اختبارات `ProductionOrderScreenTests`, `InventoryLedgerAuditTests` | توجد مسارات تنفيذ وحركات، لكن بعض اختبارات أوامر الإنتاج/الرصيد فشلت؛ التحقق جزئي. |
| 4. ترحيل المتبقي بين الورديات مع عهدة الأمر | حقول `TransferredOutQtyKg/Cartons` و`RemainderChangeRequestId`; `PlanningService.ChangeRequests` | مسار الطلب والاعتماد موجود. لا توجد حالة قبول مخصصة لهذا المسار في المجموعة الحالية؛ **غير متحقق عملياً**. |
| 5. تسجيل الفعلي والإقفال | `ProductionDeliveryService.Actual`; `ActualProductionTests`, `ProductionActualCloseConsolidationTests`, `PlanClosingTests` | الاختبار المركّز للمسار المباشر اجتاز؛ اختبار تجميع الإقفال الكامل فشل؛ تحقق جزئي. |
| 6. فحص الجودة وتصنيف النتائج | `InspectionService`, `QualityGate`; `QualityGateTests`, `B99QualityTests`, `QualityScreenTests` | حراس الجودة موجودة؛ `Passed_Zero_Accepted...` فشل في المجموعة الكاملة، مع نجاحه في الاختبار المركّز السابق؛ النتيجة غير مستقرة وتحتاج إعادة فرز. |
| 7. معالجة الكميات المرفوضة | خدمات الجودة والإتلاف/إعادة التصنيع؛ اختبارات `B99QualityTests`, `PlanClosingTests`, `QualityDeliveryFlowTests` | لم يثبت اجتياز المجموعة الكاملة لمسارات الرفض والتسوية؛ يلزم تدقيق معاملات وأرصدة مخصص. |
| 8. ترحيل التام المقبول إلى المستودعات | `FinishedGoodsService`; `B96DeliveryTests`, `FGReceiveScreenWorkflowTests` | الاستلام المباشر اختُبر مركزياً بنجاح، لكن اختبار B96 شامل فشل عند تهيئة بياناته؛ لا اعتماد كامل. |
| 9. تسليم العملاء من مخازن/دفعات متعددة يدوياً | `CustomerDeliveryService`, `DeliveryPickService`; `DeliveryPickingDirectiveTests`, `DeliveryLineMatchingTests`, `CustomerDeliveryWarehouseVisibilityTests` | عدة حالات عزل/مطابقة/اعتماد فشلت؛ غير معتمد حتى إصلاحها. |
| 10. الإقفال الاستثنائي وإعادة فتح الخطط والأوامر | `PlanClosureService`, `PlanProgressService`, `ProductionOrderService`; `PlanLevelClosingTests`, `ReferentialIntegrityTests`, `OrderReversalAuditTests` | توجد خدمات واختبارات؛ حالات إقفال/عكس/إعادة فتح فشلت؛ مراجعة لازمة. |
| 11. تتبع الدفعة الخام إلى العميل والعكس | `InventoryIntegrityService`, `AuditService`, lot/order references; `ItemTraceabilityTests`, `RawTreatmentScenarioTests`, `InventoryLedgerAuditTests` | عدة اختبارات تتبع/دفتر أستاذ فشلت؛ السلسلة غير مثبتة بالكامل. |
| 12. التدقيق والصلاحيات والفصل بين التنفيذ والاعتماد | `AuditService`, `PermissionService`, Workflow/Four-Eyes; `UsersSecurityDelegationAuditTests`, `PermissionsSystemTests`, `AuditCredentialRedactionTests` | توجد آليات واختبارات، لكن حالات RBAC والتدقيق فشلت؛ لا يمكن إعلان اكتمال الفصل أو التدقيق. |

**الخلاصة التشغيلية:** البنية والمكونات الأساسية موجودة، لكن الاختبارات الفاشلة تشمل مراحل متباعدة من الدورة، لذا لا يوجد دليل كافٍ لاعتماد اكتمال الدورة end-to-end قبل معالجة 99 إخفاقاً وإعادة الاختبار.

## تعليمات البناء المستقل
راجع `RUN_INSTRUCTIONS_AR.md` داخل ZIP. تتطلب WPF بيئة Windows للتشغيل/الفحص البصري؛ لا تتضمن الحزمة قاعدة بيانات جاهزة أو إعدادات اتصال. أعد إنشاء قاعدة محلية فارغة ولا تستخدم بيانات إنتاج.
