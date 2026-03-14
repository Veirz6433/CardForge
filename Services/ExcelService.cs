using ExcelDataReader;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;

namespace BulkImageGenerator.Services
{
    /// <summary>
    /// Parses .xls and .xlsx files into a structured list of row dictionaries
    /// using ExcelDataReader — a fast, dependency-free, fully offline parser.
    ///
    /// The first row of the first sheet is treated as the column header row.
    /// Each subsequent row becomes a Dictionary&lt;string, string&gt;
    /// where keys are column headers and values are cell values as strings.
    ///
    /// IMPORTANT: ExcelDataReader requires System.Text.Encoding.CodePages to be
    /// registered for correct parsing of legacy .xls (BIFF) files.
    /// Call ExcelService.RegisterEncodings() once at application startup (App.xaml.cs).
    /// </summary>
    public sealed class ExcelService
    {
        /// <summary>
        /// Must be called ONCE at application startup (in App.xaml.cs or Program.cs).
        /// Registers extended code page encodings required by ExcelDataReader
        /// for .xls (legacy binary Excel) support on .NET Core / .NET 5+.
        /// Without this, reading .xls files will throw an EncoderFallbackException.
        /// </summary>
        public static void RegisterEncodings()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>
        /// Reads the first worksheet of the Excel file at <paramref name="filePath"/>
        /// and returns all data rows as a list of column→value dictionaries.
        ///
        /// Rules:
        ///   - Empty rows are skipped.
        ///   - Cells with no value are stored as empty string "".
        ///   - Duplicate column headers are disambiguated by appending "_2", "_3", etc.
        ///   - Numeric cells (e.g. dates stored as doubles) are converted via .ToString().
        /// </summary>
        /// <param name="filePath">Absolute path to the .xls or .xlsx file.</param>
        /// <returns>A list of row dictionaries. Returns an empty list on failure.</returns>
        /// <exception cref="FileNotFoundException">If the file does not exist.</exception>
        /// <exception cref="InvalidOperationException">If the file cannot be parsed.</exception>
        public List<Dictionary<string, string>> ParseFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Excel file not found: {filePath}", filePath);

            var results = new List<Dictionary<string, string>>();

            // Open with FileShare.Read so the file can be open in Excel simultaneously.
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // ExcelReaderFactory.CreateReader auto-detects .xls vs .xlsx format
            // by reading the file's magic bytes — no extension sniffing needed.
            using var reader = ExcelReaderFactory.CreateReader(stream);

            // AsDataSet() loads all sheets into a DataSet in one pass.
            // UseHeaderRow = true promotes the first row to DataColumn.ColumnName.
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration
                {
                    UseHeaderRow = true  // First row becomes column names.
                }
            });

            if (dataSet.Tables.Count == 0)
                return results; // Empty workbook.

            // Use the first worksheet only.
            DataTable sheet = dataSet.Tables[0];

            // Build a disambiguated list of column names to handle duplicate headers.
            var columnNames = BuildColumnNames(sheet);

            foreach (DataRow row in sheet.Rows)
            {
                // Skip rows where every cell is null or whitespace.
                if (IsRowEmpty(row)) continue;

                var rowDict = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase); // Case-insensitive key lookup

                for (int col = 0; col < sheet.Columns.Count; col++)
                {
                    string colName = columnNames[col];
                    string cellValue = row[col]?.ToString()?.Trim() ?? string.Empty;
                    rowDict[colName] = cellValue;
                }

                results.Add(rowDict);
            }

            return results;
        }

        /// <summary>
        /// Builds a list of unique column name strings from the DataTable.
        /// Handles duplicate header names by appending a numeric suffix.
        /// E.g. headers ["name", "name", "photo"] → ["name", "name_2", "photo"].
        /// </summary>
        private static List<string> BuildColumnNames(DataTable sheet)
        {
            var names  = new List<string>(sheet.Columns.Count);
            var seen   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (DataColumn col in sheet.Columns)
            {
                // DataColumn.ColumnName may contain the original header or "Column1"
                // if the cell was blank. Use a fallback.
                string raw = string.IsNullOrWhiteSpace(col.ColumnName)
                    ? $"Column{names.Count + 1}"
                    : col.ColumnName.Trim();

                if (!seen.TryGetValue(raw, out int count))
                {
                    seen[raw] = 1;
                    names.Add(raw);
                }
                else
                {
                    seen[raw] = count + 1;
                    names.Add($"{raw}_{count + 1}"); // e.g. "name_2"
                }
            }

            return names;
        }

        /// <summary>
        /// Returns true if every cell in the row is null or whitespace.
        /// Used to skip completely blank rows in the spreadsheet.
        /// </summary>
        private static bool IsRowEmpty(DataRow row)
        {
            foreach (var item in row.ItemArray)
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.ToString()))
                    return false;
            }
            return true;
        }
    }
}
