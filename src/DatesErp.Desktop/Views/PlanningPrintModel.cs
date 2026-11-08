using Microsoft.EntityFrameworkCore;
using DatesErp.Core.Common;
using DatesErp.Infrastructure.Persistence;
namespace DatesErp.Desktop.Views;

public class PlanningPrintModel
{
    public string CompanyNameAr { get; set; } = "شركة التمور";
    public string CompanyNameEn { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public byte[] LogoBytes { get; set; }

    public string PlanNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string PlanTypeAr { get; set; } = "";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsApproved { get; set; }
    public string StatusAr { get; set; }
    public string ShiftName { get; set; } = "-";
    public string LineName { get; set; } = "-";
    public string Notes { get; set; } = "";
    public string CreatedByName { get; set; } = "-";

    public class ItemRow
    {
        public int RowNo { get; set; }
        public int? CustomerId { get; set; }
        public string CustomerName { get; set; } = "-";
        public string ShipmentNo { get; set; } = "-";
        public string LotCode { get; set; } = "-";
        public string RawName { get; set; } = "-";
        public string ProductName { get; set; } = "-";
        public string PackName { get; set; } = "-";
        public int Cartons { get; set; }
        public double QtyKg { get; set; }
        public string Date { get; set; } = "-";
        public string LineName { get; set; } = "-";
        public string ShiftName { get; set; } = "-";
        public string WarehouseName { get; set; } = "-";
        public string SourceDrawText { get; set; } = "-";
    }

    public List<ItemRow> Items { get; set; } = new();

    public static PlanningPrintModel Load(DatesErpDbContext db, int planId)
    {
        using var tx = db.Database.CurrentTransaction == null ? db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable) : null;
        var plan = db.ProductionPlans.AsNoTracking()
            .Include(p => p.Items).ThenInclude(i => i.Allocations)
            .FirstOrDefault(p => p.Id == planId);
        if (plan == null) return null;
        var co = db.CompanyInfos.AsNoTracking().OrderBy(x => x.Id).FirstOrDefault();

        var shiftId = plan.ShiftId;
        var lineId = plan.LineId;

        var m = new PlanningPrintModel
        {
            PlanNumber = plan.DocumentNumber,
            Title = plan.PlanTitle ?? "",
            StartDate = plan.StartDate,
            EndDate = plan.EndDate,
            IsApproved = plan.IsApproved,
            StatusAr = Printing.PrintStatus.Of(plan),
            CreatedByName = db.Users.Where(u => u.Id == plan.CreatedBy).Select(u => u.FullName).FirstOrDefault() ?? "—",
            ShiftName = db.Shifts.Where(s => s.Id == (shiftId ?? 0)).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "-",
            LineName = db.ProductionLines.Where(l => l.Id == (lineId ?? 0)).Select(l => l.LineNameAr).FirstOrDefault() ?? "-",
            Notes = plan.Notes ?? "",
            // §إصلاح: كانت "Monthly" تسقط في _ فتُطبع الخطة الشهرية بعنوان «فترة محددة».
            // المطابقة الآن مع PlanClosureService.cs:41 — مصطلح واحد في كل المستندات.
            PlanTypeAr = plan.PlanType switch
            {
                "Daily" => "يومية", "Weekly" => "أسبوعية", "Monthly" => "شهرية", _ => "فترية"
            }
        };
        if (co != null)
        {
            m.CompanyNameAr = co.CompanyNameAr ?? m.CompanyNameAr;
            m.CompanyNameEn = co.CompanyNameEn ?? "";
            m.Address = co.Address ?? "";
            m.Phone = co.Phone ?? "";
            m.LogoBytes = co.LogoBytes;
        }

        var whNames = db.Warehouses.AsNoTracking().ToDictionary(w => w.Id, w => w.WarehouseNameAr);
        int? defaultRawWh = db.Warehouses.AsNoTracking().Where(w => w.WarehouseCode == "WRM").Select(w => (int?)w.Id).FirstOrDefault();
        int n = 1;
        foreach (var it in plan.Items.OrderBy(i => i.ScheduledDate ?? DateTime.MinValue).ThenBy(i => i.PriorityNo))
        {
            string Cust(int? cid) => db.Customers.Where(c => c.Id == (cid ?? 0)).Select(c => c.CustomerName).FirstOrDefault();
            string Prod(int pid) => db.Products.Where(p => p.Id == pid).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
            string LotCode(int? lid) => db.Lots.Where(l => l.Id == (lid ?? 0)).Select(l => l.LotCode).FirstOrDefault() ?? "-";

            var allocLotIds = it.Allocations?.Select(a => a.LotId).Distinct().ToList() ?? new List<int>();
            string lotDisplay = it.LotId != null
                ? LotCode(it.LotId)
                : allocLotIds.Count > 0
                    ? string.Join(" + ", allocLotIds.Select(lid => LotCode(lid)))
                    : "-";

            int? primaryLotId = it.LotId ?? (allocLotIds.Count > 0 ? allocLotIds[0] : null);
            int? lotShip = it.ShipmentId ?? db.Lots.Where(l => l.Id == (primaryLotId ?? 0)).Select(l => l.ShipmentId).FirstOrDefault();
            string shipDisplay = db.Shipments.Where(s => s.Id == (lotShip ?? 0)).Select(s => s.DocumentNumber).FirstOrDefault() ?? "-";

            int? rawProdId = it.SelectedRawProductId
                ?? (primaryLotId != null ? db.Lots.Where(l => l.Id == primaryLotId.Value).Select(l => (int?)l.ProductId).FirstOrDefault() : null);
            string rawName = rawProdId is int rp && rp > 0
                ? db.Products.Where(p => p.Id == rp).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-"
                : "-";

            string whDisplay = "-";
            if (it.Allocations != null && it.Allocations.Count > 0)
            {
                whDisplay = string.Join(" + ", it.Allocations.Select(a => a.WarehouseId).Distinct()
                    .Select(wid => whNames.TryGetValue(wid, out var wn) ? wn : $"مخزن #{wid}"));
            }
            else if (lotShip != null)
            {
                int? recWh = db.Shipments.Where(s => s.Id == lotShip.Value).Select(s => s.ReceivingWarehouseId).FirstOrDefault() ?? defaultRawWh;
                if (recWh != null && whNames.TryGetValue(recWh.Value, out var wn)) whDisplay = wn;
            }

            string srcUnit = string.IsNullOrWhiteSpace(it.SourceUnit) ? "كجم" : it.SourceUnit.Trim();
            double rawKg = it.SourceQtyKg > 0 ? (double)it.SourceQtyKg : (it.RawRequiredQtyKg > 0 ? (double)it.RawRequiredQtyKg : it.PlannedQtyKg);
            string srcDraw = (srcUnit != "كجم" && it.SourceQtyInUnit > 0)
                ? $"{it.SourceQtyInUnit:0.##} {srcUnit}\n({rawKg:N1} كجم)"
                : $"{rawKg:N1} كجم";

            m.Items.Add(new ItemRow
            {
                RowNo = n++,
                CustomerId = it.CustomerId,
                CustomerName = Cust(it.CustomerId) ?? "-",
                ShipmentNo = shipDisplay,
                LotCode = lotDisplay,
                RawName = rawName,
                ProductName = Prod(it.ProductId),
                PackName = it.PackagingTypeId != null
                    ? db.PackagingTypes.Where(p_ => p_.Id == it.PackagingTypeId).Select(p_ => p_.PackageNameAr).FirstOrDefault() ?? "-"
                    : "-",
                Cartons = it.PlannedCartons,
                QtyKg = it.PlannedQtyKg,
                Date = UiFormat.D(it.ScheduledDate),
                LineName = db.ProductionLines.Where(l => l.Id == (it.SuggestedLineId ?? plan.LineId ?? 0)).Select(l => l.LineNameAr).FirstOrDefault() ?? "—",
                ShiftName = db.Shifts.Where(s => s.Id == (it.SuggestedShiftId ?? plan.ShiftId ?? 0)).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "-",
                WarehouseName = whDisplay,
                SourceDrawText = srcDraw
            });
        }
        tx?.Commit();
        return m;
    }
}
