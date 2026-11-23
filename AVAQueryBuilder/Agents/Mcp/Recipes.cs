using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AVAQueryBuilder.Agents.Mcp;

/// <summary>
/// Saved recipes, served as MCP prompts: short step-by-step plans for queries agents are often asked for, written with
/// the real tool names and arguments. Each step that calls a tool shows the call on a line of its own,
/// "→ tool {arguments}", so an agent can follow it as it is or adapt it to its own tables (the tests run every recipe
/// through the server, call by call, against the sample clinic database, so a recipe that stops working fails the
/// build). The defaults name the sample database's tables (samples/clinic.db): patients, providers and visits.
/// </summary>
[McpServerPromptType]
public static class Recipes
{
    /// <summary>The marker that starts a step's tool call in a recipe's text.</summary>
    public const string CallMarker = "→ ";

    [McpServerPrompt(Name = "question_to_query", Title = "A question in plain words, as a query")]
    [Description("Turns a question (\"active patients seen by cardiology this year, by city\") into a query on the diagram: finds the tables and columns, builds it, has the database check it, runs it for a count, and explains it.")]
    public static string QuestionToQuery(
        [Description("The question, in the person's words.")] string? question = null)
    {
        var q = string.IsNullOrWhiteSpace(question) ? "Which active patients does each provider have, with the provider's name?" : question.Trim();
        var r = new Recipe($"Answer this with a query: \"{q}\"");
        r.Step("See what's open and which database it's connected to (if it isn't, list_connections and connect).", "get_query", new { });
        r.Step("Find the tables the question is about.", "list_tables", new { });
        r.Step("Find where each thing the question names is kept (a name, a date, a status, a provider).", "find_columns", new { text = "provider" });
        r.Step("Read the main table: its columns, its keys, and the lookups its foreign keys suggest.", "describe_table", new { table = "patients" });
        r.Step("Build the query in one call. The call below shows the shape for the default question: use your own tables, columns and conditions.", "build_query", new
        {
            replace = true,
            query = new
            {
                table = "patients",
                columns = new[] { "id", "first_name", "last_name", "city" },
                aliases = new Dictionary<string, string> { ["first_name"] = "First Name", ["last_name"] = "Last Name" },
                lookups = new[] { new { table = "providers", on = "id", from = new[] { "provider_id" }, columns = new[] { "name" }, aliases = new Dictionary<string, string> { ["name"] = "Provider" }, join = "LEFT" } },
                filters = new[] { new { field = "status", op = "=", value = "active" } },
                sort = new[] { new { field = "LOOKUP_1.name" }, new { field = "last_name" } },
            },
        });
        r.Step("Have the database check the SQL.", "check_query", new { });
        r.Step("Run it: how many rows, which columns (the person sees the rows in the Results Grid).", "run_query", new { max_rows = 10 });
        r.Step("Show the diagram.", "render_view", new { });
        return r.Text("Explain the query in plain words (which rows, joined to what, filtered how, in what order) and say how many rows it returned. Offer to save it (save_query) or export the rows (export_results).");
    }

    [McpServerPrompt(Name = "counts_by_month", Title = "Counts by month")]
    [Description("Counts rows per month of a date (visits a month, admissions a month), newest first: derived Year and Month fields, grouped, counted and sorted.")]
    public static string CountsByMonth(
        [Description("The table (default \"visits\").")] string? table = null,
        [Description("The date column (default \"visit_date\").")] string? date_field = null)
    {
        var t = string.IsNullOrWhiteSpace(table) ? "visits" : table.Trim();
        var d = string.IsNullOrWhiteSpace(date_field) ? "visit_date" : date_field.Trim();
        var r = new Recipe($"Count {t} per month of {d}.");
        r.Step("Check the table's columns (the date may be stored as text, a date or a timestamp).", "describe_table", new { table = t });
        r.Step("Year and month as derived fields, grouped and counted, newest first.", "build_query", new
        {
            replace = true,
            query = new
            {
                table = t,
                derived = new[]
                {
                    new { field = d, derivation = "Year Part", alias = "Year" },
                    new { field = d, derivation = "Month Part", alias = "Month" },
                },
                group_by = new { fields = new[] { "Year", "Month" }, aggregates = new[] { new { function = "COUNT(*)", alias = "Count" } } },
                sort = new[] { new { field = "Year", direction = "DESC" }, new { field = "Month", direction = "DESC" } },
            },
        });
        r.Step("Check and run it.", "check_query", new { });
        r.Step(null, "run_query", new { max_rows = 24 });
        return r.Text("Say how many months it covers. To count by something else as well (a provider, a clinic), add it to the group_by fields with set_group_by.");
    }

    [McpServerPrompt(Name = "missing_related", Title = "Records with no related record")]
    [Description("Finds rows with no matching row in another table (patients with no visits, members with no claims): a LEFT lookup, and a filter that keeps the rows where the lookup found nothing.")]
    public static string MissingRelated(
        [Description("The table whose rows are checked (default \"patients\").")] string? table = null,
        [Description("The table that should have a matching row (default \"visits\").")] string? related = null,
        [Description("The related table's column that points back (default \"patient_id\").")] string? related_column = null,
        [Description("The checked table's column it points at (default \"id\").")] string? key = null)
    {
        var t = string.IsNullOrWhiteSpace(table) ? "patients" : table.Trim();
        var rel = string.IsNullOrWhiteSpace(related) ? "visits" : related.Trim();
        var rc = string.IsNullOrWhiteSpace(related_column) ? "patient_id" : related_column.Trim();
        var k = string.IsNullOrWhiteSpace(key) ? "id" : key.Trim();
        var r = new Recipe($"Find the {t} that have no {rel}.");
        r.Step("Confirm how the two tables are joined.", "describe_table", new { table = rel });
        r.Step($"Every {t} row, LEFT-joined to {rel}; the filter keeps those where the join found nothing (the related table's own column is NULL).", "build_query", new
        {
            replace = true,
            query = new
            {
                table = t,
                lookups = new[] { new { table = rel, on = rc, from = new[] { k }, columns = new[] { rc }, join = "LEFT" } },
                filters = new[] { new { field = $"LOOKUP_1.{rc}", op = "IS NULL" } },
                distinct = true,
            },
        });
        r.Step("Check and count.", "check_query", new { });
        r.Step(null, "run_query", new { max_rows = 20 });
        return r.Text($"Say how many {t} have no {rel}. If the count is surprising, ask whether some should be left out (inactive ones, for instance) and add a filter.");
    }

    [McpServerPrompt(Name = "find_duplicates", Title = "Possible duplicate records")]
    [Description("Finds values that occur more than once (patients with the same name and date of birth): grouped by those fields, counted, and kept where the count is above one.")]
    public static string FindDuplicates(
        [Description("The table (default \"patients\").")] string? table = null,
        [Description("The fields that should be unique together, separated by commas (default \"last_name, first_name, birth_date\").")] string? fields = null)
    {
        var t = string.IsNullOrWhiteSpace(table) ? "patients" : table.Trim();
        var f = (string.IsNullOrWhiteSpace(fields) ? "last_name, first_name, birth_date" : fields).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        var r = new Recipe($"Find {t} rows that share {string.Join(", ", f)}.");
        r.Step("Grouped by the fields, counted, kept where the count is over 1, most repeated first.", "build_query", new
        {
            replace = true,
            query = new
            {
                table = t,
                group_by = new
                {
                    fields = f,
                    aggregates = new[] { new { function = "COUNT(*)", alias = "Copies" } },
                    having = new[] { new { aggregate = "Copies", op = ">", value = "1" } },
                },
                sort = new[] { new { field = "Copies", direction = "DESC" } },
            },
        });
        r.Step("Check and count.", "check_query", new { });
        r.Step(null, "run_query", new { max_rows = 20 });
        return r.Text("Say how many sets of possible duplicates there are. Names are often spelled differently: offer to loosen it (UPPER CASE and Trim as derived fields, grouped by those).");
    }

    [McpServerPrompt(Name = "top_n", Title = "Top N by a total")]
    [Description("The top N of something by a total or count (providers by charges, clinics by visits): a lookup for the name, grouped, summed, sorted and limited.")]
    public static string TopN(
        [Description("How many (default 10).")] int? n = null)
    {
        var count = n is > 0 ? n.Value : 10;
        var r = new Recipe($"The top {count} providers by total charges.");
        r.Step("Visits, with the provider's name looked up, grouped by provider, charges summed and visits counted, highest first.", "build_query", new
        {
            replace = true,
            query = new
            {
                table = "visits",
                lookups = new[] { new { table = "providers", on = "id", from = new[] { "provider_id" }, columns = new[] { "name" }, join = "INNER" } },
                group_by = new
                {
                    fields = new[] { "LOOKUP_1.name" },
                    aggregates = new object[] { new { function = "SUM", field = "charge", alias = "Charges" }, new { function = "COUNT(*)", alias = "Visits" } },
                },
                sort = new[] { new { field = "Charges", direction = "DESC" } },
                limit = count,
            },
        });
        r.Step("Check and run it.", "check_query", new { });
        r.Step(null, "run_query", new { max_rows = count });
        r.Step("Show the diagram.", "render_view", new { });
        return r.Text("Say what the list is and offer to export it (export_results to .xlsx).");
    }

    [McpServerPrompt(Name = "date_range_report", Title = "A date-range report, kept to rerun")]
    [Description("A report between two dates (visits last quarter), kept as a steps file with ${from} and ${to} so the same report runs again next quarter with other dates.")]
    public static string DateRangeReport(
        [Description("The first date, yyyy-mm-dd (default \"2026-07-01\").")] string? from = null,
        [Description("The last date, yyyy-mm-dd (default \"2026-09-30\").")] string? to = null)
    {
        var a = string.IsNullOrWhiteSpace(from) ? "2026-07-01" : from.Trim();
        var b = string.IsNullOrWhiteSpace(to) ? "2026-09-30" : to.Trim();
        var r = new Recipe($"Visits from {a} to {b}, with the patient and provider, and a steps file to run it again with other dates.");
        r.Step("The report, in one undo step.", "build_query", new
        {
            replace = true,
            query = new
            {
                table = "visits",
                columns = new[] { "visit_date", "kind", "charge" },
                aliases = new Dictionary<string, string> { ["visit_date"] = "Date", ["kind"] = "Visit", ["charge"] = "Charge" },
                lookups = new object[]
                {
                    new { table = "patients", on = "id", from = new[] { "patient_id" }, columns = new[] { "last_name", "first_name" }, aliases = new Dictionary<string, string> { ["last_name"] = "Last Name", ["first_name"] = "First Name" }, join = "INNER" },
                    new { table = "providers", on = "id", from = new[] { "provider_id" }, columns = new[] { "name" }, aliases = new Dictionary<string, string> { ["name"] = "Provider" }, join = "LEFT" },
                },
                filters = new[] { new { field = "visit_date", op = "BETWEEN", value = a, value2 = b, cast = "DATE" } },
                sort = new[] { new { field = "visit_date" } },
            },
        });
        r.Step("Check and count.", "check_query", new { });
        r.Step(null, "run_query", new { max_rows = 10 });
        r.Step("Keep it as a steps file with the dates as parameters.", "save_steps", new
        {
            path = "reports/visits-between",
            overwrite = true,
            description = "Visits between two dates, with the patient and provider",
            steps = new object[]
            {
                new
                {
                    tool = "build_query",
                    args = new
                    {
                        replace = true,
                        query = new
                        {
                            table = "visits",
                            columns = new[] { "visit_date", "kind", "charge" },
                            lookups = new object[]
                            {
                                new { table = "patients", on = "id", from = new[] { "patient_id" }, columns = new[] { "last_name", "first_name" }, join = "INNER" },
                                new { table = "providers", on = "id", from = new[] { "provider_id" }, columns = new[] { "name" }, join = "LEFT" },
                            },
                            filters = new[] { new { field = "visit_date", op = "BETWEEN", value = "${from}", value2 = "${to}", cast = "DATE" } },
                            sort = new[] { new { field = "visit_date" } },
                        },
                    },
                },
            },
            parameters = new Dictionary<string, string> { ["from"] = a, ["to"] = b },
        });
        return r.Text("Say how many visits there were, and that run_steps with path \"reports/visits-between.avasteps.json\" and new from and to values builds the same report for another period.");
    }

    [McpServerPrompt(Name = "explain_saved_query", Title = "Explain a saved query")]
    [Description("Opens a saved query (.qry) and explains in plain words what it returns: its tables and joins, its filters, its grouping and order.")]
    public static string ExplainSavedQuery(
        [Description("The .qry file, relative to the working folder (default \"queries/active-patients.qry\").")] string? path = null)
    {
        var p = string.IsNullOrWhiteSpace(path) ? "queries/active-patients.qry" : path.Trim();
        var r = new Recipe($"Explain what {p} does.");
        r.Step("Find it if the path isn't certain.", "list_files", new { pattern = "*.qry", recursive = true });
        r.Step("Open it (it connects to the database it was saved with). If the open query has unsaved changes, ask the person first.", "open_query", new { path = p, discard_changes = true });
        r.Step("Read its entities and SQL.", "get_query", new { });
        r.Step("Have the database check it still works (tables may have changed since it was saved).", "check_query", new { });
        r.Step("Show the diagram.", "render_view", new { });
        return r.Text("Explain it as you would to a manager: which records it starts from, what it joins and why, which it keeps, how it groups and orders them, and what each column is. Point out anything that may not do what its name suggests (a LEFT join filtered on the lookup, a text date compared without a cast).");
    }

    [McpServerPrompt(Name = "port_query", Title = "Move a query to another database engine")]
    [Description("Writes the open query's SQL for another engine (SQL Server, PostgreSQL or SQLite), and, if the same tables exist there, connects to it and checks it.")]
    public static string PortQuery(
        [Description("SQL Server, PostgreSQL or SQLite (default \"PostgreSQL\").")] string? engine = null)
    {
        var e = string.IsNullOrWhiteSpace(engine) ? "PostgreSQL" : engine.Trim();
        var r = new Recipe($"Move the open query to {e}.");
        r.Step("The query as it is.", "get_query", new { });
        r.Step($"Its SQL written for {e}: the row limit, quoting, casts and date functions change.", "get_sql", new { engine = e });
        r.Step("If the person has a saved connection to a copy of the database on that engine, connect to it (the query's entities stay) and check it there.", "list_connections", new { });
        return r.Text($"Give the person the {e} SQL. If you connected to a {e} copy, say whether check_query passed there; if a table or column is named differently, change the query to match.");
    }

    private sealed class Recipe(string intro)
    {
        private readonly StringBuilder _text = new(intro + "\n\nFollow these steps in order. A line starting with \"" + CallMarker.Trim() + "\" is one tool call, with its arguments as JSON. If a step fails, read the error, fix the cause and carry on. Table and column names in these calls are the sample clinic database's: use the connected database's own.\n");
        private int _number;

        public void Step(string? says, string tool, object args)
        {
            _number++;
            _text.Append('\n').Append(_number).Append('.').Append(says != null ? " " : "");
            if (says != null) _text.Append(says);
            _text.Append("\n   ");
            _text.Append(CallMarker).Append(tool).Append(' ').Append(JsonSerializer.Serialize(args, Json.Options));
        }

        public string Text(string closing) => _text.Append("\n\n").Append(closing).ToString();
    }
}
