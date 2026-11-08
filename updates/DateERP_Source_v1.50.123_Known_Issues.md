# المشكلات المعروفة — DateERP 1.50.123

نتيجة الاختبار الكامل: **99 اختباراً فاشلاً**؛ التفاصيل/رسائل الاستثناء كاملة في `source-audit_v1.50.123/TestResults/DateERP_v1.50.123_tests.trx`. لم تُحذف الاختبارات ولم تُخفَ.

المجموعات أدناه هي أسماء حالات الاختبار الفاشلة المسجلة فعلياً؛ وجود فشل لا يثبت وحده أن السبب في كود التحديث، لكنه يمنع وصف الإصدار بأنه مجتاز للتحقق.

## ActualDeliveryFixesTests (2)
- `ActualDeliveryFixesTests.Production_Delivery_Screen_Uses_The_Compiled_Order_API`
- `ActualDeliveryFixesTests.Quality_Lists_Produced_First_And_Uses_The_Adopted_Design`

## AuditAndRbacTests (1)
- `AuditAndRbacTests.Failed_Logins_Lock_Account_After_5_Attempts`

## AuditCredentialRedactionTests (1)
- `AuditCredentialRedactionTests.LegacyAuditRows_WithEmbeddedHashes_AreScrubbedByReferenceUpgrade`

## AuxFlexTests (1)
- `AuxFlexTests.Closing_Returns_OverIssued_Aux_Automatically`

## B96DeliveryTests (1)
- `B96DeliveryTests.Plan_With_Second_Pending_Order_Remains_Open_After_First_Delivery`

## ClosingPreservesPlanBaselineTests (1)
- `ClosingPreservesPlanBaselineTests.Partial_Closing_Still_Releases_The_Reservation`

## ConversionAutoLinkTests (1)
- `ConversionAutoLinkTests.Migrate_AddsSourceColumn_And_AutoLinks_Conversions_FromNames`

## DeliveryAuditProbeTests (3)
- `DeliveryAuditProbeTests.S10_MultiLot_Without_Lot_Rejected_Explicit_Lot_Works`
- `DeliveryAuditProbeTests.S2_MultiItems_OneDoc_BothDeducted`
- `DeliveryAuditProbeTests.S7_Update_Same_Doc_And_DeleteDraft`

## DeliveryLineMatchingTests (2)
- `DeliveryLineMatchingTests.Ambiguous_Customer_Is_Rejected_Not_Silently_Guessed`
- `DeliveryLineMatchingTests.Explicit_Customer_Is_Honoured_On_Shared_Lot`

## DeliveryPickingDirectiveTests (3)
- `DeliveryPickingDirectiveTests.Pick_05_CustomerIsolation_ByProductsExcluded`
- `DeliveryPickingDirectiveTests.Pick_06_Approve_RechecksBalance_Concurrency`
- `DeliveryPickingDirectiveTests.Pick_07_SkippedItems_NeverEnterDocument`

## DiagnosticIntegrityTests (1)
- `DiagnosticIntegrityTests.Deep_Detects_Missing_Permission_Matrix_Row`

## FairDistributionAcceptanceTests (7)
- `FairDistributionAcceptanceTests.AmendmentProposal_IsCappedAtCustomerProductUnproducedNeed`
- `FairDistributionAcceptanceTests.Capacity_IsRateTimesEffectiveHours_NotStaleShiftCapacity`
- `FairDistributionAcceptanceTests.ExistingPlanOccupancy_ReducesAvailableShiftHours`
- `FairDistributionAcceptanceTests.PreviousFairness_UsesActualProducedQuantity_NotPlannedQuantity`
- `FairDistributionAcceptanceTests.RawBatchBecomingReadyDuringPeriod_IsNotSuggestedBeforeItsReadyDate`
- `FairDistributionAcceptanceTests.SuggestedRow_SeparatesFinishedOutput_FromRawRequirementAndSource`
- `FairDistributionAcceptanceTests.ZeroEffectiveShiftHours_ProducesNoProposalInsteadOfAssumingEightHours`

## FullCycleFourCustomersAcceptanceTests (5)
- `FullCycleFourCustomersAcceptanceTests.ACC_01_Foundation_DraftLifecycle_And_InventoryParity`
- `FullCycleFourCustomersAcceptanceTests.ACC_02_Plans_Availability_MultiCustomerShift_And_EditGuards`
- `FullCycleFourCustomersAcceptanceTests.ACC_03_Orders_Parity_PartialExecution_Completion_NoDouble`
- `FullCycleFourCustomersAcceptanceTests.ACC_04_Quality_FgReceipt_PartialDelivery_And_OwnershipGuards`
- `FullCycleFourCustomersAcceptanceTests.ACC_06_FinalReconciliation_PerCustomer_ChainIdentity`

## FullCycleMultiCustomerTests (1)
- `FullCycleMultiCustomerTests.Multi_Customer_Full_Cycle_With_Inventory_Impacts`

## FullWorkflowTests (1)
- `FullWorkflowTests.Full_Traceability_Workflow_End_To_End`

## GuardTests (2)
- `GuardTests.Cannot_Deliver_More_Than_Customer_Balance`
- `GuardTests.Double_Delivery_Receipt_Is_Rejected`

## InitOrderGuardTests (2)
- `InitOrderGuardTests.Other_InitTime_Handlers_Guard_The_Latest_Control`
- `InitOrderGuardTests.ServerSide_Queries_Never_Use_Math_Max_Min`

## InspectionScreenTests (1)
- `InspectionScreenTests.Screen_Code_Contains_No_Hardcoded_ResultType_Names_Or_Units`

## InventoryLedgerAuditTests (2)
- `InventoryLedgerAuditTests.Full_Cycle_Ledger_Audit_Raw_And_Finished_Decrease_Correctly`
- `InventoryLedgerAuditTests.Negative_Raw_Balance_Is_Impossible`

## ItemTraceabilityTests (3)
- `ItemTraceabilityTests.Prevent_Producing_Khalas_From_Sukkari_Without_OfficialDefinition`
- `ItemTraceabilityTests.Quality_Rejects_Product_Not_In_Order`
- `ItemTraceabilityTests.Sukkari_And_Khalas_FullCycle_SingleCustomer_IdentityPreserved`

## LongTermPlanTests (2)
- `LongTermPlanTests.Customer_Status_Independent_And_Updates_Through_Production_And_Delivery`
- `LongTermPlanTests.Future_Days_Editable_With_Energy_Recheck_Executed_Locked_Billing_On_Delivered_Only`

## MultiCustomerPlanTests (1)
- `MultiCustomerPlanTests.One_Plan_Multiple_Customers_Rows_Keep_Full_References`

## NumberingTests (1)
- `NumberingTests.TreatmentNumbering_StaysUniqueAcrossConcurrentSqliteContexts`

## OneClickLaunchTests (3)
- `OneClickLaunchTests.First_Launch_Creates_Database_And_Tables_Automatically`
- `OneClickLaunchTests.Installer_Seeds_The_Separate_Identity_With_Valid_Json`
- `OneClickLaunchTests.OneClick_Upgrader_Copies_Nothing_And_Backs_Up_Nothing`

## OrdersScreenToolbarTests (1)
- `OrdersScreenToolbarTests.Toolbar_Communicates_Available_Actions_And_Active_Step`

## PermissionGatingTests (1)
- `PermissionGatingTests.Existing_Matrix_Is_Upgraded_For_Designated_Planning_Edit_Roles`

## PermissionsSystemTests (1)
- `PermissionsSystemTests.Viewer_Can_View_Only_And_Delete_Is_Denied`

## PlanCheckB91Tests (2)
- `PlanCheckB91Tests.LotShortage_Warns_And_Caps_Coverage`
- `PlanCheckB91Tests.Overload_Shortage_Detected_With_Short_Days`

## PlanClosingTests (3)
- `PlanClosingTests.AutoClose_PlanClosedWhenAllProduced_ReservationsReleased`
- `PlanClosingTests.CloseDay_FullScenario_Outputs_Downtimes_Quality`
- `PlanClosingTests.QualityTwoDays_DeliveryToFinishedAllowed_CustomerDeliveryWaitsApproval`

## PlanIssueB93Tests (4)
- `PlanIssueB93Tests.All_Customers_Are_Issued_With_Their_Own_Identity`
- `PlanIssueB93Tests.Issue_From_Plan_Can_Create_A_Future_Scheduled_Day`
- `PlanIssueB93Tests.Issue_Only_Today_Then_Advance_To_Next_Scheduled_Day`
- `PlanIssueB93Tests.Scheduled_Sheet_Shows_Past_Current_And_Future_And_Issues_Past_Group_By_Its_Date`

## PlanLevelClosingTests (2)
- `PlanLevelClosingTests.CloseDay_Guards_OverProduction_Negative_And_Empty`
- `PlanLevelClosingTests.CloseWholePlan_ReturnsRemaindersToRaw_SendsQuality_AndAutoCloses`

## PlanningB56Tests (1)
- `PlanningB56Tests.Planning_Is_One_Screen_And_Plans_Search_Is_A_Popup`

## PlanningFifoTests (1)
- `PlanningFifoTests.Fifo_Warns_When_Planning_Later_While_Earlier_Exists`

## PlanningLinkCapacityTests (1)
- `PlanningLinkCapacityTests.Full_Master_Raw_Selector_Save_Backend_And_Capacity_Scenario`

## PlanningPackSpecsAndLockTests (2)
- `PlanningPackSpecsAndLockTests.Approved_Plan_Locks_All_Edit_Buttons_Until_Unapprove`
- `PlanningPackSpecsAndLockTests.Capacity_Bar_Is_Compact_For_Weekly_Periods`

## ProductLotAvailabilityTests (3)
- `ProductLotAvailabilityTests.PerProductAvailability_DeductsOnlySameProductReservations`
- `ProductLotAvailabilityTests.SavePlan_AllowsQuantityExceedingRawStock_Because_Water_Gains_Weight`
- `ProductLotAvailabilityTests.SavePlan_MultipleProductsFromSameLot_Share_The_Lot_Without_Fixed_Equation`

## ProductionActualCloseConsolidationTests (1)
- `ProductionActualCloseConsolidationTests.Actual_Close_Provides_Explicit_Search_Save_Print_And_Byproduct_Source_Entry`

## ProductionOrderScreenTests (2)
- `ProductionOrderScreenTests.Multi_Customer_Orders_Keep_Customer_And_Product_Identity`
- `ProductionOrderScreenTests.Order_Cannot_Exceed_Plan_Remaining_And_Capacity_Is_Checked`

## ProductionScreensDesignTests (3)
- `ProductionScreensDesignTests.Every_Window_Class_Is_Reachable_From_Another_File`
- `ProductionScreensDesignTests.Orders_Screen_Uses_The_Adopted_Design`
- `ProductionScreensDesignTests.ProductionDelivery_Screen_Uses_The_Adopted_Design`

## QualityGateTests (1)
- `QualityGateTests.Passed_Zero_Accepted_Is_Rejected_Before_Approval_And_Delivery`

## QualityToolbarPermissionTests (1)
- `QualityToolbarPermissionTests.Quality_Search_Opens_An_Unambiguous_Match_And_Empty_Search_Gives_Feedback`

## RawTreatmentScenarioTests (1)
- `RawTreatmentScenarioTests.P7_Full_Traceability_Chain_Is_Intact`

## ReceivingScreenRepairTests (1)
- `ReceivingScreenRepairTests.Grid_Contains_Inline_Choice_And_Conditional_DatePicker_Not_A_Master_Product_Flag`

## ReferentialIntegrityTests (1)
- `ReferentialIntegrityTests.Unapprove_Without_Plan_References_Still_Works`

## ReportPreviewNavigationTests (1)
- `ReportPreviewNavigationTests.Operational_Preview_Selects_Modern_Template_And_Renders_Immediately`

## StockBalancePackagingTests (3)
- `StockBalancePackagingTests.Customer_A_And_B_Should_Have_Separate_Balances`
- `StockBalancePackagingTests.Dispensing_From_8kg_Should_Not_Affect_4kg`
- `StockBalancePackagingTests.SameProduct_Warehouse_Customer_Lot_With_Different_Packaging_Should_Be_Separate_Balances`

## TodayOrdersTests (1)
- `TodayOrdersTests.Approved_Today_Only_Immutable_Order_And_Execution_End_To_End`

## UiWiringTests (3)
- `UiWiringTests.Every_Xaml_Click_Handler_Exists_And_Is_Not_Empty`
- `UiWiringTests.Main_Grids_Fill_The_Window_Instead_Of_Fixed_Height`
- `UiWiringTests.Planning_Save_Uses_Edit_Gate_And_Save_Click_For_F10`

## UnapproveUsesReversalEntriesTests (1)
- `UnapproveUsesReversalEntriesTests.Unapproved_Customer_Delivery_Keeps_Ledger_And_Adds_Reversal`

## UnitRuleTests (1)
- `UnitRuleTests.Closing_Rejects_Cartons_Contradicting_Carton_Weight`

## UnitsGoldenRuleTests (1)
- `UnitsGoldenRuleTests.Units_02_FinishedChain_CartonOfficial_KgSupplementary`

## UsersSecurityDelegationAuditTests (4)
- `UsersSecurityDelegationAuditTests.Sec_03_UserExceptions_Override_Roles_With_Audit`
- `UsersSecurityDelegationAuditTests.Sec_07_Password_Lifecycle_Reset_Change_Reuse_Lockout`
- `UsersSecurityDelegationAuditTests.Sec_09_ForgottenPassword_ResetGenerated_SelfChange_History`
- `UsersSecurityDelegationAuditTests.Sec_12_EmergencyFlag_Required_And_Login_Messages_Unified`

## V15026UpdateAssuranceTests (3)
- `V15026UpdateAssuranceTests.Delivery_Screen_Is_One_Fixed_Page_Without_Vertical_Scroll`
- `V15026UpdateAssuranceTests.Single_Click_Begins_Editing_On_Both_Grids`
- `V15026UpdateAssuranceTests.Update_Bat_Installs_Itself_From_A_Nested_Update_Folder`

## ملاحظات بيئة/مصدر أخرى
- لا تتوفر حالة Git قابلة للتحقق: أمر `git status`/`rev-parse` يفشل؛ مؤشرات `.git/HEAD` و`packed-refs` تذكر فرعاً/commit لكن لا تثبت نظافة الشجرة أو تطابقها.
- توجد ثلاثة مشاريع مصدرية خارج `DateERP.sln`: `OpenProjectAAA`, `SourceDrawProof`, `UpdateBotAAA`؛ أُدرجت وبُنيت منفردة.
- لا توجد قاعدة بيانات إنتاج مرفقة؛ ملف `mfgsystem_local.db` الفارغ وملفات قواعد البيانات الثنائية مستبعدة.
