using System.Text.RegularExpressions;
using Flujo.Core;
using Flujo.Documents;

namespace Flujo.Quality;

public sealed class QualityChecker : IQualityChecker
{
    private static readonly Regex ParentheticalYear = new("\\(((?:19|20)\\d{2})[a-z]?\\)", RegexOptions.CultureInvariant);
    private static readonly Regex NumberedHeading = new("^\\d+(?:\\.\\d+)*\\.?\\s+\\S", RegexOptions.CultureInvariant);

    public QualityReport Check(QualityInput input)
    {
        var findings = new List<QualityFinding>();
        var rules = new HashSet<string>(input.Manifest.CheckableRules, StringComparer.OrdinalIgnoreCase);
        if (rules.Contains("required_sections"))
        {
            foreach (var section in input.Manifest.RequiredSections)
            {
                var present = input.Inspection.Headings.Any(heading => heading.Text.Contains(section, StringComparison.OrdinalIgnoreCase))
                    || input.Inspection.Sheets.Any(sheet => sheet.Contains(section, StringComparison.OrdinalIgnoreCase))
                    || input.Inspection.Text.Contains(section, StringComparison.OrdinalIgnoreCase);
                if (!present)
                {
                    findings.Add(new QualityFinding
                    {
                        Code = "required_sections",
                        Message = "Falta la sección requerida: " + section + "."
                    });
                }
            }
        }

        if (rules.Contains("required_markers"))
        {
            var pending = input.Mode == GenerationMode.Regenerate
                ? input.Inspection.Markers.Where(marker => input.SectionIds.Contains(marker, StringComparer.OrdinalIgnoreCase))
                : input.Inspection.Markers;
            foreach (var marker in pending)
            {
                findings.Add(new QualityFinding
                {
                    Code = "required_markers",
                    Message = "El marcador {{" + marker + "}} sigue sin rellenar."
                });
            }
        }

        if (rules.Contains("no_invented_references"))
        {
            var corpus = input.TaskText + "\n" + string.Join("\n", input.Manifest.AllowedReferences);
            foreach (Match match in ParentheticalYear.Matches(input.Inspection.Text))
            {
                var year = match.Groups[1].Value;
                if (!corpus.Contains(year, StringComparison.Ordinal))
                {
                    findings.Add(new QualityFinding
                    {
                        Code = "no_invented_references",
                        Message = "El año " + year + " no está en el material de la tarea ni en las referencias permitidas."
                    });
                }
            }
        }

        if (rules.Contains("heading_numbering"))
        {
            foreach (var heading in input.Inspection.Headings)
            {
                if (!NumberedHeading.IsMatch(heading.Text.Trim()))
                {
                    findings.Add(new QualityFinding
                    {
                        Code = "heading_numbering",
                        Message = "El título no está numerado: " + heading.Text + "."
                    });
                }
            }
        }

        if (input.Manifest.PreservedFormulas.Count > 0)
        {
            foreach (var expected in input.Manifest.PreservedFormulas)
            {
                var actual = input.Inspection.Formulas.FirstOrDefault(formula =>
                    formula.Sheet.Equals(expected.Sheet, StringComparison.OrdinalIgnoreCase) &&
                    formula.Cell.Equals(expected.Cell, StringComparison.OrdinalIgnoreCase));
                if (actual is null || !FormulasMatch(actual.Formula, expected.Formula))
                {
                    findings.Add(new QualityFinding
                    {
                        Code = "preserved_formulas",
                        Message = "La fórmula de " + expected.Sheet + "!" + expected.Cell + " no se conservó."
                    });
                }
            }
        }

        if (input.Manifest.ExportPdf && (string.IsNullOrWhiteSpace(input.PdfPath) || !File.Exists(input.PdfPath) || new FileInfo(input.PdfPath).Length == 0))
        {
            findings.Add(new QualityFinding
            {
                Code = "pdf",
                Message = "No se generó el PDF."
            });
        }

        return new QualityReport
        {
            Accepted = findings.Count == 0,
            Findings = findings
        };
    }

    public static bool FormulasMatch(string actual, string expected)
    {
        return Normalize(actual) == Normalize(expected) || Normalize(actual).Contains(Normalize(expected.TrimStart('=')), StringComparison.Ordinal);
    }

    private static string Normalize(string formula)
    {
        return formula
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace(";", ",", StringComparison.Ordinal)
            .ToUpperInvariant()
            .Replace("SUMA(", "SUM(", StringComparison.Ordinal);
    }
}
