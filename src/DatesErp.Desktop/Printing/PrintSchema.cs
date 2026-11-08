using DatesErp.Desktop.Views;

namespace DatesErp.Desktop.Printing;

public static class PrintSchema
{
    public static PrintSpec FromPhase(PhaseDocModel m)
    {
        if (m.Columns.Length == 0 && m.Rows.Count > 0 || m.SecondColumns.Length == 0 && m.SecondRows.Count > 0)
            throw new ArgumentException("بنود طباعة بلا عناوين أعمدة؛ لا يمكن إسقاطها من المستند.");
        // §التصميم المعتمد — النماذج عمودية افتراضياً؛ المستند الذي يحدد Landscape صراحةً (مثل خطة الإنتاج) يبقى أفقياً. لا يوجد انقلاب تلقائي.
        var spec=new PrintSpec {Company=m.CompanyNameAr??"",CompanyNameEn=m.CompanyNameEn??"",
            CompanyDepartmentEn=m.CompanyDepartmentEn??"",CompanyAddress=m.CompanyAddress??"",CompanyPhone=m.CompanyPhone??"",
            CompanyTaxNumber=m.CompanyTaxNumber??"",Logo=m.LogoBytes?.ToArray(),Title=m.DocTitle,Number=m.DocNo,
            Status=m.StatusAr,Landscape=m.Landscape,CapturedAt=m.CapturedAt};
        var fields=m.Info.ToList();
        void Pairs(string title,List<(string Label,string Value)> values,string style)
        {
            if(values.Count==0)return;
            var s=new PrintSection {Title=title,Style=style,Weights=new[]{1d,2d,1d,2d}};
            for(int i=0;i<values.Count;i+=2)
                s.Rows.Add(new[]{values[i].Label,values[i].Value,i+1<values.Count?values[i+1].Label:"",i+1<values.Count?values[i+1].Value:""});
            spec.Sections.Add(s);
        }
        Pairs("بيانات المستند",fields,"meta");
        if(m.Columns.Length>0)
        {
            spec.Sections.Add(new PrintSection {Title=m.MainTitle,Columns=m.Columns.ToArray(),Weights=m.ColumnWeights?.ToArray(),Rows=m.Rows.Select(r=>r.Select(PrintLayout.Format).ToArray()).ToList()});
            if(m.TableTotalsRow is { Length: > 0 })
            {
                if (m.TableTotalsRow.Length != m.Columns.Length)
                    throw new ArgumentException("صف إجمالي الجدول يجب أن يطابق عدد أعمدة النموذج.");
                spec.Sections.Add(new PrintSection {Style="total",Weights=m.ColumnWeights?.ToArray(),Rows=new(){m.TableTotalsRow.ToArray()}});
            }
        }
        if(m.SecondColumns.Length>0)spec.Sections.Add(new PrintSection {Title=m.SecondTitle,Columns=m.SecondColumns.ToArray(),Rows=m.SecondRows.Select(r=>r.Select(PrintLayout.Format).ToArray()).ToList()});
        foreach(var s in m.ExtraSections)spec.Sections.Add(new PrintSection {Title=s.Title,Columns=s.Columns.ToArray(),Rows=s.Rows.Select(r=>r.ToArray()).ToList(),Weights=s.Weights?.ToArray(),Style=s.Style});
        // بطاقات ملخص مستقلة؛ لا نعيد جمع قيم المبيعات أو النسب أو الوحدات المختلطة تلقائياً.
        if (m.Totals.Count > 0)
        {
            var kpis = new PrintSection { Style = "kpi", Weights = Enumerable.Repeat(1d, 8).ToArray() };
            for (int i = 0; i < m.Totals.Count; i += 4)
            {
                var cells = new string[8];
                for (int card = 0; card < 4 && i + card < m.Totals.Count; card++)
                {
                    cells[card * 2] = m.Totals[i + card].Label ?? "";
                    cells[card * 2 + 1] = m.Totals[i + card].Value ?? "";
                }
                kpis.Rows.Add(cells);
            }
            spec.Sections.Add(kpis);
        }
        if(!string.IsNullOrWhiteSpace(m.Notes))spec.Sections.Add(new PrintSection {Title="ملاحظات المستند",Rows=new(){new[]{m.Notes}}});
        if(m.Signatures.Count>0)spec.Sections.Add(new PrintSection {Title="التوقيعات والاستلام",Style="signature",Rows=new(){m.Signatures.Select(s=>$"{s}\nالاسم: ........................\nالتوقيع: .....................\nالتاريخ: ......................").ToArray()}});
        if(!string.IsNullOrWhiteSpace(m.FooterNote))spec.Sections.Add(new PrintSection {Title="بيان النسخة",Rows=new(){new[]{m.FooterNote}}});
        return spec;
    }
}
