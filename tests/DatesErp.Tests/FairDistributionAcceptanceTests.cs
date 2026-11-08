using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>قبول محرك التوزيع الجديد: الطاقة من المعدل×الساعات، الخام منفصل عن التام،
/// والالتزامات السابقة لا تُكرر أو تُتجاوز.</summary>
public class FairDistributionAcceptanceTests
{
    private static string D(DateTime date) => date.ToString("dd/MM/yyyy");
    private static DateTime Workday(DateTime date)
    {
        while (date.DayOfWeek == DayOfWeek.Friday) date = date.AddDays(1);
        return date;
    }

    private static (TestHost Host, int CustomerId, int LotId) SeedLot(double kg, int rate = 10,
        int effectiveHours = 5, int staleShiftCapacity = 9999)
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var cust = new Customer { CustomerCode = "FAIR-AC", CustomerName = "عميل قبول التوزيع", IsActive = true };
        db.Customers.Add(cust);
        var shift = db.Shifts.Single(s => s.Id == 1);
        shift.EffectiveProductiveHours = effectiveHours;
        shift.TotalHours = effectiveHours;
        shift.PlannedDowntimeHours = 0;
        var cap = db.ProductShiftCapacities.First(c => c.ProductId == 3 && c.ShiftId == 1 && c.PackagingTypeId == null);
        cap.HourlyProductionRate = rate;
        cap.ShiftCapacity = staleShiftCapacity;
        db.SaveChanges();
        var lot = new Lot
        {
            LotCode = "LOT-FAIR-AC", ProductId = 1, CustomerId = cust.Id,
            InitialQtyKg = kg, InStockQtyKg = kg, Status = "Approved"
        };
        db.Lots.Add(lot);
        db.SaveChanges();
        return (host, cust.Id, lot.Id);
    }

    private static FairDistributionProposal Propose(TestHost host, DateTime day, int? excludePlanId = null)
    {
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        return svc.SuggestFairDistribution(D(day), D(day), 1, 1, targetProductId: 3,
            excludeFriday: false, useCumulative: true, capPerCustomerPerDay: false,
            useFullDay: false, excludePlanId: excludePlanId);
    }

    [Fact]
    public void Capacity_IsRateTimesEffectiveHours_NotStaleShiftCapacity()
    {
        var (host, _, _) = SeedLot(5000, rate: 10, effectiveHours: 5, staleShiftCapacity: 9999);
        using (host)
        {
            var proposal = Propose(host, Workday(DateTime.Today));
            Assert.True(proposal.Ok, proposal.Message);
            Assert.Equal(50, proposal.Rows.Sum(r => r.PlannedCartons));
            Assert.All(proposal.Rows, row =>
            {
                Assert.Equal(10, row.HourlyRateCartons);
                Assert.Equal(5, row.EffectiveShiftHours);
                Assert.Equal(5, row.PlannedHours, 3);
                Assert.Equal(50, row.ShiftCapacityCartons);
            });
        }
    }

    [Fact]
    public void ExistingPlanOccupancy_ReducesAvailableShiftHours()
    {
        var (host, customerId, _) = SeedLot(5000, rate: 10, effectiveHours: 5, staleShiftCapacity: 9999);
        using (host)
        {
            var day = Workday(DateTime.Today);
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var lot = new Lot { LotCode = "LOT-FAIR-OTHER", ProductId = 1, CustomerId = customerId,
                    InitialQtyKg = 5000, InStockQtyKg = 5000, Status = "Approved" };
                db.Lots.Add(lot);
                db.SaveChanges();
                var plan = new ProductionPlan
                {
                    DocumentNumber = "PLAN-FAIR-OTHER", PlanTitle = "حجز طاقة سابق", PlanType = "Period",
                    StartDate = day, EndDate = day, ShiftId = 1, LineId = 1, IsApproved = true,
                    Status = "Approved", IsClosed = false
                };
                plan.Items.Add(new ProductionPlanItem
                {
                    ProductId = 3, PackagingTypeId = 1, CustomerId = customerId, LotId = lot.Id,
                    PlannedCartons = 20, PlannedQtyKg = 100, ScheduledDate = day,
                    SuggestedShiftId = 1, SuggestedLineId = 1, Status = "Approved"
                });
                db.ProductionPlans.Add(plan);
                db.SaveChanges();
            }
            var proposal = Propose(host, day);
            Assert.True(proposal.Ok, proposal.Message);
            Assert.Equal(30, proposal.Rows.Sum(r => r.PlannedCartons));
            Assert.True(proposal.Rows.Sum(r => r.PlannedHours) <= 3.0001);
        }
    }

    [Fact]
    public void SuggestedRow_SeparatesFinishedOutput_FromRawRequirementAndSource()
    {
        var (host, _, _) = SeedLot(5000, rate: 10, effectiveHours: 5);
        using (host)
        {
            var proposal = Propose(host, Workday(DateTime.Today));
            Assert.True(proposal.Ok, proposal.Message);
            Assert.All(proposal.Rows, row =>
            {
                Assert.True(row.PlannedQtyKg > 0);
                Assert.True(row.RawRequiredQtyKg > 0);
                Assert.NotEqual(0, row.LotId);
                Assert.Contains("خام/كرتون", row.RawSource);
            });
        }
    }

    [Fact]
    public void PreviousFairness_UsesActualProducedQuantity_NotPlannedQuantity()
    {
        var (host, customerId, _) = SeedLot(5000, rate: 10, effectiveHours: 5);
        using (host)
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var plan = new ProductionPlan
                {
                    DocumentNumber = "PLAN-FAIR-HISTORY", PlanTitle = "سجل سابق", PlanType = "Period",
                    StartDate = DateTime.Today, EndDate = DateTime.Today, ShiftId = 1, LineId = 1,
                    IsApproved = true, Status = "Approved"
                };
                plan.Items.Add(new ProductionPlanItem
                {
                    ProductId = 3, PackagingTypeId = 1, CustomerId = customerId,
                    PlannedCartons = 200, PlannedQtyKg = 1000, ProducedQtyKg = 125,
                    ScheduledDate = DateTime.Today.AddDays(-2), SuggestedShiftId = 1, SuggestedLineId = 1,
                    IsClosed = false, Status = "Approved"
                });
                db.ProductionPlans.Add(plan);
                db.SaveChanges();
            }
            var proposal = Propose(host, Workday(DateTime.Today));
            Assert.True(proposal.Ok, proposal.Message);
            Assert.Equal(125, Assert.Single(proposal.Customers).PreviouslyProducedKg);
            Assert.Equal(125, Assert.Single(proposal.Customers).HistoricalKg);
        }
    }

    [Fact]
    public void ExcludingCurrentPlan_DoesNotDoubleCountItsRawReservationOrCapacity()
    {
        var (host, customerId, lotId) = SeedLot(5000, rate: 10, effectiveHours: 5);
        using (host)
        {
            var day = Workday(DateTime.Today);
            int planId;
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var plan = new ProductionPlan
                {
                    DocumentNumber = "PLAN-FAIR-SELF", PlanTitle = "الخطة الحالية", PlanType = "Period",
                    StartDate = day, EndDate = day, ShiftId = 1, LineId = 1, IsApproved = true,
                    Status = "Approved", IsClosed = false
                };
                plan.Items.Add(new ProductionPlanItem
                {
                    ProductId = 3, PackagingTypeId = 1, CustomerId = customerId, LotId = lotId,
                    PlannedCartons = 20, PlannedQtyKg = 100, RawRequiredQtyKg = 100,
                    ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1, IsClosed = false,
                    Status = "Approved"
                });
                db.ProductionPlans.Add(plan);
                db.SaveChanges();
                planId = plan.Id;
            }
            var proposal = Propose(host, day, planId);
            Assert.True(proposal.Ok, proposal.Message);
            Assert.Equal(50, proposal.Rows.Sum(r => r.PlannedCartons));
        }
    }

    [Fact]
    public void CapacityAndStockAreNeverExceeded_WhenRawIsTheTighterConstraint()
    {
        var (host, _, _) = SeedLot(77, rate: 100, effectiveHours: 8);
        using (host)
        {
            var proposal = Propose(host, Workday(DateTime.Today));
            Assert.True(proposal.Ok, proposal.Message);
            Assert.Equal(15, proposal.Rows.Sum(r => r.PlannedCartons)); // carton weight is 5 kg
            Assert.True(proposal.Rows.Sum(r => r.RawRequiredQtyKg) <= 77.001);
            Assert.True(proposal.Rows.Sum(r => r.PlannedHours) <= 8.0001);
        }
    }

    [Fact]
    public void SameLotIsNotAllocatedTwiceAcrossRows()
    {
        var (host, _, lotId) = SeedLot(5000, rate: 10, effectiveHours: 5);
        using (host)
        {
            var proposal = Propose(host, Workday(DateTime.Today));
            Assert.True(proposal.Ok, proposal.Message);
            Assert.Equal(proposal.Rows.Count, proposal.Rows.Select(r => (r.LotId, r.Date, r.ShiftId, r.ProductId, r.PackagingTypeId)).Distinct().Count());
            Assert.All(proposal.Rows, r => Assert.Equal(lotId, r.LotId));
            Assert.True(proposal.Rows.Sum(r => r.RawRequiredQtyKg) <= 5000.001);
        }
    }

    [Fact]
    public void ZeroEffectiveShiftHours_ProducesNoProposalInsteadOfAssumingEightHours()
    {
        var (host, _, _) = SeedLot(5000, rate: 10, effectiveHours: 0, staleShiftCapacity: 5000);
        using (host)
        {
            var proposal = Propose(host, Workday(DateTime.Today));
            Assert.False(proposal.Ok);
            Assert.Empty(proposal.Rows);
        }
    }

    [Fact]
    public void RawBatchBecomingReadyDuringPeriod_IsNotSuggestedBeforeItsReadyDate()
    {
        var (host, customerId, lotId) = SeedLot(5000, rate: 10, effectiveHours: 5);
        using (host)
        {
            var start = Workday(DateTime.Today);
            var ready = start.AddDays(1);
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var shipment = new Shipment
                {
                    DocumentNumber = "SHP-FAIR-READY", CustomerId = customerId,
                    ContainerNumber = "CONT-FAIR-READY", ArrivalDate = DateTime.Today.AddDays(-1),
                    Status = "Approved", IsApproved = true,
                    ReceivingWarehouseId = db.Warehouses.Where(w => w.WarehouseCode == "WRM").Select(w => w.Id).First()
                };
                db.Shipments.Add(shipment);
                db.SaveChanges();
                var receipt = new ShipmentItem
                {
                    ShipmentId = shipment.Id, ProductId = 1, TreatmentRequired = true,
                    TreatmentUntilDate = ready, TotalWeightKg = 5000, UnitWeightKg = 5000,
                    PackageCount = 1, Status = "Approved"
                };
                db.ShipmentItems.Add(receipt);
                db.SaveChanges();
                var lot = db.Lots.Single(l => l.Id == lotId);
                lot.ShipmentId = shipment.Id;
                lot.ShipmentItemId = receipt.Id;
                lot.UnderTreatmentQtyKg = 5000;
                db.SaveChanges();
            }
            using var scope2 = host.Services.CreateScope();
            var rawEngine = new DatesErp.Application.Services.RawPlanningAvailabilityEngine(scope2.ServiceProvider.GetRequiredService<DatesErpDbContext>());
            var readySnapshots = rawEngine.ForLotAllWarehouses(lotId, 0, null, null, ready).Sum(x => x.AvailableForPlanningKg);
            Assert.True(readySnapshots > 0, $"Expected ready raw at {ready:dd/MM/yyyy}, actual {readySnapshots}");
            var svc = scope2.ServiceProvider.GetRequiredService<IPlanningService>();
            var proposal = svc.SuggestFairDistribution(D(start), D(ready), 1, 1, targetProductId: 3,
                excludeFriday: false, capPerCustomerPerDay: false, useFullDay: false);
            Assert.True(proposal.Ok, proposal.Message);
            Assert.All(proposal.Rows, row => Assert.Equal(D(ready), row.Date));
        }
    }

    [Fact]
    public void ApprovedPlanAmendment_RejectsStartedSession_AndLeavesPlanUntouched()
    {
        var (host, customerId, lotId) = SeedLot(5000, rate: 10, effectiveHours: 5);
        using (host)
        {
            int planId;
            int startedSessionId;
            int untouchedItemId;
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var plan = new ProductionPlan
                {
                    DocumentNumber = "PLAN-FAIR-REV", PlanTitle = "خطة معتمدة", PlanType = "Period",
                    StartDate = DateTime.Today, EndDate = DateTime.Today.AddDays(2), ShiftId = 1,
                    LineId = 1, IsApproved = true, Status = "Approved", IsClosed = false
                };
                plan.CustomerScopes.Add(new ProductionPlanCustomerScope { CustomerId = customerId, IsEnabled = true });
                db.ProductionPlans.Add(plan);
                db.SaveChanges();
                planId = plan.Id;
                var started = new ProductionPlanSession
                {
                    PlanId = planId, SequenceNo = 1, SessionDate = DateTime.Today, ShiftId = 1,
                    LineId = 1, Status = "InProgress", CapacityHours = 5
                };
                var pending = new ProductionPlanSession
                {
                    PlanId = planId, SequenceNo = 2, SessionDate = DateTime.Today.AddDays(1), ShiftId = 1,
                    LineId = 1, Status = "Pending", CapacityHours = 5
                };
                db.ProductionPlanSessions.AddRange(started, pending);
                db.SaveChanges();
                startedSessionId = started.Id;
                var startedItem = new ProductionPlanItem
                {
                    PlanId = planId, SessionId = started.Id, CustomerId = customerId, ProductId = 3,
                    PackagingTypeId = 1, PlannedCartons = 20, PlannedQtyKg = 100,
                    ProducedQtyKg = 25, RawRequiredQtyKg = 100, LotId = lotId,
                    ScheduledDate = DateTime.Today, SuggestedShiftId = 1, SuggestedLineId = 1,
                    Status = "Approved"
                };
                var pendingItem = new ProductionPlanItem
                {
                    PlanId = planId, SessionId = pending.Id, CustomerId = customerId, ProductId = 3,
                    PackagingTypeId = 1, PlannedCartons = 20, PlannedQtyKg = 100,
                    RawRequiredQtyKg = 100, LotId = lotId,
                    ScheduledDate = pending.SessionDate, SuggestedShiftId = 1, SuggestedLineId = 1,
                    Status = "Approved"
                };
                db.ProductionPlanItems.AddRange(startedItem, pendingItem);
                db.SaveChanges();
                untouchedItemId = pendingItem.Id;
            }

            using var scope2 = host.Services.CreateScope();
            var svc = scope2.ServiceProvider.GetRequiredService<IPlanningService>();
            var proposal = new PlanItemDto
            {
                SessionId = startedSessionId,
                SourceType = "FromReceiving", LotId = lotId, SelectedRawProductId = 1,
                CustomerId = customerId, ProductId = 3, PackagingTypeId = 1,
                PlannedCartons = 10, PlannedQtyKg = 50, RawRequiredQtyKg = 50,
                ScheduledDate = D(DateTime.Today), SuggestedShiftId = 1, SuggestedLineId = 1,
                Allocations = new List<PlanItemAllocationDto> { new() { WarehouseId = 1, LotId = lotId, CustomerId = customerId, AllocatedQtyKg = 50 } }
            };
            var result = svc.AmendFuturePlan(planId, "إعادة جدولة بسبب تغيير الطلب", new List<PlanItemDto> { proposal });
            Assert.False(result.Ok);
            Assert.Contains("بدأت أو انتهت", result.Message);
            using var scope3 = host.Services.CreateScope();
            var dbAfter = scope3.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            Assert.True(dbAfter.ProductionPlanItems.Any(i => i.Id == untouchedItemId));
            Assert.Equal(25, dbAfter.ProductionPlanItems.Single(i => i.SessionId == startedSessionId).ProducedQtyKg);
            Assert.Empty(dbAfter.ProductionPlanRevisions.Where(r => r.PlanId == planId));
        }
    }


    [Fact]
    public void FutureChangeRequest_CancellationIsAppliedOnlyAfterApproval()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int planId; int sessionId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var plan = new ProductionPlan { DocumentNumber = "PLAN-REQ-ATOMIC", PlanTitle = "طلب تعديل", PlanType = "Period",
                StartDate = DateTime.Today.AddDays(1), EndDate = DateTime.Today.AddDays(1), ShiftId = 1, LineId = 1,
                IsApproved = true, Status = "Approved" };
            db.ProductionPlans.Add(plan); db.SaveChanges(); planId = plan.Id;
            var session = new ProductionPlanSession { PlanId = planId, SequenceNo = 1, SessionDate = DateTime.Today.AddDays(1),
                ShiftId = 1, LineId = 1, Status = "Pending", CapacityHours = 5 };
            db.ProductionPlanSessions.Add(session); db.SaveChanges(); sessionId = session.Id;
        }
        int requestId;
        using (var scope = host.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var submitted = svc.SubmitFuturePlanChangeRequest(planId, "إلغاء جلسة لم تعد مطلوبة", new List<PlanItemDto>(), new List<int> { sessionId });
            Assert.True(submitted.Ok, submitted.Message); requestId = submitted.Id;
            Assert.Contains(svc.GetPlanChangeRequests(planId), r => r.Id == requestId && r.ProposedItemsCount == 0);
            Assert.True(svc.ApprovePlanChangeRequest(requestId).Ok);
            Assert.False(svc.ApprovePlanChangeRequest(requestId).Ok);
        }
        using var verifyScope = host.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var request = verify.ProductionPlanChangeRequests.Single(r => r.Id == requestId);
        Assert.True(request.IsApproved); Assert.Equal("Approved", request.Status);
        Assert.Single(verify.ProductionPlanRevisions.Where(r => r.PlanId == planId));
        Assert.Equal("Pending", verify.ProductionPlanSessions.Single(s => s.Id == sessionId).Status);
        Assert.Empty(verify.ProductionPlanItems.Where(i => i.PlanId == planId));
    }

    [Fact]
    public void FutureAmendment_CreatesImmutableRevision_AndCanClearUnneededPendingSession()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using (host)
        {
            int planId;
            int sessionId;
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var plan = new ProductionPlan
                {
                    DocumentNumber = "PLAN-FAIR-REV2", PlanTitle = "خطة إصدار", PlanType = "Period",
                    StartDate = DateTime.Today.AddDays(1), EndDate = DateTime.Today.AddDays(1),
                    ShiftId = 1, LineId = 1, IsApproved = true, Status = "Approved"
                };
                db.ProductionPlans.Add(plan);
                db.SaveChanges();
                planId = plan.Id;
                var session = new ProductionPlanSession
                {
                    PlanId = planId, SequenceNo = 1, SessionDate = DateTime.Today.AddDays(1),
                    ShiftId = 1, LineId = 1, Status = "Pending", CapacityHours = 5
                };
                db.ProductionPlanSessions.Add(session);
                db.SaveChanges();
                sessionId = session.Id;
            }
            using (var scope = host.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
                var result = svc.AmendFuturePlan(planId, "إلغاء إنتاج غير مطلوب", new List<PlanItemDto>(), new List<int> { sessionId });
                Assert.True(result.Ok, result.Message);
            }
            using var verifyScope = host.Services.CreateScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var revision = Assert.Single(verify.ProductionPlanRevisions.Where(r => r.PlanId == planId));
            Assert.Equal("FutureAmendment", revision.RevisionType);
            Assert.Contains("Items", revision.SnapshotJson);
            Assert.Null(revision.PreviousSnapshotJson);
            Assert.Equal("Pending", verify.ProductionPlanSessions.Single(s => s.Id == sessionId).Status);
        }
    }


    [Fact]
    public void AmendmentProposal_IsCappedAtCustomerProductUnproducedNeed()
    {
        var (host, customerId, _) = SeedLot(5000, rate: 100, effectiveHours: 8);
        using (host)
        {
            var day = Workday(DateTime.Today);
            int planId;
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var plan = new ProductionPlan
                {
                    DocumentNumber = "PLAN-FAIR-NEED", PlanTitle = "طلب إنتاج قائم", PlanType = "Period",
                    StartDate = day, EndDate = day, ShiftId = 1, LineId = 1, IsApproved = true, Status = "Approved"
                };
                db.ProductionPlans.Add(plan);
                db.SaveChanges();
                planId = plan.Id;
                var session = new ProductionPlanSession
                {
                    PlanId = planId, SequenceNo = 1, SessionDate = day, ShiftId = 1,
                    LineId = 1, Status = "Pending", CapacityHours = 8
                };
                db.ProductionPlanSessions.Add(session);
                db.SaveChanges();
                db.ProductionPlanItems.Add(new ProductionPlanItem
                {
                    PlanId = planId, SessionId = session.Id, CustomerId = customerId, ProductId = 3,
                    PackagingTypeId = 1, PlannedCartons = 20, PlannedQtyKg = 100,
                    ProducedQtyKg = 0, ScheduledDate = day, SuggestedShiftId = 1,
                    SuggestedLineId = 1, IsClosed = false, Status = "Approved"
                });
                db.SaveChanges();
            }
            using var scope2 = host.Services.CreateScope();
            var svc = scope2.ServiceProvider.GetRequiredService<IPlanningService>();
            var proposal = svc.SuggestFairDistribution(D(day), D(day), 1, 1, targetProductId: 3,
                excludeFriday: false, capPerCustomerPerDay: false, useFullDay: false, excludePlanId: planId);
            Assert.True(proposal.Ok, proposal.Message);
            Assert.Equal(20, proposal.Rows.Sum(r => r.PlannedCartons));
            Assert.Equal(100, proposal.Rows.Sum(r => r.PlannedQtyKg));
            Assert.All(proposal.Rows, r => Assert.Equal(0, r.RemainingNeedKg));
            Assert.Equal(0, Assert.Single(proposal.Customers).RemainingNeedKg);
        }
    }

}
