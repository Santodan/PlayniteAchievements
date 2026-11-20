using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// Developer-only test setup for linked files, hidden unless <see cref="Enabled"/> is on.
    /// Deliberately not localized. Creates a test game with four custom achievements, writes a
    /// sample file in every format, saves a mapping for each, links the CSV, applies it, and
    /// writes a report of what every format reads and would change.
    /// </summary>
    internal static class LinkedFileTestKit
    {
        /// <summary>Shows the developer menu item. Turn off before a release build.</summary>
        public static readonly bool Enabled = false;

        public const string GameName = "PA Linked File Test";

        private static readonly (string Id, string Name)[] Achievements =
        {
            ("ach_one", "First Steps"),
            ("ach_two", "Second Wind"),
            ("ach_three", "Third Time Lucky"),
            ("ach_four", "Four Leaf Clover")
        };

        public static string Run(
            IPlayniteAPI api,
            GameCustomDataStore store,
            AchievementOverridesService overrides,
            LinkedFileSyncService sync,
            LinkedFileSchemaStore schemas,
            string folder,
            ILogger logger)
        {
            Directory.CreateDirectory(folder);
            var report = new StringBuilder();
            report.AppendLine("Linked file test, " + DateTime.Now.ToString("g", CultureInfo.CurrentCulture));
            report.AppendLine("Folder: " + folder);
            report.AppendLine();

            var game = EnsureGame(api, folder);
            report.AppendLine("Test game: " + game.Name + " (" + game.Id + ")");

            // Every run starts from four locked achievements so the results repeat.
            store.Update(game.Id, record => record.LinkedFile = null, affectsSummaryData: false);
            overrides.SetCustomAchievements(
                game.Id,
                Achievements.Select(item => new CustomAchievementDefinition { Id = item.Id, DisplayName = item.Name }).ToList());

            var files = WriteSampleFiles(folder);
            foreach (var file in files)
            {
                file.Schema.Name = "Test " + file.Label;
                schemas.Save(file.Schema);
            }

            report.AppendLine();
            report.AppendLine("What each sample reads and would change (nothing is written by this part):");
            foreach (var file in files)
            {
                var preview = sync.Preview(game.Id, new LinkedFileLink { FilePath = file.Path, Schema = file.Schema });
                report.AppendLine();
                report.AppendLine("== " + file.Label + ": " + Path.GetFileName(file.Path));
                report.AppendLine("   read: " + preview.Read.Status + (preview.Read.Message != null ? " (" + preview.Read.Message + ")" : string.Empty));
                if (preview.Read.Success)
                {
                    report.AppendLine("   columns: " + string.Join(", ", preview.Read.Table.Columns));
                    report.AppendLine("   rows: " + preview.Read.Table.Rows.Count);
                }

                if (preview.Plan != null)
                {
                    report.AppendLine("   result: " + preview.Plan.Report.Summarize());
                    foreach (var message in preview.Plan.Report.Messages)
                    {
                        report.AppendLine("   note: " + message);
                    }

                    report.AppendLine("   expected: " + file.Expected);
                }
            }

            // Link the CSV and apply it, as the Apply button does.
            var csv = files.First();
            store.Update(
                game.Id,
                record => record.LinkedFile = new LinkedFileLink { FilePath = csv.Path, Schema = csv.Schema.Clone() },
                affectsSummaryData: false);
            var applied = sync.SyncGame(game.Id, LinkedFileSyncReason.UserRequest);
            report.AppendLine();
            report.AppendLine("Linked " + Path.GetFileName(csv.Path) + " and applied it: " + applied.Summarize());
            report.AppendLine("Expected: First Steps unlocked (14 Nov 2023), the other three locked.");

            var reportPath = Path.Combine(folder, "report.txt");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(true));
            logger?.Info("[LinkedFile] Test kit ran; report at " + reportPath);
            return reportPath;
        }

        private static Game EnsureGame(IPlayniteAPI api, string folder)
        {
            var existing = api.Database.Games.FirstOrDefault(candidate => string.Equals(candidate.Name, GameName, StringComparison.Ordinal));
            // The game is a dialog owned by its own process, so it counts as the game's window:
            // notifications wait until the game is in front, and clicking the dialog after saving
            // a file brings it there. A console would not do, since a terminal can host it under
            // another process.
            var lines = new[]
            {
                "The linked file test game is running.",
                string.Empty,
                "Edit and save a file in " + folder + ", then click this window: notifications wait until the game is in front.",
                string.Empty,
                "Click OK to stop the game."
            };
            var message = string.Join(" + [char]10 + ", lines.Select(line => "'" + line.Replace("'", "''") + "'"));
            var action = new GameAction
            {
                Name = "Play",
                Type = GameActionType.File,
                Path = "powershell.exe",
                Arguments = "-NoProfile -WindowStyle Hidden -Command \"Add-Type -AssemblyName System.Windows.Forms; " +
                            "[System.Windows.Forms.MessageBox]::Show((" + message + "), '" + GameName + "') | Out-Null\"",
                IsPlayAction = true
            };

            if (existing != null)
            {
                existing.GameActions = new ObservableCollection<GameAction> { action };
                existing.IsInstalled = true;
                api.Database.Games.Update(existing);
                return existing;
            }

            var game = new Game(GameName)
            {
                IsInstalled = true,
                GameActions = new ObservableCollection<GameAction> { action }
            };
            api.Database.Games.Add(game);
            return game;
        }

        private sealed class SampleFile
        {
            public string Label;
            public string Path;
            public LinkedFileSchema Schema;
            public string Expected;
        }

        private static LinkedFileSchema Mapping(LinkedFileFormat format, params (string Column, LinkedFileField Field)[] columns)
        {
            return LinkedFileSchema.Normalize(new LinkedFileSchema
            {
                Format = format,
                Columns = columns.Select(column => new LinkedFileColumnMapping { Column = column.Column, Field = column.Field }).ToList()
            });
        }

        private static List<SampleFile> WriteSampleFiles(string folder)
        {
            var files = new List<SampleFile>();
            var fileTime = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc).ToFileTimeUtc();

            string Write(string name, string text)
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, text, new UTF8Encoding(false));
                return path;
            }

            files.Add(new SampleFile
            {
                Label = "CSV",
                Path = Write("progress.csv", "id,name,done,when\r\nach_one,First Steps,true,1700000000\r\nach_two,Second Wind,false,\r\nach_three,Third Time Lucky,false,\r\nach_four,Four Leaf Clover,false,\r\n"),
                Schema = Mapping(LinkedFileFormat.Delimited,
                    ("id", LinkedFileField.ApiName), ("name", LinkedFileField.DisplayName),
                    ("done", LinkedFileField.Unlocked), ("when", LinkedFileField.UnlockTime)),
                Expected = "matched=4 unlocked=1"
            });

            var json = Mapping(LinkedFileFormat.Json,
                ("$key", LinkedFileField.ApiName), ("done", LinkedFileField.Unlocked), ("time", LinkedFileField.UnlockTime));
            files.Add(new SampleFile
            {
                Label = "JSON",
                Path = Write("progress.json", "{\r\n  \"ach_one\": { \"done\": true, \"time\": \"2024-05-01T10:00:00Z\" },\r\n  \"ach_three\": { \"done\": true }\r\n}\r\n"),
                Schema = json,
                Expected = "matched=2 unlocked=2 (an object keyed by id)"
            });

            var toml = Mapping(LinkedFileFormat.Toml, ("$key", LinkedFileField.ApiName), ("filetime", LinkedFileField.UnlockTime));
            toml.RootPath = "unlocked";
            files.Add(new SampleFile
            {
                Label = "TOML",
                Path = Write("progress.toml", "# Earned achievements\r\n[unlocked.ach_two]\r\nfiletime = " + fileTime.ToString(CultureInfo.InvariantCulture) + "\r\n"),
                Schema = toml,
                Expected = "matched=1 unlocked=1 at 3 Feb 2024 (Windows file time, read from the time alone)"
            });

            var xml = Mapping(LinkedFileFormat.Xml, ("@id", LinkedFileField.ApiName), ("@achieved", LinkedFileField.Unlocked));
            xml.RowElementPath = "achievements.achievement";
            files.Add(new SampleFile
            {
                Label = "XML",
                Path = Write("progress.xml", "<achievements>\r\n  <achievement id=\"ach_four\" achieved=\"1\"/>\r\n  <achievement id=\"ach_one\" achieved=\"0\"/>\r\n</achievements>\r\n"),
                Schema = xml,
                Expected = "matched=2 unlocked=1 (attribute columns)"
            });

            var ini = Mapping(LinkedFileFormat.Ini,
                ("$section", LinkedFileField.ApiName), ("Achieved", LinkedFileField.Unlocked), ("UnlockTime", LinkedFileField.UnlockTime));
            files.Add(new SampleFile
            {
                Label = "INI",
                Path = Write("progress.ini", "; one section per achievement\r\n[ach_one]\r\nAchieved=1\r\nUnlockTime=1700000000\r\n\r\n[ach_two]\r\nAchieved=0\r\n"),
                Schema = ini,
                Expected = "matched=2 unlocked=1"
            });

            var flat = Mapping(LinkedFileFormat.Ini, ("$key", LinkedFileField.ApiName), ("$value", LinkedFileField.UnlockTime));
            files.Add(new SampleFile
            {
                Label = "INI flat",
                Path = Write("earned.ini", "# earned: id = unix time\r\nach_three = 1700000500\r\n"),
                Schema = flat,
                Expected = "matched=1 unlocked=1 (one key per achievement, no sections)"
            });

            var xlsx = Mapping(LinkedFileFormat.Xlsx, ("Name", LinkedFileField.DisplayName), ("Done", LinkedFileField.Unlocked));
            var xlsxPath = Path.Combine(folder, "checklist.xlsx");
            File.WriteAllBytes(xlsxPath, BuildChecklistWorkbook());
            files.Add(new SampleFile
            {
                Label = "Excel",
                Path = xlsxPath,
                Schema = xlsx,
                Expected = "matched=4 unlocked=2 (matched by name; Y means unlocked)"
            });

            return files;
        }

        /// <summary>A one-sheet checklist: Name and a Y or N Done column.</summary>
        private static byte[] BuildChecklistWorkbook()
        {
            const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var rows = new[] { ("Name", "Done"), ("First Steps", "Y"), ("Second Wind", "N"), ("Third Time Lucky", "Y"), ("Four Leaf Clover", "N") };

            string Cell(string reference, string text) =>
                "<c r=\"" + reference + "\" t=\"inlineStr\"><is><t>" + System.Security.SecurityElement.Escape(text) + "</t></is></c>";

            var sheet = new StringBuilder("<worksheet xmlns=\"" + main + "\"><sheetData>");
            for (var i = 0; i < rows.Length; i++)
            {
                var number = (i + 1).ToString(CultureInfo.InvariantCulture);
                sheet.Append("<row r=\"" + number + "\">").Append(Cell("A" + number, rows[i].Item1)).Append(Cell("B" + number, rows[i].Item2)).Append("</row>");
            }

            sheet.Append("</sheetData></worksheet>");

            using (var stream = new MemoryStream())
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    void Add(string name, string content)
                    {
                        using (var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)))
                        {
                            writer.Write(content);
                        }
                    }

                    Add("[Content_Types].xml",
                        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
                    Add("_rels/.rels",
                        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                    Add("xl/workbook.xml",
                        "<workbook xmlns=\"" + main + "\" xmlns:r=\"" + rel + "\"><sheets><sheet name=\"Checklist\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                    Add("xl/_rels/workbook.xml.rels",
                        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                    Add("xl/worksheets/sheet1.xml", sheet.ToString());
                }

                return stream.ToArray();
            }
        }
    }
}
