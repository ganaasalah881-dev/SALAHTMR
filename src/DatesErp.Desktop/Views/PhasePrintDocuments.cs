using System.Windows.Documents;
using DatesErp.Desktop.Printing;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views;

public static class PhasePrint
{
    // Compatibility/default route intentionally remains the previous template.
    public static FixedDocument Build(PhaseDocModel m) => Build(m, PrintTemplate.Previous);

    public static FixedDocument Build(PhaseDocModel m, PrintTemplate template)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (string.IsNullOrWhiteSpace(m.CompanyNameAr)) m.CompanyNameAr = CompanyIdentity.NameAr;
        if (string.IsNullOrWhiteSpace(m.CompanyNameEn)) m.CompanyNameEn = CompanyIdentity.NameEn;
        if (string.IsNullOrWhiteSpace(m.CompanyAddress)) m.CompanyAddress = CompanyIdentity.Address;
        if (string.IsNullOrWhiteSpace(m.CompanyPhone)) m.CompanyPhone = CompanyIdentity.Phone;
        if (string.IsNullOrWhiteSpace(m.CompanyTaxNumber)) m.CompanyTaxNumber = CompanyIdentity.TaxNumber;
        m.LogoBytes ??= CompanyIdentity.LogoBytes;
        if (string.IsNullOrWhiteSpace(m.CompanyDepartmentEn))
            m.CompanyDepartmentEn = m.DocTitle.Contains("جودة", StringComparison.OrdinalIgnoreCase) || m.DocTitle.Contains("QC", StringComparison.OrdinalIgnoreCase)
                ? "Quality Assurance & Lab"
                : m.DocTitle.Contains("خطة", StringComparison.OrdinalIgnoreCase)
                    ? "Production & Planning Dept"
                    : m.DocTitle.Contains("WFG", StringComparison.OrdinalIgnoreCase) || m.DocTitle.Contains("إنتاج تام", StringComparison.OrdinalIgnoreCase)
                        ? "Finished Goods Transfer"
                        : m.DocTitle.Contains("استلام", StringComparison.OrdinalIgnoreCase)
                            ? "Official Inbound Receipt"
                            : "Manufacturing Operations";
        var spec = PrintSchema.FromPhase(m);
        return template switch
        {
            PrintTemplate.Previous => PrintRenderer.Build(spec),
            PrintTemplate.Modern => ModernPrintRenderer.Build(spec),
            _ => throw new ArgumentOutOfRangeException(nameof(template))
        };
    }
    public static void ExportPdf(PhaseDocModel m,string path)
    {
        // Build includes m.MainTitle, m.SecondTitle, m.SecondColumns and m.SecondRows via the shared schema.
        // No separate PDF layout, no omitted notes and no 46-character truncation.
        PrintRenderer.ExportPdf(Build(m),path,$"{m.DocTitle} {m.DocNo}");
    }
}
