# تقرير فحص جاهزية DateERP — الإصدار 1.50.109

**تاريخ الفحص:** 3 نوفمبر 2026  
**المسار المفحوص:** `/home/user/OFFTT`  
**الخلاصة:** **الجاهزية الهندسية المقدّرة 74% — غير موصى بإطلاقه إنتاجياً الآن.** هذا تقدير مراجعة لا شهادة اعتماد.

## طريقة احتساب النسبة

مؤشر مرجّح معلن لتجنب إعطاء انطباع أن نجاح البناء وحده يعني جاهزية:

| المحور | النتيجة | الوزن | النقاط |
|---|---:|---:|---:|
| بناء الحل الكامل Release | 100% (كل مشاريع الحل بُنيت، 0 أخطاء) | 20% | 20.0 |
| مجموعة الاختبارات الآلية | 914/1000 = 91.4% | 40% | 36.6 |
| مشغّل القبول التشغيلي الفعلي | 39/45 = 86.7% | 20% | 17.3 |
| تحقق Windows وSQL Server وتشغيل حي | لم يُنفَّذ في هذه البيئة | 20% | 0.0 |
| **الإجمالي** |  | **100%** | **73.9% ≈ 74%** |

عدم تنفيذ محور البيئة الحية لا يعني أن النظام يفشل حتماً هناك؛ يعني أن الأدلة الحالية لا تسمح باحتساب جاهزية إطلاق.

## ما تم فحصه ونتيجته

1. **حل Visual Studio كاملاً:** `dotnet build DateERP.sln -c Release --no-restore -t:Rebuild` — نجح، **0 أخطاء و14 تحذيراً**. شمل Desktop/WPF ومشاريع القبول وWPF smoke؛ هذا تحقق ترجمة فقط وليس تشغيل WPF على Windows.
2. **الاختبارات:** 1000 منفّذ، **914 ناجحاً و86 فاشلاً** (91.4%). الإخفاقات موزعة على 49 مجموعة/صنف اختبار؛ ليست كلها دليلاً على عيب إنتاجي، إذ تتضمن توقعات نصوص/واجهات قديمة، بيانات اختبار ناقصة، وصلاحيات اختبار غير مهيأة. لكنها تعني أن بوابة الاختبار الحالية حمراء وغير صالحة لإعلان إصدار خالٍ من الإخفاقات.
3. **الاختبارات الموجهة لشاشة تسليم العملاء:** 5 سيناريوهات أساسية للالتقاط/الترتيب/التوزيع/التجاوز/التقرير نجحت. من مجموعة التسليم الأوسع ظهرت إخفاقات بسبب صلاحية Delete غير ممنوحة للاختبار، وبيانات عملاء/أرصدة غير متوافقة مع فرضيات الاختبار؛ راجع الملحق. واجتازت 8 اختبارات تصميم/ربط شاشة وتسليم.
4. **مدقق قاعدة الوحدات:** **14/14 مطابق**.
5. **مشغّل القبول الفعلي `AcceptanceRunner`:** **39/45؛ 6 فشل متتابع**. أول فشل هو رفض اعتماد محضر الجودة لأن بيانات السيناريو تتضمن «خرج تام مطابق» كإجمالي متداخل مع نتائج تفصيلية. تبع ذلك تعذر إصدار/استلام التام ثم التسليم. هذا قد يكون سيناريو قبول قديماً غير مواكب لقواعد الفحص الحالية، لكنه يجب تحديثه وإعادة إثبات دورة صحيحة من الشاشة حتى المخزون قبل الإطلاق.
6. **اختبارات SQL Server:** ظهرت خضراء شكلياً، لكن متغير `DATESERP_SQL_CONNECTION` غير مضبوط. الكود يجعل هذه الاختبارات ترجع فوراً دون اتصال عند غياب المتغير؛ لذلك **لم يحدث اختبار SQL Server حقيقي**.
7. **حزمة التحديث 1.50.109:** سلامة ZIP وSHA-256 تحققت، ومكتبتي `MfgSystem.dll` و`DatesErp.Application.dll` تطابقان نواتج البناء. البوت يحتوي المسار الافتراضي `D:\TM1212\TMRSA_1.50.20` وينشئ اختصار `AAA`، ولا يتطلب أوامر تثبيت.

## الإخفاقات الأعلى أولوية

### مانع إطلاق — تدفق الجودة ثم المخزون

في `AcceptanceRunner` تعثر الاعتماد بسبب فحص ذي مجموع نتائج متداخلة. ثم لم يُنشأ استلام تام ولم تدخل كمية للمخزون، ففشل تسليم العميل. يجب تحديث بيانات السيناريو لتستخدم تصنيفات النتائج التفصيلية الصحيحة، ثم إثبات أن حفظ الفحص واعتماده ← استلام التام ← تسليم العميل ينجح فعلياً من دون تجاوز بوابة الجودة.

### فروق كمية/هوية في اختبارات دورة العمل

من نتائج الاختبارات الكاملة ظهرت أمثلة تستلزم التحقيق قبل اعتبار دورة العمل مستقرة:

- `ACC_01`: المتوقع 7000، الفعلي 7140.
- `ACC_03`: المتوقع 600، الفعلي 450.
- `ACC_04`: رفض عدم تطابق 280 كجم مع 28 كرتون × 7.5 = 210 كجم.
- `Full_Traceability_Workflow_End_To_End`: كمية التسليم 3000 مقابل قبول جودة 2990.
- عدة سيناريوهات استلام أفادت بتجاوز كراتين السند للمتبقي في بند أمر التسليم.

بعض هذه النتائج قد تعكس بيانات/افتراضات اختبار متقادمة لا عيباً في النظام؛ يلزم إعادة كتابة السيناريوهات لتطابق تعريفات الوحدات والعبوات الحالية، ثم مراجعة الحراس التي ترفضها. لا ينبغي إضعاف تحقق الكميات أو الجودة لإسكات الاختبارات.

### ثقة الاختبارات

- **43** إخفاقاً surfaced كاختلاف assertion (نص/واجهة/رقم/حالة).
- **23** surfaced كرفض خدمة أو حارس تدفق/بيانات.
- **10** surfaced كصلاحيات/هوية اختبار.
- **9** surfaced كافتراض بيانات أو ملف/تسجيل ناقص.
- **1** surfaced كاستعلام SQLite غير قابل للترجمة.

هذه تصنيفات آلية حسب أول رسالة خطأ، لا حكم يدوي بأن كل إخفاق قديم أو حقيقي.

## حدود الفحص

- البيئة Linux؛ لم يُشغَّل التطبيق أو نوافذ WPF تفاعلياً، ولم تُختبر الطباعة/دقة الشاشة/تسجيل الدخول على جهاز مستخدم.
- لا يوجد اتصال إنتاجي بقاعدة SQL Server ولا تحقق ترقية/استعادة قاعدة واقعية.
- لم يُتحقق من نسخ احتياطي فعلي أو استعادة أو شبكة متعددة المستخدمين.
- لا تُحسب اختبارات SQL Server الخضراء كاختبارات منفذة لأنها تخطّت عند غياب متغير الاتصال.

## قرار الجاهزية

**للتجربة الداخلية/قاعدة معزولة:** مناسب بحذر مع مراقبة، بعد قبول أن بعض الاختبارات لم تنجح.  
**للإطلاق الفعلي على بيانات العملاء:** **غير جاهز بعد**. شرط الانتقال: تصفير إخفاقات مسار القبول التشغيلي، تحليل الـ86 إخفاقاً وإصلاح الحقيقي/تحديث المتقادم مع توثيق السبب، تشغيل SQL Server CI، ثم اختبار Windows يدوي كامل يتضمن سيناريو المسودة (لا خصم) والاعتماد (خصم ذري واحد).

## الملحق — قائمة الاختبارات الفاشلة بالكامل

- `DatesErp.Tests.ProductLotAvailabilityTests.SavePlan_MultipleProductsFromSameLot_Share_The_Lot_Without_Fixed_Equation` — منتجان من دفعة واحدة بلا معادلة ثابتة: تجميع تخصيصات الخطة لتاريخ 01/09/2026 يتجاوز المتاح للدفعة 1 في المخزن 1: المطلوب التراكمي 11,000.0 كجم، المتاح 10,000.0 كجم.
- `DatesErp.Tests.StockBalancePackagingTests.SameProduct_Warehouse_Customer_Lot_With_Different_Packaging_Should_Be_Separate_Balances` — System.InvalidOperationException : Sequence contains no elements
- `DatesErp.Tests.FullCycleMultiCustomerTests.Multi_Customer_Full_Cycle_With_Inventory_Impacts` — عدد كراتين سند الاستلام يتجاوز المتبقي في بند أمر التسليم.
- `DatesErp.Tests.DeliveryPickingDirectiveTests.Pick_06_Approve_RechecksBalance_Concurrency` — DatesErp.Core.Exceptions.PermissionDeniedException : لا تملك صلاحية: Delete على وحدة delivery
- `DatesErp.Tests.DeliveryAuditProbeTests.S7_Update_Same_Doc_And_DeleteDraft` — DatesErp.Core.Exceptions.PermissionDeniedException : لا تملك صلاحية: Delete على وحدة delivery
- `DatesErp.Tests.PlanClosingTests.AutoClose_PlanClosedWhenAllProduced_ReservationsReleased` — Assert.Contains() Failure: Sub-string not found String:    "\ud83d\udd12 أُقفل يوم الإنتاج للأمر 1: المنتَج 10,"··· Not found: "مكتملة"
- `DatesErp.Tests.UsersSecurityDelegationAuditTests.Sec_12_EmergencyFlag_Required_And_Login_Messages_Unified` — Assert.False() Failure Expected: False Actual:   True
- `DatesErp.Tests.UiWiringTests.Every_Xaml_Click_Handler_Exists_And_Is_Not_Empty` — معالجات أزرار فارغة: ItemsView.xaml -> ItemsGrid_DoubleClick WarehouseVariablesView.xaml -> GroupSearch_Click WarehouseVariablesView.xaml -> GroupsGrid_DoubleClick WarehouseVariablesView.xaml -> UnitSearch_Click WarehouseVariablesView.xaml …
- `DatesErp.Tests.DeliveryPickingDirectiveTests.Pick_07_SkippedItems_NeverEnterDocument` — System.InvalidOperationException : Sequence contains more than one element
- `DatesErp.Tests.ProductionOrderScreenTests.Order_Cannot_Exceed_Plan_Remaining_And_Capacity_Is_Checked` — Assert.Contains() Failure: Sub-string not found String:    "⛔ لا يمكن حجز 99,999.0 كجم من الدفعة 1 في"··· Not found: "الطاقة"
- `DatesErp.Tests.PlanCheckB91Tests.LotShortage_Warns_And_Caps_Coverage` — Assert.True() Failure Expected: True Actual:   False
- `DatesErp.Tests.QualityToolbarPermissionTests.Quality_Search_Opens_An_Unambiguous_Match_And_Empty_Search_Gives_Feedback` — Assert.Contains() Failure: Sub-string not found String:    "using System.Collections.ObjectModel;\nusi"··· Not found: "if (term.Length == 0)"
- `DatesErp.Tests.ConversionAutoLinkTests.Migrate_AddsSourceColumn_And_AutoLinks_Conversions_FromNames` — Assert.Contains() Failure: Filter not matched in collection Collection: ["تمت إضافة العمود المفقود: Products.SourceProductId", "تم ربط هوية مصدر الخام فقط: خلاص ممتاز 500جم ← تمر"···, "تم ربط هوية مصدر الخام فقط: سكري فاخر 1كجم ← تمر خ"···]
- `DatesErp.Tests.PlanLevelClosingTests.CloseDay_Guards_OverProduction_Negative_And_Empty` — Assert.Contains() Failure: Sub-string not found String:    "⛔ كمية الإنتاج الفعلية (999,999.0 كجم) مع"··· Not found: "أكبر من المسموح"
- `DatesErp.Tests.PlanIssueB93Tests.All_Customers_Are_Issued_With_Their_Own_Identity` — Assert.Equal() Failure: Values differ Expected: 2 Actual:   1
- `DatesErp.Tests.UsersSecurityDelegationAuditTests.Sec_07_Password_Lifecycle_Reset_Change_Reuse_Lockout` — Assert.Equal() Failure: Values differ Expected: 1 Actual:   0
- `DatesErp.Tests.UsersSecurityDelegationAuditTests.Sec_09_ForgottenPassword_ResetGenerated_SelfChange_History` — DatesErp.Core.Exceptions.PermissionDeniedException : لا تملك صلاحية: Edit على وحدة users
- `DatesErp.Tests.OneClickLaunchTests.Installer_Seeds_The_Separate_Identity_With_Valid_Json` — 2-تنصيب.bat يجب أن يكون CRLF
- `DatesErp.Tests.AuditAndRbacTests.Failed_Logins_Lock_Account_After_5_Attempts` — Assert.False() Failure Expected: False Actual:   True
- `DatesErp.Tests.V15026UpdateAssuranceTests.Single_Click_Begins_Editing_On_Both_Grids` — Assert.Equal() Failure: Values differ Expected: 2 Actual:   1
- `DatesErp.Tests.DeliveryAuditProbeTests.S10_MultiLot_Without_Lot_Rejected_Explicit_Lot_Works` — DatesErp.Core.Exceptions.PermissionDeniedException : لا تملك صلاحية: Create على وحدة planning
- `DatesErp.Tests.DeliveryAuditProbeTests.S2_MultiItems_OneDoc_BothDeducted` — DatesErp.Core.Exceptions.PermissionDeniedException : لا تملك صلاحية: Create على وحدة planning
- `DatesErp.Tests.FullCycleFourCustomersAcceptanceTests.ACC_01_Foundation_DraftLifecycle_And_InventoryParity` — Assert.Equal() Failure: Values are not within 1 decimal place Expected: 7000 (rounded from 7000) Actual:   7140 (rounded from 7140)
- `DatesErp.Tests.ProductionScreensDesignTests.Every_Window_Class_Is_Reachable_From_Another_File` — النافذة «CustomerScopePickerWindow» لا يذكرها أي ملف آخر في الواجهات — نافذة بلا نقطة دخول (كود بلا واجهة). إما ربطها بزر/مسار فتح أو حذفها.
- `DatesErp.Tests.V15026UpdateAssuranceTests.Title_Bar_Stamp_Equals_The_Delivered_Version` — Assert.Contains() Failure: Sub-string not found String:    "<Project Sdk="Microsoft.NET.Sdk">\n\n  <Pro"··· Not found: "<Version>1.50.108</Version>"
- `DatesErp.Tests.DeliveryLineMatchingTests.Explicit_Customer_Is_Honoured_On_Shared_Lot` — Assert.True() Failure Expected: True Actual:   False
- `DatesErp.Tests.FullCycleFourCustomersAcceptanceTests.ACC_03_Orders_Parity_PartialExecution_Completion_NoDouble` — Assert.Equal() Failure: Values are not within 1 decimal place Expected: 600 (rounded from 600) Actual:   450 (rounded from 450)
- `DatesErp.Tests.RawTreatmentScenarioTests.P7_Full_Traceability_Chain_Is_Intact` — Assert.All() Failure: 6 out of 6 items in the collection did not pass. [0]: Item:  InventoryTransaction { CreatedBy = 1, CreatedDate = 2026-11-02T21:13:39.7225183, CustomerId = 1, Id = 2, IsApproved = True, ··· }      Error: Assert.Contains…
- `DatesErp.Tests.DeliveryPickingDirectiveTests.Pick_05_CustomerIsolation_ByProductsExcluded` — System.InvalidOperationException : Sequence contains no elements
- `DatesErp.Tests.DiagnosticIntegrityTests.Deep_Detects_Missing_Permission_Matrix_Row` — Assert.True() Failure Expected: True Actual:   False
- `DatesErp.Tests.PermissionGatingTests.Existing_Matrix_Is_Upgraded_For_Designated_Planning_Edit_Roles` — System.InvalidOperationException : Sequence contains more than one element
- `DatesErp.Tests.ProductionOrderScreenTests.Multi_Customer_Orders_Keep_Customer_And_Product_Identity` — System.InvalidOperationException : Sequence contains no elements
- `DatesErp.Tests.ActualDeliveryFixesTests.Production_Delivery_Screen_Uses_The_Compiled_Order_API` — Assert.Contains() Failure: Sub-string not found String:    "using System.Collections.ObjectModel;\nusi"··· Not found: "GetActualDeliveryOrders((int?)wantedId)"
- `DatesErp.Tests.InspectionScreenTests.Screen_Code_Contains_No_Hardcoded_ResultType_Names_Or_Units` — Assert.DoesNotContain() Failure: Sub-string found                                 ↓ (pos 6084) String: ···"لمعادلة: السليم + المنسم + غير المطابق = "··· Found:  "منسم"
- `DatesErp.Tests.InventoryLedgerAuditTests.Negative_Raw_Balance_Is_Impossible` — التخطيط بأكثر من رصيد الخام مشروع: ⛔ لا يمكن حجز 500.0 كجم من الدفعة 1 في المخزن 1. الفعلي: 1,000.0 | حجز الخطط: 800.0 | الالتزامات الأخرى: 0.0 | المتاح للتخطيط: 200.0 كجم.
- `DatesErp.Tests.StockBalancePackagingTests.Dispensing_From_8kg_Should_Not_Affect_4kg` — System.InvalidOperationException : No service for type 'DatesErp.Application.Services.CustomerDeliveryService' has been registered.
- `DatesErp.Tests.InitOrderGuardTests.ServerSide_Queries_Never_Use_Math_Max_Min` — ExecutionService.cs:168 — Math.Max/Min داخل استعلام يُترجم للخادم: استخدم شرطاً ثلاثياً (a > 0 ? a : 0).
- `DatesErp.Tests.FullCycleFourCustomersAcceptanceTests.ACC_02_Plans_Availability_MultiCustomerShift_And_EditGuards` — منع تحويل خاطئ: الصنف التام غير مرتبط بالصنف الخام المختار بقاعدة تحويل رسمية. راجع مصادر الخام ومعامل التحويل في بطاقة الصنف.
- `DatesErp.Tests.UiWiringTests.Main_Grids_Fill_The_Window_Instead_Of_Fixed_Height` — DeliveryView: لا يوجد جدول DeliveriesGrid
- `DatesErp.Tests.NumberingTests.TreatmentNumbering_StaysUniqueAcrossConcurrentSqliteContexts` — System.InvalidOperationException : The LINQ expression 'DbSet<RawTreatment>()     .OrderBy(r => int.Parse(r.TreatmentNo))' could not be translated. Additional information: Translation of method 'int.Parse' failed. If this method can be mapp…
- `DatesErp.Tests.ClosingPreservesPlanBaselineTests.Partial_Closing_Still_Releases_The_Reservation` — الحجز يجب أن يتحرر بعد الإقفال: قبل 90000 ← بعد 90000
- `DatesErp.Tests.OneClickLaunchTests.First_Launch_Creates_Database_And_Tables_Automatically` — Assert.Contains() Failure: Sub-string not found String:    "using System.IO;\nusing System.Windows;\nus"··· Not found: "bool autoInit = true;"
- `DatesErp.Tests.ReceivingScreenRepairTests.Grid_Contains_Inline_Choice_And_Conditional_DatePicker_Not_A_Master_Product_Flag` — Assert.Contains() Failure: Sub-string not found String:    "using System.Collections.ObjectModel;\nusi"··· Not found: "ItemsGrid.IsReadOnly = !editable"
- `DatesErp.Tests.V15026UpdateAssuranceTests.Delivery_Screen_Is_One_Fixed_Page_Without_Vertical_Scroll` — Assert.Contains() Failure: Sub-string not found String:    "x:Name="SecondaryGrid" MinHeight="66" Max"··· Not found: "MaxHeight="96""
- `DatesErp.Tests.FullCycleFourCustomersAcceptanceTests.ACC_06_FinalReconciliation_PerCustomer_ChainIdentity` — Assert.True() Failure Expected: True Actual:   False
- `DatesErp.Tests.PermissionsSystemTests.Viewer_Can_View_Only_And_Delete_Is_Denied` — Assert.DoesNotContain() Failure: Item found in set Set:   [Tuple ("receiving", "View"), Tuple ("receiving", "Create"), Tuple ("receiving", "Edit"), Tuple ("receiving", "Delete"), Tuple ("receiving", "Approve"), ···] Found: Tuple ("reports",…
- `DatesErp.Tests.UsersSecurityDelegationAuditTests.Sec_03_UserExceptions_Override_Roles_With_Audit` — Assert.Contains() Failure: Sub-string not found String:    "كلمة المرور يجب ألا تقل عن 8 رموز." Not found: "حروف وأرقام"
- `DatesErp.Tests.V15026UpdateAssuranceTests.Update_Bat_Installs_Itself_From_A_Nested_Update_Folder` — System.IO.FileNotFoundException : Could not find file '/home/user/OFFTT/Installer/1-حدّث_وشغل.bat'.
- `DatesErp.Tests.PlanCheckB91Tests.Overload_Shortage_Detected_With_Short_Days` — ⛔ لا يمكن حجز 18,000.0 كجم من الدفعة LOT-B91-SHORT في المخزن 1. الفعلي: 1,000.0 | حجز الخطط: 0.0 | الالتزامات الأخرى: 0.0 | المتاح للتخطيط: 1,000.0 كجم.
- `DatesErp.Tests.StockBalancePackagingTests.Customer_A_And_B_Should_Have_Separate_Balances` — System.InvalidOperationException : Sequence contains no elements
- `DatesErp.Tests.ProductLotAvailabilityTests.SavePlan_AllowsQuantityExceedingRawStock_Because_Water_Gains_Weight` — التخطيط بأكثر من رصيد الخام مشروع: ⛔ لا يمكن حجز 6,000.0 كجم من الدفعة 1 في المخزن 1. الفعلي: 5,000.0 | حجز الخطط: 0.0 | الالتزامات الأخرى: 0.0 | المتاح للتخطيط: 5,000.0 كجم.
- `DatesErp.Tests.PlanningPackSpecsAndLockTests.Capacity_Bar_Is_Compact_For_Weekly_Periods` — Assert.Contains() Failure: Sub-string not found String:    "<UserControl x:Class="DatesErp.Desktop.Vi"··· Not found: "MaxWidth="520""
- `DatesErp.Tests.AuxFlexTests.Closing_Returns_OverIssued_Aux_Automatically` — ⛔ تعذر إقفال اليوم: رصيد الصنف المساعد «ديزل المولد» في مخزن المساعدة (0.0) لا يغطي الإدخال الفعلي (35.0). قيّد صرفه مسبقاً (أو تسوية) ثم أقفل اليوم.
- `DatesErp.Tests.ItemTraceabilityTests.Prevent_Producing_Khalas_From_Sukkari_Without_OfficialDefinition` — Assert.Contains() Failure: Sub-string not found String:    "منع تحويل خاطئ: الصنف التام غير مرتبط بال"··· Not found: "تعريف"
- `DatesErp.Tests.PlanLevelClosingTests.CloseWholePlan_ReturnsRemaindersToRaw_SendsQuality_AndAutoCloses` — Assert.Contains() Failure: Filter not matched in collection Collection: [InventoryTransaction { CreatedBy = 1, CreatedDate = 2026-11-02T21:17:47.5849891, CustomerId = 1, Id = 1, IsApproved = True, ··· }, InventoryTransaction { CreatedBy = 1…
- `DatesErp.Tests.AuditCredentialRedactionTests.LegacyAuditRows_WithEmbeddedHashes_AreScrubbedByReferenceUpgrade` — Assert.Contains() Failure: Filter not matched in collection Collection: ["B110: مُسحت حقول الاعتماد من 1 سجل تدقيق تاريخي — "···]
- `DatesErp.Tests.FullCycleFourCustomersAcceptanceTests.ACC_04_Quality_FgReceipt_PartialDelivery_And_OwnershipGuards` — ⛔ بند فحص الجودة — المقبول: الكمية بالكيلو (280.0 كجم) لا تطابق عدد الكراتين (28 × 7.50 = 210.0 كجم) للصنف «خلاص ممتاز 500جم».
- `DatesErp.Tests.ItemTraceabilityTests.Sukkari_And_Khalas_FullCycle_SingleCustomer_IdentityPreserved` — Assert.Contains() Failure: Sub-string not found String:    "حدد بند أمر التسليم لكل صنف في السند المر"··· Not found: "تحويل"
- `DatesErp.Tests.UnitsGoldenRuleTests.Units_02_FinishedChain_CartonOfficial_KgSupplementary` — ⛔ كمية الإنتاج الفعلية (800.0 كجم) مع ما سبق (0.0) تتجاوز المخطط الكلي (750.0 كجم).
- `DatesErp.Tests.DeliveryLineMatchingTests.Ambiguous_Customer_Is_Rejected_Not_Silently_Guessed` — Assert.True() Failure Expected: True Actual:   False
- `DatesErp.Tests.PlanningFifoTests.Fifo_Warns_When_Planning_Later_While_Earlier_Exists` — System.InvalidOperationException : Sequence contains no elements
- `DatesErp.Tests.ProductLotAvailabilityTests.PerProductAvailability_DeductsOnlySameProductReservations` — Assert.Equal() Failure: Values are not within 1 decimal place Expected: 10000 (rounded from 10000) Actual:   7000 (rounded from 7000)
- `DatesErp.Tests.ProductionActualCloseConsolidationTests.Actual_Close_Provides_Explicit_Search_Save_Print_And_Byproduct_Source_Entry` — Assert.Contains() Failure: Sub-string not found String:    "using System.Collections.ObjectModel;\nusi"··· Not found: "searchText: query, selectFirst: false"
- `DatesErp.Tests.PlanClosingTests.CloseDay_FullScenario_Outputs_Downtimes_Quality` — Assert.Equal() Failure: Values are not within 1 decimal place Expected: 20000 (rounded from 20000) Actual:   17850 (rounded from 17850)
- `DatesErp.Tests.TodayOrdersTests.Approved_Today_Only_Immutable_Order_And_Execution_End_To_End` — أمر لكل عميل وفتحة مع حفظ الارتباط؛ جميع التواريخ اليوم
- `DatesErp.Tests.PlanClosingTests.QualityTwoDays_DeliveryToFinishedAllowed_CustomerDeliveryWaitsApproval` — عدد الكراتين لهذا الاستلام يجب أن يكون 3,750 حسب التوزيع التراكمي لبند التسليم (أو اتركه صفراً للحساب التلقائي).
- `DatesErp.Tests.UiWiringTests.Planning_Save_Uses_Edit_Gate_And_Save_Click_For_F10` — Assert.Contains() Failure: Sub-string not found String:    "using System.Windows;\nusing System.Window"··· Not found: "AddBtn(label, "ErpPrimaryButton", h, "حفظ"···
- `DatesErp.Tests.ItemTraceabilityTests.Quality_Rejects_Product_Not_In_Order` — Assert.Contains() Failure: Sub-string not found String:    "بند الفحص لا يطابق بنداً فعلياً في أمر ال"··· Not found: "ليس من بنود"
- `DatesErp.Tests.PlanIssueB93Tests.Issue_Only_Today_Then_Advance_To_Next_Scheduled_Day` — Assert.Equal() Failure: Values differ Expected: 2 Actual:   null
- `DatesErp.Tests.PlanIssueB93Tests.Scheduled_Sheet_Shows_Past_Current_And_Future_And_Issues_Past_Group_By_Its_Date` — هذه المجموعة صادرة سابقاً أو لم تعد متاحة — حدّث الشاشة.
- `DatesErp.Tests.InitOrderGuardTests.Other_InitTime_Handlers_Guard_The_Latest_Control` — Assert.Contains() Failure: Sub-string not found String:    "using System.Collections.ObjectModel;\nusi"··· Not found: "if (CalcTotalBox == null) return;"
- `DatesErp.Tests.PlanningB56Tests.Planning_Is_One_Screen_And_Plans_Search_Is_A_Popup` — Assert.Contains() Failure: Sub-string not found String:    "using System.Collections.ObjectModel;\nusi"··· Not found: "new Views.PlanSearchWindow("
- `DatesErp.Tests.GuardTests.Double_Delivery_Receipt_Is_Rejected` — Assert.True() Failure Expected: True Actual:   False
- `DatesErp.Tests.UnapproveUsesReversalEntriesTests.Unapproved_Customer_Delivery_Keeps_Ledger_And_Adds_Reversal` — DatesErp.Core.Exceptions.PermissionDeniedException : لا تملك صلاحية: Cancel على وحدة delivery
- `DatesErp.Tests.OneClickLaunchTests.OneClick_Upgrader_Copies_Nothing_And_Backs_Up_Nothing` — System.IO.FileNotFoundException : Could not find file '/home/user/OFFTT/Installer/1-حدّث_وشغل.bat'.
- `DatesErp.Tests.FullWorkflowTests.Full_Traceability_Workflow_End_To_End` — ⛔ لا يمكن تسليم 3,000.0 كجم من «خلاص ممتاز 500جم»: المقبول المعتمد لنفس الدفعة/العبوة 2,990.0 كجم. لا يُسلَّم للعميل إلا الكمية المطابقة المعتمدة لنفس هوية البند.
- `DatesErp.Tests.OrdersScreenToolbarTests.Toolbar_Communicates_Available_Actions_And_Active_Step` — Assert.Contains() Failure: Sub-string not found String:    "using System.Windows;\nusing System.Window"··· Not found: "PermissionGate.Can(Module, "Create") || P"···
- `DatesErp.Tests.LongTermPlanTests.Future_Days_Editable_With_Energy_Recheck_Executed_Locked_Billing_On_Delivered_Only` — عدد الكراتين لهذا الاستلام يجب أن يكون 400 حسب التوزيع التراكمي لبند التسليم (أو اتركه صفراً للحساب التلقائي).
- `DatesErp.Tests.ReferentialIntegrityTests.Unapprove_Without_Plan_References_Still_Works` — Assert.False() Failure Expected: False Actual:   True
- `DatesErp.Tests.B96DeliveryTests.Plan_With_Second_Pending_Order_Remains_Open_After_First_Delivery` — بنود الأمر يجب أن تطابق مجموعة الخطة المعتمدة ليوم 20/08/2026 كاملة؛ لا اختيار جزئي أو صنف إضافي أو تكرار أمر سابق.
- `DatesErp.Tests.LongTermPlanTests.Customer_Status_Independent_And_Updates_Through_Production_And_Delivery` — عدد الكراتين لهذا الاستلام يجب أن يكون 240 حسب التوزيع التراكمي لبند التسليم (أو اتركه صفراً للحساب التلقائي).
- `DatesErp.Tests.PlanIssueB93Tests.Issue_From_Plan_Can_Create_A_Future_Scheduled_Day` — Assert.Equal() Failure: Values differ Expected: 3 Actual:   null
- `DatesErp.Tests.GuardTests.Cannot_Deliver_More_Than_Customer_Balance` — Assert.Contains() Failure: Sub-string not found String:    "⛔ لا يمكن التسليم للعميل: المقبول المعتمد"··· Not found: "رصيد العميل"
- `DatesErp.Tests.InventoryLedgerAuditTests.Full_Cycle_Ledger_Audit_Raw_And_Finished_Decrease_Correctly` — عدد كراتين سند الاستلام يتجاوز المتبقي في بند أمر التسليم.
- `DatesErp.Tests.UnitRuleTests.Closing_Rejects_Cartons_Contradicting_Carton_Weight` — Assert.Throws() Failure: No exception was thrown Expected: typeof(DatesErp.Core.Exceptions.DomainException)
- `DatesErp.Tests.MultiCustomerPlanTests.One_Plan_Multiple_Customers_Rows_Keep_Full_References` — Assert.Contains() Failure: Sub-string not found String:    "⛔ لا يمكن حجز 99,999.0 كجم من الدفعة 1 في"··· Not found: "الطاقة"
